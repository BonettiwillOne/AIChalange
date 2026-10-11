namespace W2D1;

// Manual CLI demonstration: no index writes and no document embedding generation.
internal sealed class RagEvaluationService
{
    private readonly LlmAgent _agent;
    private readonly SemanticSearchService _search;
    private static readonly string[] Questions =
    {
        "What are async and await used for in C#?",
        "Why is async/await preferred over ContinueWith in C#?",
        "What is LINQ in C#?",
        "What is the difference between a class and an object in C#?",
        "What are nullable reference types in C#?",
        "What is pattern matching in C#?",
        "What are the main principles of object-oriented programming described in the documentation?",
        "What coding conventions are recommended for C#?",
        "What are some distinctive features of C#?",
        "How does a gasoline car engine work?"
    };

    public RagEvaluationService(LlmAgent agent, SemanticSearchService search)
    {
        _agent = agent;
        _search = search;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var answerCount = 0;
        var unknownCount = 0;
        var sourceCount = 0;
        var quoteCount = 0;
        var meaningCount = 0;
        var errorCount = 0;
        var antiHallucinationPassed = false;

        for (var i = 0; i < Questions.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var question = Questions[i];
            Console.WriteLine($"\nВопрос {i + 1}:\n{question}\n");
            var unknown = false;
            var generationRequested = false;
            var hasAnswer = false;
            var hasSources = false;
            var hasQuotes = false;
            var meaningPassed = false;
            var usedSources = new List<(int Number, DocumentChunk Chunk)>();
            var quotes = new List<EvaluationQuote>();
            string? error = null;
            try
            {
                // Same retrieval order and limits as /rag-cited, including its reranker fallback.
                var rewrittenQuery = await _agent.RewriteQueryAsync(question, cancellationToken);
                var initialResults = await _search.SearchAsync(
                    rewrittenQuery, SemanticSearchService.CandidateCount, cancellationToken);
                var candidates = initialResults
                    .Where(result => result.Similarity >= SemanticSearchService.SimilarityThreshold)
                    .Take(SemanticSearchService.FilteredResultLimit).ToArray();
                if (candidates.Length == 0)
                {
                    unknown = true;
                    unknownCount++;
                    Console.WriteLine(FormattableString.Invariant(
                        $"После threshold = {SemanticSearchService.SimilarityThreshold:F2} релевантных чанков нет. Финальный ответ LLM не генерировался."));
                    if (initialResults.Count > 0)
                        Console.WriteLine(FormattableString.Invariant(
                            $"Лучший similarity score: {initialResults.Max(result => result.Similarity):F6}"));
                }
                else
                {
                    var reranking = await _agent.RerankAsync(question, candidates, cancellationToken);
                    if (reranking.FallbackReason is not null)
                        Console.WriteLine($"Reranker: {reranking.FallbackReason} Использован порядок cosine similarity.");
                    var sources = reranking.CandidateNumbers.Take(LlmAgent.RerankedResultLimit)
                        .Select(number => candidates[number - 1]).ToArray();
                    generationRequested = true;
                    var answer = await _agent.AskCitedAsync(question, sources, cancellationToken);
                    hasAnswer = true;
                    answerCount++;
                    // AskCitedAsync has already checked that every cited number exists.
                    foreach (var number in answer.UsedSourceNumbers)
                    {
                        var chunk = sources[number - 1].Chunk;
                        usedSources.Add((number, chunk));
                    }
                    quotes.AddRange(answer.Quotes);
                    hasSources = usedSources.Count > 0 && usedSources.All(source =>
                        !string.IsNullOrWhiteSpace(source.Chunk.Source)
                        && !string.IsNullOrWhiteSpace(source.Chunk.ChunkId));
                    hasQuotes = quotes.Count > 0 && usedSources.All(source =>
                            quotes.Any(quote => quote.SourceNumber == source.Number && !string.IsNullOrWhiteSpace(quote.Text)))
                        && quotes.All(quote => !string.IsNullOrWhiteSpace(quote.Text)
                            && sources[quote.SourceNumber - 1].Chunk.Text.Contains(quote.Text, StringComparison.Ordinal));
                    if (hasSources) sourceCount++;
                    if (hasQuotes) quoteCount++;
                    // Always use a separate LLM request; do not infer meaning from retrieval similarity.
                    meaningPassed = await _agent.EvaluateCitationMeaningAsync(
                        question, answer.Text, quotes, cancellationToken);
                    if (meaningPassed) meaningCount++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                errorCount++;
            }

            Console.WriteLine($"Результат: {(unknown ? "НЕ ЗНАЮ" : hasAnswer ? "ANSWER" : "ОШИБКА")}");
            Console.WriteLine($"Источники: {(hasSources ? "ДА" : "НЕТ")}");
            Console.WriteLine($"Цитаты: {(hasQuotes ? "ДА" : "НЕТ")}");
            Console.WriteLine($"Проверка смысла: {(unknown ? "НЕ ПРИМЕНИМО" : meaningPassed ? "ДА" : "НЕТ")}");
            foreach (var source in usedSources)
                Console.WriteLine($"[{source.Number}] {source.Chunk.Source} | {source.Chunk.Section} | {source.Chunk.ChunkId}");
            if (hasAnswer && !meaningPassed)
                Console.WriteLine(error is null
                    ? "Evaluator: NO — основные утверждения не подтверждены извлечёнными цитатами."
                    : "Evaluator: проверка не завершена успешно (см. ошибку ниже).");
            if (error is not null)
                Console.WriteLine($"Ошибка проверки (FAIL): {error}");

            if (i == 9)
                antiHallucinationPassed = error is null && unknown && !generationRequested
                    && usedSources.Count == 0 && quotes.Count == 0;
        }

        Console.WriteLine("\n=== ИТОГ RAG EVALUATION ===\n");
        Console.WriteLine($"Всего вопросов: {Questions.Length}");
        Console.WriteLine($"Обычных RAG-ответов: {answerCount}");
        Console.WriteLine($"Ответов \"Не знаю\": {unknownCount}");
        Console.WriteLine($"Ответов с источниками: {sourceCount}");
        Console.WriteLine($"Ответов с цитатами: {quoteCount}");
        Console.WriteLine($"Ответов, прошедших смысловую проверку: {meaningCount} из {answerCount} обычных ответов");
        Console.WriteLine($"Ошибок выполнения/проверки: {errorCount}");
        Console.WriteLine($"Анти-галлюцинационный тест: {(antiHallucinationPassed ? "PASS" : "FAIL")}");
        var passed = errorCount == 0 && answerCount + unknownCount == Questions.Length
            && sourceCount == answerCount && quoteCount == answerCount
            && meaningCount == answerCount && antiHallucinationPassed;
        Console.WriteLine($"\nRAG EVALUATION: {(passed ? "PASS" : "FAIL")}");
    }
}
