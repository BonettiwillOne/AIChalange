using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace W2D1;

internal sealed class RagBenchmarkService
{
    private static readonly string[] Questions =
    {
        "What is encapsulation in C#?",
        "What is inheritance in C#?",
        "What is polymorphism in C#?",
        "What is the difference between a class and a struct in C#?",
        "What is async/await in C#?"
    };
    private readonly LlmAgent _agent;
    private readonly SemanticSearchService _cloudSearch;
    private readonly SemanticSearchService _localSearch;
    private readonly LocalRagSession _localSession;
    private sealed record Source(int Number, string SourceFile, string Title, string Section,
        string ChunkId, double Score);
    private sealed class Run
    {
        public int QuestionNumber { get; init; }
        public string Question { get; init; } = "";
        public int Repetition { get; init; }
        public string System { get; init; } = "";
        public double ElapsedMilliseconds { get; set; }
        public string Stage { get; set; } = "retrieval";
        public string Status { get; set; } = "pending";
        public string? Answer { get; set; }
        public string? Error { get; set; }
        public string? ErrorType { get; set; }
        public bool TimedOut { get; set; }
        public bool EmptyAnswer { get; set; }
        public bool GenerationRequested { get; set; }
        public string? RewrittenQuery { get; set; }
        public string? RerankerFallback { get; set; }
        public Source[] RetrievedSources { get; set; } = Array.Empty<Source>();
        public Source[] FinalSources { get; set; } = Array.Empty<Source>();
        public int[] CitedSourceNumbers { get; set; } = Array.Empty<int>();
    }

    public RagBenchmarkService(LlmAgent agent, SemanticSearchService cloudSearch,
        SemanticSearchService localSearch, LocalRagSession localSession)
    {
        _agent = agent;
        _cloudSearch = cloudSearch;
        _localSearch = localSearch;
        _localSession = localSession;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var runs = new List<Run>();
        var root = Directory.GetParent(new DocumentChunkingService().DocumentsDirectory)!.FullName;
        var reportPath = Path.Combine(root, "reports", "rag-benchmark.json");
        Console.WriteLine("RAG benchmark: 5 вопросов × 2 повтора × 2 системы = 20 отдельных прогонов.");
        Console.WriteLine("Local: /local-rag; Cloud: /rag-cited (rewrite, filter, reranking, citations). История и LLM-оценка качества не используются.");
        try
        {
            for (var questionIndex = 0; questionIndex < Questions.Length; questionIndex++)
            for (var repetition = 1; repetition <= 2; repetition++)
            foreach (var system in new[] { "local", "cloud" })
            {
                if (cancellationToken.IsCancellationRequested) return;
                var run = new Run
                {
                    QuestionNumber = questionIndex + 1, Question = Questions[questionIndex],
                    Repetition = repetition, System = system
                };
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    if (system == "local") await RunLocalAsync(run, cancellationToken);
                    else await RunCloudAsync(run, cancellationToken);
                    if (run.GenerationRequested && string.IsNullOrWhiteSpace(run.Answer))
                    {
                        run.EmptyAnswer = true;
                        run.Status = "empty-answer";
                    }
                    else if (run.Status == "pending") run.Status = "success";
                }
                catch (Exception ex)
                {
                    run.Error = ex.Message;
                    run.ErrorType = ex.GetType().Name;
                    run.TimedOut = !cancellationToken.IsCancellationRequested
                        && (ex is OperationCanceledException or TimeoutException
                            || ex.Message.Contains("тайм-аут", StringComparison.OrdinalIgnoreCase)
                            || ex.Message.Contains("истек", StringComparison.OrdinalIgnoreCase)
                            || ex.Message.Contains("истёк", StringComparison.OrdinalIgnoreCase));
                    run.EmptyAnswer = run.GenerationRequested
                        && (ex.Message.Contains("без текста", StringComparison.OrdinalIgnoreCase)
                            || ex.Message.Contains("не вернула итоговый ответ", StringComparison.OrdinalIgnoreCase));
                    run.Status = cancellationToken.IsCancellationRequested ? "cancelled"
                        : run.TimedOut ? "timeout" : run.EmptyAnswer ? "empty-answer" : "error";
                }
                finally
                {
                    stopwatch.Stop();
                    run.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                    runs.Add(run);
                }
                PrintRun(run);
                // Persist each completed attempt; report I/O and console output are outside the timer.
                await SaveAsync(reportPath, startedAt, runs);
            }
        }
        finally
        {
            // Also retain the partial report on Ctrl+C, independently of the cancelled request token.
            await SaveAsync(reportPath, startedAt, runs);
            PrintSummary(runs);
            Console.WriteLine($"Отчёт: {reportPath}");
        }
    }

    private async Task RunLocalAsync(Run run, CancellationToken token)
    {
        var sources = await _localSearch.SearchAsync(run.Question, 3, token);
        run.RetrievedSources = ToSources(sources);
        if (sources.Count == 0 || sources[0].Similarity < SemanticSearchService.SimilarityThreshold)
        {
            run.Answer = LocalRagSession.Unknown;
            run.Status = "no-context";
            return;
        }
        run.FinalSources = ToSources(sources);
        run.Stage = "generation";
        run.GenerationRequested = true;
        run.Answer = await _localSession.GenerateAnswerAsync(run.Question, token, sources);
    }

    private async Task RunCloudAsync(Run run, CancellationToken token)
    {
        run.Stage = "rewrite";
        run.RewrittenQuery = await _agent.RewriteQueryAsync(run.Question, token);
        run.Stage = "retrieval";
        var initial = await _cloudSearch.SearchAsync(run.RewrittenQuery,
            SemanticSearchService.CandidateCount, token);
        run.RetrievedSources = ToSources(initial);
        var candidates = initial.Where(result => result.Similarity >= SemanticSearchService.SimilarityThreshold)
            .Take(SemanticSearchService.FilteredResultLimit).ToArray();
        if (candidates.Length == 0)
        {
            run.Answer = "Не знаю: в локальной базе документов недостаточно релевантной информации.";
            run.Status = "no-context";
            return;
        }
        run.Stage = "reranking";
        var ranking = await _agent.RerankAsync(run.Question, candidates, token);
        run.RerankerFallback = ranking.FallbackReason;
        var sources = ranking.CandidateNumbers.Take(LlmAgent.RerankedResultLimit)
            .Select(number => candidates[number - 1]).ToArray();
        run.FinalSources = ToSources(sources);
        run.Stage = "generation";
        run.GenerationRequested = true;
        var answer = await _agent.AskCitedAsync(run.Question, sources, token);
        run.Answer = answer.Text;
        run.CitedSourceNumbers = answer.UsedSourceNumbers.ToArray();
    }

    private static Source[] ToSources(IReadOnlyList<SemanticSearchResult> sources) =>
        sources.Select((source, index) => new Source(index + 1, source.Chunk.Source,
            source.Chunk.Title, source.Chunk.Section, source.Chunk.ChunkId, source.Similarity)).ToArray();

    private static void PrintRun(Run run)
    {
        Console.WriteLine(FormattableString.Invariant(
            $"\nВопрос {run.QuestionNumber}, повтор {run.Repetition}, {run.System}: {run.ElapsedMilliseconds:F1} мс; {run.Status}\n{run.Question}"));
        Console.WriteLine("Ответ: " + (run.Answer ?? "[ответ не получен]"));
        if (run.Error is not null) Console.WriteLine($"Ошибка ({run.Stage}): {run.Error}");
        if (run.RerankerFallback is not null) Console.WriteLine("Reranker fallback: " + run.RerankerFallback);
        Console.WriteLine("Найденные источники:");
        foreach (var source in run.RetrievedSources)
            Console.WriteLine(FormattableString.Invariant(
                $"[{source.Number}] {source.Title} | {source.SourceFile} | {source.Section} | {source.ChunkId} | score={source.Score:F6}"));
        Console.WriteLine("Финальные источники: " + string.Join(", ", run.FinalSources.Select(source => $"[{source.Number}] {source.ChunkId}")));
    }

    private static void PrintSummary(List<Run> runs)
    {
        Console.WriteLine("\nВопрос | Повтор | Local, мс (статус) | Cloud, мс (статус)");
        for (var question = 1; question <= Questions.Length; question++)
        for (var repetition = 1; repetition <= 2; repetition++)
        {
            string Cell(string system)
            {
                var run = runs.FirstOrDefault(item => item.QuestionNumber == question
                    && item.Repetition == repetition && item.System == system);
                return run is null ? "не выполнен" : FormattableString.Invariant($"{run.ElapsedMilliseconds:F1} ({run.Status})");
            }
            Console.WriteLine($"{question} | {repetition} | {Cell("local")} | {Cell("cloud")}");
        }
        foreach (var system in new[] { "local", "cloud" })
        {
            var attempts = runs.Where(run => run.System == system).ToArray();
            var successful = attempts.Where(run => run.Status == "success").ToArray();
            Console.WriteLine(FormattableString.Invariant(
                $"{system}: завершено {attempts.Length}/10; успешных ответов {successful.Length}; среднее всех попыток: {(attempts.Length == 0 ? 0 : attempts.Average(run => run.ElapsedMilliseconds)):F1} мс."));
            Console.WriteLine(successful.Length == 0 ? "Среднее успешных ответов: нет данных."
                : FormattableString.Invariant($"Среднее успешных ответов: {successful.Average(run => run.ElapsedMilliseconds):F1} мс."));
        }
    }

    private static async Task SaveAsync(string path, DateTimeOffset startedAt, List<Run> runs)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            StartedAt = startedAt, UpdatedAt = DateTimeOffset.UtcNow,
            ExpectedRunCount = 20, CompletedRunCount = runs.Count,
            LocalPipeline = "/local-rag", CloudPipeline = "/rag-cited",
            LocalEmbeddingModel = OllamaEmbeddingService.Model,
            LocalGenerationModel = OllamaChatService.Model,
            LocalIndex = "index/" + LocalRagSession.IndexFileName,
            CloudEmbeddingModel = DocumentEmbeddingService.Model, CloudIndex = "index/rag-index.json",
            SimilarityThreshold = SemanticSearchService.SimilarityThreshold,
            Timing = "Stopwatch: retrieval and generation, including cloud rewrite/reranking; excludes report and result printing",
            QualityEvaluation = "Manual only", Runs = runs,
            Averages = new[] { "local", "cloud" }.Select(system => new
            {
                System = system,
                AllAttemptsMilliseconds = runs.Any(run => run.System == system)
                    ? (double?)runs.Where(run => run.System == system).Average(run => run.ElapsedMilliseconds) : null,
                SuccessfulAnswersMilliseconds = runs.Any(run => run.System == system && run.Status == "success")
                    ? (double?)runs.Where(run => run.System == system && run.Status == "success").Average(run => run.ElapsedMilliseconds) : null
            })
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
