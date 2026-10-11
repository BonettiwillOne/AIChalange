using System.Text;
using ModelContextProtocol.Protocol;
using W2D1;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;
// В Windows ReadFile при CP_UTF8 может заменять кириллицу на NUL.
// Для консоли читаем Unicode напрямую; перенаправленный stdin остаётся UTF-8.
if (OperatingSystem.IsWindows() && !Console.IsInputRedirected)
    Console.SetIn(new WindowsConsoleReader());
using var agent = new LlmAgent();
var mcpClient = new McpClientService();
var chunkingService = new DocumentChunkingService();
using var embeddingService = new DocumentEmbeddingService();
var localIndexService = new LocalIndexService(chunkingService, embeddingService);
var semanticSearchService = new SemanticSearchService(embeddingService);
Console.WriteLine("Локальный RAG: /local-rag-index — построить индекс; /local-rag [вопрос] — документация через Ollama.");
Console.WriteLine("Диагностика языка вопроса: /local-rag-compare — два ответа по одинаковым Top 3 английского поиска.");
Console.WriteLine("Оптимизация локальной LLM: /local-opt-benchmark — BASELINE/OPTIMIZED, метрики и JSON-отчёт.");
Console.WriteLine("Сводка оптимизации: /local-opt-summary — таблица и ответы из сохранённого отчёта, без вызова модели.");
Console.WriteLine("Учебное сравнение RAG: /rag-benchmark — 20 прогонов Local/Cloud, время, ответы и JSON-отчёт.");
Console.WriteLine("Локальный чат: /local-chat — Qwen2.5 3B через Ollama, генерация локально.");
Console.WriteLine("RAG-чат: /rag-chat — отдельный диалог с источниками и памятью задачи.");
Console.WriteLine("Проверка RAG: /rag-eval — 10 вопросов, источники, цитаты и смысловая LLM-проверка.");
Console.WriteLine("Цитаты и источники: /rag-cited <вопрос> — улучшенный RAG с ссылками и дословными цитатами.");
Console.WriteLine("Сравнение RAG: /rag-improved-compare <вопрос> — базовый и улучшенный RAG.");
Console.WriteLine("Reranking: /rag-rerank <вопрос> — rewrite, similarity filter, LLM reranker, Top 3.");
Console.WriteLine("Фильтрация: /rag-filter <вопрос> — rewrite, Top 10 и similarity >= 0.50, максимум 5.");
Console.WriteLine("Сравнение: /rag-compare <вопрос> — ответы без RAG и с RAG.");
Console.WriteLine("Поиск: /rag-search <вопрос> — Top 5 чанков по cosine similarity.");
Console.WriteLine("Индекс: /build-index — embeddings всех чанков и сохранение index/rag-index.json.");
Console.WriteLine("Embeddings: /embed-docs — по 3 чанка fixed и structure.");
Console.WriteLine("Документы: /chunk-docs — сравнение fixed и structure chunking.");
agent.Progress += Console.WriteLine;
Console.WriteLine("Инварианты: /invariants; /invariant add <category> | <rule> | <reason>.");
Console.WriteLine("Управление: /invariant remove <id>; /invariant disable <id>; /invariant enable <id>.");
Console.WriteLine("Инварианты обязательны; /reset, /task clear, очистка памяти и переключение профиля их сохраняют.");
Console.WriteLine("Задача: /task; /task start <описание>; /task step <текст>; /task expect <текст>.");
Console.WriteLine("Подтверждения: /task approve-plan; /task complete-execution; /task validation pass; /task validation fail.");
Console.WriteLine("Этапы: /task next; /task revise. Пауза: /task pause; /task resume. Удаление: /task clear.");
Console.WriteLine("/task start заменяет текущую задачу. /reset и очистка памяти сохраняют Task State.");
Console.WriteLine("Профиль: /profile; /profile set key=value; /profile remove key; /profile clear.");
Console.WriteLine("Профили: /profiles; /profile create имя; /profile switch имя; /profile delete имя.");
Console.WriteLine("Профили и активный выбор сохраняются между запусками; /reset и /clear all их не сбрасывают.");
Console.WriteLine("Память: /remember working key=value; /remember long key=value.");
Console.WriteLine("Просмотр: /memory short|working|long|all. Очистка: /clear short|working|long|all.");
Console.WriteLine("/reset сохраняет долговременную память. Summary и автоизвлечение facts отключены.");
Console.WriteLine(agent.HistoryStatus);
Console.WriteLine(agent.StrategyStatus);
Console.WriteLine("Стратегии: /strategy sliding|facts|branching. Память: /facts.");
Console.WriteLine("Окно Sliding: /sliding N (включая новый запрос); /sliding — текущий размер.");
Console.WriteLine("Ветки: /checkpoint имя; /branch ветка [checkpoint]; /switch ветка; /branches.");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};
Console.WriteLine("Введите запрос на русском языке. Новый диалог: /reset. Выход: /выход, /exit или Ctrl+C.");
Console.WriteLine("Проверка переполнения контекста: /overflow-test.");
Console.WriteLine("Инструменты MCP-сервера: /mcp-tools.");
Console.WriteLine("Курс валют через MCP: /currency <код>, например /currency USD.");
Console.WriteLine("Сводка курсов валют через MCP: /currency-summary.");
Console.WriteLine("Pipeline отчета о валюте через MCP: /currency-pipeline USD.");
Console.WriteLine("Оркестрация двух MCP-серверов: /orchestrate USD.");
while (!cancellation.IsCancellationRequested)
{
    Console.Write("Вы: ");
    var request = Console.ReadLine();
    if (request is null || request.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase)
                        || request.Trim().Equals("/выход", StringComparison.OrdinalIgnoreCase)
                        || cancellation.IsCancellationRequested)
        break;
    if (string.IsNullOrWhiteSpace(request))
        continue;
    var commandParts = request.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (commandParts[0].Equals("/local-opt-summary", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 1)
        {
            Console.WriteLine("Команда не требует аргументов: /local-opt-summary.");
            continue;
        }
        await LocalOptSummary.RunAsync(cancellation.Token);
        continue;
    }
    if (commandParts[0].Equals("/local-opt-benchmark", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 1)
        {
            Console.WriteLine("Команда не требует аргументов: /local-opt-benchmark.");
            continue;
        }
        try
        {
            using var localEmbeddings = new OllamaEmbeddingService();
            using var benchmark = new LocalOptBenchmark();
            var localSearch = new SemanticSearchService(localEmbeddings.GenerateEmbeddingAsync,
                LocalRagSession.IndexFileName, OllamaEmbeddingService.Dimensions,
                "Постройте локальный индекс командой /local-rag-index.",
                embeddingModel: OllamaEmbeddingService.Model);
            await benchmark.RunAsync(localSearch, cancellation.Token);
        }
        catch (OperationCanceledException) { Console.WriteLine("Benchmark отменён; частичный отчёт сохранён."); }
        catch (Exception ex) { Console.WriteLine($"Ошибка local-opt-benchmark: {ex.Message}"); }
        continue;
    }
    if (commandParts[0].Equals("/rag-benchmark", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 1)
        {
            Console.WriteLine("Команда не требует аргументов: /rag-benchmark.");
            continue;
        }
        try
        {
            using var localEmbeddings = new OllamaEmbeddingService();
            using var ollama = new OllamaChatService();
            var localSearch = new SemanticSearchService(localEmbeddings.GenerateEmbeddingAsync,
                LocalRagSession.IndexFileName, OllamaEmbeddingService.Dimensions,
                "Постройте локальный индекс командой /local-rag-index.",
                embeddingModel: OllamaEmbeddingService.Model);
            await new RagBenchmarkService(agent, semanticSearchService, localSearch,
                new LocalRagSession(localSearch, ollama)).RunAsync(cancellation.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось выполнить benchmark или сохранить отчёт: {ex.Message}");
        }
        continue;
    }
    if (commandParts[0].Equals("/local-rag-compare", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 1)
        {
            Console.WriteLine("Команда не требует аргументов: /local-rag-compare.");
            continue;
        }
        try
        {
            using var localEmbeddings = new OllamaEmbeddingService();
            using var ollama = new OllamaChatService();
            var localSearch = new SemanticSearchService(localEmbeddings.GenerateEmbeddingAsync,
                LocalRagSession.IndexFileName, OllamaEmbeddingService.Dimensions,
                "Постройте локальный индекс командой /local-rag-index.",
                embeddingModel: OllamaEmbeddingService.Model);
            await LocalRagComparison.RunAsync(localSearch, ollama, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Сравнение локального RAG отменено.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка сравнения локального RAG: {ex.Message}");
        }
        continue;
    }
    if (commandParts[0].Equals("/local-rag-index", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 1)
        {
            Console.WriteLine("Команда не требует аргументов: /local-rag-index.");
            continue;
        }
        try
        {
            using var localEmbeddings = new OllamaEmbeddingService();
            var indexBuilder = new LocalIndexService(chunkingService,
                localEmbeddings.GenerateEmbeddingAsync, LocalRagSession.IndexFileName,
                maxChunkCharacters: 1000, embeddingModel: OllamaEmbeddingService.Model);
            Console.WriteLine($"Локальная индексация всех документов: {OllamaEmbeddingService.Model}.");
            var result = await indexBuilder.BuildIndexAsync(Console.WriteLine, cancellation.Token);
            Console.WriteLine($"Документов: {result.DocumentCount}; fixed: {result.FixedChunkCount}; structure: {result.StructureChunkCount}.");
            Console.WriteLine($"Индекс: {result.FilePath}; чанков: {result.IndexedChunkCount}; размерность: {result.EmbeddingDimensions}; байт: {result.FileSizeBytes}.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Локальная индексация отменена.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка локальной индексации: {ex.Message}");
        }
        continue;
    }
    if (commandParts[0].Equals("/local-rag", StringComparison.OrdinalIgnoreCase))
    {
        using var localEmbeddings = new OllamaEmbeddingService();
        using var ollama = new OllamaChatService();
        var localSearch = new SemanticSearchService(localEmbeddings.GenerateEmbeddingAsync,
            LocalRagSession.IndexFileName, OllamaEmbeddingService.Dimensions,
            "Постройте локальный индекс командой /local-rag-index.",
            embeddingModel: OllamaEmbeddingService.Model);
        var session = new LocalRagSession(localSearch, ollama);
        var parts = request.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
            await session.AnswerAsync(parts[1].Trim(), cancellation.Token);
        else
            await session.RunAsync(cancellation.Token);
        continue;
    }
    if (commandParts[0].Equals("/local-chat", StringComparison.OrdinalIgnoreCase))
    {
        using var ollama = new OllamaChatService();
        await new LocalChatSession(ollama).RunAsync(cancellation.Token);
        Console.WriteLine("Локальный чат завершён. Возврат в основное меню.");
        continue;
    }
    if (commandParts[0].Equals("/rag-chat", StringComparison.OrdinalIgnoreCase))
    {
        await new RagChatSession(agent, semanticSearchService).RunAsync(cancellation.Token);
        Console.WriteLine("RAG-чат завершён.");
        continue;
    }
    if (commandParts[0].Equals("/rag-eval", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            await new RagEvaluationService(agent, semanticSearchService).RunAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("RAG evaluation отменена.\nRAG EVALUATION: FAIL");
        }
        Console.WriteLine();
        continue;
    }

    if (commandParts[0].Equals("/rag-cited", StringComparison.OrdinalIgnoreCase))
    {
        var question = request.TrimStart()[commandParts[0].Length..].Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            Console.WriteLine("Укажите вопрос: /rag-cited <вопрос>.");
            continue;
        }
        try
        {
            var rewrittenQuery = await agent.RewriteQueryAsync(question, cancellation.Token);
            var initialResults = await semanticSearchService.SearchAsync(
                rewrittenQuery, SemanticSearchService.CandidateCount, cancellation.Token);
            var candidates = initialResults
                .Where(result => result.Similarity >= SemanticSearchService.SimilarityThreshold)
                .Take(SemanticSearchService.FilteredResultLimit).ToArray();
            if (candidates.Length == 0)
            {
                Console.WriteLine("=== ОТВЕТ ===\n");
                Console.WriteLine("Не знаю: в локальной базе документов недостаточно релевантной информации для ответа на этот вопрос.");
                Console.WriteLine("Пожалуйста, уточните вопрос или добавьте подходящий источник в базу.");
                Console.WriteLine("\n=== АНТИ-ГАЛЛЮЦИНАЦИЯ ===\n");
                Console.WriteLine("Ответ LLM не генерировался.");
                Console.WriteLine(FormattableString.Invariant(
                    $"Причина: после фильтрации по similarity threshold = {SemanticSearchService.SimilarityThreshold:F2} не осталось релевантных чанков."));
                if (initialResults.Count > 0)
                    Console.WriteLine(FormattableString.Invariant(
                        $"Лучший найденный similarity score: {initialResults.Max(result => result.Similarity):F6}"));
                else
                    Console.WriteLine("Результатов поиска нет; лучший similarity score отсутствует.");
                Console.WriteLine(FormattableString.Invariant(
                    $"Используемый threshold: {SemanticSearchService.SimilarityThreshold:F2}\n"));
                continue;
            }
            var reranking = await agent.RerankAsync(question, candidates, cancellation.Token);
            if (reranking.FallbackReason is not null)
                Console.WriteLine($"Reranker: {reranking.FallbackReason} Использован исходный порядок по cosine similarity.");
            var sources = reranking.CandidateNumbers.Take(LlmAgent.RerankedResultLimit)
                .Select(number => candidates[number - 1]).ToArray();
            // No answer can have valid citations when the retrieval stage returns no sources.
            if (sources.Length == 0)
                throw new InvalidOperationException("После similarity filter нет источников для генерации ответа с ссылками.");
            var answer = await agent.AskCitedAsync(question, sources, cancellation.Token);
            Console.WriteLine("=== ОТВЕТ ===\n");
            Console.WriteLine(answer.Text);
            Console.WriteLine("\n=== ИСТОЧНИКИ ===\n");
            for (var i = 0; i < sources.Length; i++)
            {
                var source = sources[i];
                Console.WriteLine($"[{i + 1}]");
                Console.WriteLine($"Source: {source.Chunk.Source}\nSection: {source.Chunk.Section}\nChunkId: {source.Chunk.ChunkId}");
                Console.WriteLine(FormattableString.Invariant($"Similarity: {source.Similarity:F6}\n"));
            }
            Console.WriteLine("=== ЦИТАТЫ ===\n");
            foreach (var quote in answer.Quotes)
                Console.WriteLine($"[{quote.SourceNumber}] \"{quote.Text}\"");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Генерация ответа с источниками отменена.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось выполнить /rag-cited: {ex.Message}");
        }
        Console.WriteLine();
        continue;
    }

    if (commandParts[0].Equals("/rag-improved-compare", StringComparison.OrdinalIgnoreCase))
    {
        var question = request.TrimStart()[commandParts[0].Length..].Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            Console.WriteLine("Укажите вопрос: /rag-improved-compare <вопрос>.");
            continue;
        }
        Console.WriteLine("=== БАЗОВЫЙ RAG ===");
        try
        {
            var basicResults = await semanticSearchService.SearchAsync(question, cancellation.Token);
            var basicContext = string.Join("\n\n---\n\n", basicResults.Select(result => result.Chunk.Text));
            Console.WriteLine("Ответ:");
            try
            {
                Console.WriteLine(await agent.AskIndependentAsync(question, basicContext, cancellation.Token));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось получить ответ базового RAG: {ex.Message}");
            }
            Console.WriteLine("\nИспользованные чанки:");
            for (var i = 0; i < basicResults.Count; i++)
            {
                var result = basicResults[i];
                Console.WriteLine(FormattableString.Invariant(
                    $"{i + 1}. {result.Similarity:F6} | Source: {result.Chunk.Source} | Section: {result.Chunk.Section} | ChunkId: {result.Chunk.ChunkId}"));
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Сравнение отменено.");
            continue;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось выполнить базовый RAG: {ex.Message}");
        }

        Console.WriteLine("\n=== УЛУЧШЕННЫЙ RAG ===");
        try
        {
            var rewrittenQuery = await agent.RewriteQueryAsync(question, cancellation.Token);
            Console.WriteLine($"Переписанный запрос:\n{rewrittenQuery}\n");
            var initialResults = await semanticSearchService.SearchAsync(
                rewrittenQuery, SemanticSearchService.CandidateCount, cancellation.Token);
            var candidates = initialResults
                .Where(result => result.Similarity >= SemanticSearchService.SimilarityThreshold)
                .Take(SemanticSearchService.FilteredResultLimit).ToArray();
            Console.WriteLine($"До фильтрации: {initialResults.Count} результатов");
            Console.WriteLine($"После similarity filter: {candidates.Length} результатов");
            var reranking = await agent.RerankAsync(question, candidates, cancellation.Token);
            if (reranking.FallbackReason is not null)
                Console.WriteLine($"Reranker: {reranking.FallbackReason} Использован исходный порядок по cosine similarity.");
            var finalOrder = reranking.CandidateNumbers.Take(LlmAgent.RerankedResultLimit).ToArray();
            var improvedResults = finalOrder.Select(number => candidates[number - 1]).ToArray();
            Console.WriteLine($"После reranking: {improvedResults.Length} результатов");
            // Empty string still activates context-only instructions; never reuse the basic context.
            var improvedContext = string.Join("\n\n---\n\n", improvedResults.Select(result => result.Chunk.Text));
            Console.WriteLine("\nОтвет:");
            try
            {
                Console.WriteLine(await agent.AskIndependentAsync(question, improvedContext, cancellation.Token));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось получить ответ улучшенного RAG: {ex.Message}");
            }
            Console.WriteLine("\nИспользованные чанки:");
            for (var i = 0; i < improvedResults.Length; i++)
            {
                var result = improvedResults[i];
                Console.WriteLine(FormattableString.Invariant(
                    $"{i + 1}. исходный similarity: {result.Similarity:F6} | Source: {result.Chunk.Source} | Section: {result.Chunk.Section} | ChunkId: {result.Chunk.ChunkId}"));
                Console.WriteLine($"было место {finalOrder[i]} → стало место {i + 1}");
            }
            if (improvedResults.Length == 0)
                Console.WriteLine("Нет чанков после similarity filter; в LLM передан пустой контекст.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Сравнение отменено.");
            continue;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось выполнить улучшенный RAG: {ex.Message}");
        }

        Console.WriteLine("\n=== ИТОГ СРАВНЕНИЯ ===");
        Console.WriteLine("Базовый RAG:\n- Top K: 5\n- Query rewrite: нет\n- Similarity filter: нет\n- Reranking: нет");
        Console.WriteLine($"\nУлучшенный RAG:\n- Initial Top K: {SemanticSearchService.CandidateCount}\n- Query rewrite: да");
        Console.WriteLine(FormattableString.Invariant(
            $"- Similarity threshold: {SemanticSearchService.SimilarityThreshold:F2}"));
        Console.WriteLine($"- Reranking: да\n- Final Top K: {LlmAgent.RerankedResultLimit}");
        Console.WriteLine();
        continue;
    }

    if (commandParts[0].Equals("/rag-rerank", StringComparison.OrdinalIgnoreCase))
    {
        var question = request.TrimStart()[commandParts[0].Length..].Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            Console.WriteLine("Укажите вопрос: /rag-rerank <вопрос>.");
            continue;
        }
        try
        {
            Console.WriteLine($"Исходный вопрос:\n{question}\n");
            var rewrittenQuery = await agent.RewriteQueryAsync(question, cancellation.Token);
            Console.WriteLine($"Переписанный запрос:\n{rewrittenQuery}\n");
            var results = await semanticSearchService.SearchAsync(
                rewrittenQuery, SemanticSearchService.CandidateCount, cancellation.Token);
            var candidates = results
                .Where(result => result.Similarity >= SemanticSearchService.SimilarityThreshold)
                .Take(SemanticSearchService.FilteredResultLimit).ToArray();
            Console.WriteLine("=== ДО RERANKING ===");
            for (var i = 0; i < candidates.Length; i++)
            {
                var result = candidates[i];
                Console.WriteLine(FormattableString.Invariant(
                    $"{i + 1}. {result.Similarity:F6} | Source: {result.Chunk.Source} | Section: {result.Chunk.Section} | ChunkId: {result.Chunk.ChunkId}"));
            }
            if (candidates.Length == 0)
                Console.WriteLine("Нет кандидатов после similarity filter. Запрос к reranker не выполняется.");

            var reranking = await agent.RerankAsync(question, candidates, cancellation.Token);
            Console.WriteLine("\n=== ПОСЛЕ RERANKING ===");
            if (reranking.FallbackReason is not null)
                Console.WriteLine($"Reranker: {reranking.FallbackReason} Использован исходный порядок по cosine similarity.");
            var finalOrder = reranking.CandidateNumbers.Take(LlmAgent.RerankedResultLimit).ToArray();
            Console.WriteLine($"Финальный Top 3: {finalOrder.Length} результатов.");
            for (var i = 0; i < finalOrder.Length; i++)
            {
                var originalPosition = finalOrder[i];
                var result = candidates[originalPosition - 1];
                Console.WriteLine(FormattableString.Invariant(
                    $"{i + 1}. Source: {result.Chunk.Source} | Section: {result.Chunk.Section} | ChunkId: {result.Chunk.ChunkId} | исходный similarity: {result.Similarity:F6}"));
                Console.WriteLine($"было место {originalPosition} → стало место {i + 1}");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Reranking отменён.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось выполнить поиск с reranking: {ex.Message}");
        }
        Console.WriteLine();
        continue;
    }

    if (commandParts[0].Equals("/rag-filter", StringComparison.OrdinalIgnoreCase))
    {
        var question = request.TrimStart()[commandParts[0].Length..].Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            Console.WriteLine("Укажите вопрос: /rag-filter <вопрос>.");
            continue;
        }
        try
        {
            Console.WriteLine($"Исходный вопрос:\n{question}\n");
            var rewrittenQuery = await agent.RewriteQueryAsync(question, cancellation.Token);
            Console.WriteLine($"Переписанный запрос:\n{rewrittenQuery}\n");
            var candidates = await semanticSearchService.SearchAsync(
                rewrittenQuery, SemanticSearchService.CandidateCount, cancellation.Token);
            var filtered = candidates
                .Where(result => result.Similarity >= SemanticSearchService.SimilarityThreshold)
                .Take(SemanticSearchService.FilteredResultLimit).ToArray();
            Console.WriteLine($"Результатов до фильтрации: {candidates.Count}");
            Console.WriteLine("Top 10 ДО фильтрации:");
            for (var i = 0; i < candidates.Count; i++)
            {
                var result = candidates[i];
                var status = result.Similarity < SemanticSearchService.SimilarityThreshold
                    ? "отброшен: ниже порога"
                    : filtered.Contains(result) ? "оставлен" : "отброшен: лимит Top 5";
                Console.WriteLine(FormattableString.Invariant(
                    $"Место: {i + 1}; similarity: {result.Similarity:F6}; {status}"));
                Console.WriteLine($"Source: {result.Chunk.Source}\nSection: {result.Chunk.Section}\nChunkId: {result.Chunk.ChunkId}\n");
            }
            Console.WriteLine(FormattableString.Invariant(
                $"Порог similarity: {SemanticSearchService.SimilarityThreshold:F2}"));
            Console.WriteLine($"Результатов после фильтрации: {filtered.Length}");
            for (var i = 0; i < filtered.Length; i++)
            {
                var result = filtered[i];
                Console.WriteLine(FormattableString.Invariant(
                    $"Место: {i + 1}; similarity: {result.Similarity:F6}"));
                Console.WriteLine($"Source: {result.Chunk.Source}\nSection: {result.Chunk.Section}\nChunkId: {result.Chunk.ChunkId}\n");
            }
            if (filtered.Length == 0)
                Console.WriteLine("Нет результатов с similarity не ниже порога.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Фильтрация отменена.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось выполнить фильтрацию: {ex.Message}");
        }
        Console.WriteLine();
        continue;
    }
    if (commandParts[0].Equals("/rag-compare", StringComparison.OrdinalIgnoreCase))
    {
        var question = request.TrimStart()[commandParts[0].Length..].Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            Console.WriteLine("Укажите вопрос: /rag-compare <вопрос>.");
            continue;
        }

        Console.WriteLine("=== БЕЗ RAG ===");
        try
        {
            Console.WriteLine(await agent.AskIndependentAsync(question, cancellationToken: cancellation.Token));
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Сравнение отменено.");
            continue;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось получить ответ без RAG: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("=== С RAG ===");
        try
        {
            var results = await semanticSearchService.SearchAsync(question, cancellation.Token);
            var context = string.Join("\n\n---\n\n", results.Select(result => result.Chunk.Text));
            Console.WriteLine(await agent.AskIndependentAsync(question, context, cancellation.Token));
            Console.WriteLine();
            Console.WriteLine("Использованные чанки:");
            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                Console.WriteLine($"{i + 1}. {result.Chunk.Source} | {result.Chunk.Section} | " +
                    $"{result.Chunk.ChunkId} | {result.Similarity:F6}");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Сравнение отменено.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось получить ответ с RAG: {ex.Message}");
        }
        Console.WriteLine();
        continue;
    }
    if (commandParts[0].Equals("/rag-search", StringComparison.OrdinalIgnoreCase))
    {
        var question = request.TrimStart()[commandParts[0].Length..].Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            Console.WriteLine("Укажите вопрос: /rag-search <вопрос>.");
            continue;
        }
        try
        {
            var results = await semanticSearchService.SearchAsync(question, cancellation.Token);
            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                var chunk = result.Chunk;
                Console.WriteLine($"Место: {i + 1}; similarity score: {result.Similarity:F6}");
                Console.WriteLine($"ChunkId: {chunk.ChunkId}");
                Console.WriteLine($"Source: {chunk.Source}");
                Console.WriteLine($"Section: {chunk.Section}");
                Console.WriteLine($"Strategy: {chunk.Strategy}");
                var preview = chunk.Text.Length > 300 ? chunk.Text[..300] + "…" : chunk.Text;
                Console.WriteLine($"Text: {preview}");
                Console.WriteLine();
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Семантический поиск отменён.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось выполнить семантический поиск: {ex.Message}");
        }
        continue;
    }
    if (commandParts[0].Equals("/build-index", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 1)
        {
            Console.WriteLine("Команда не требует аргументов: /build-index.");
            continue;
        }
        try
        {
            var result = await localIndexService.BuildIndexAsync(Console.WriteLine, cancellation.Token);
            Console.WriteLine($"Количество документов: {result.DocumentCount}");
            Console.WriteLine($"Количество fixed-чанков: {result.FixedChunkCount}");
            Console.WriteLine($"Количество structure-чанков: {result.StructureChunkCount}");
            Console.WriteLine($"Всего проиндексировано чанков: {result.IndexedChunkCount}");
            Console.WriteLine($"Размерность embeddings: {result.EmbeddingDimensions}");
            Console.WriteLine($"Файл индекса: {result.FilePath}");
            Console.WriteLine($"Размер JSON-файла: {result.FileSizeBytes:N0} байт");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Индексация отменена. Готовый индекс не опубликован.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось построить индекс: {ex.Message}");
        }
        Console.WriteLine();
        continue;
    }
    if (commandParts[0].Equals("/embed-docs", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 1)
        {
            Console.WriteLine("Команда не требует аргументов: /embed-docs.");
            continue;
        }
        try
        {
            var result = await chunkingService.ChunkDocumentsAsync(cancellation.Token);
            var samples = result.FixedChunks.Take(3).Concat(result.StructureChunks.Take(3)).ToArray();
            Console.WriteLine($"Модель: {DocumentEmbeddingService.Model}. Выбрано чанков: {samples.Length}.");
            for (var i = 0; i < samples.Length; i++)
            {
                Console.WriteLine($"Получение embedding: {samples[i].ChunkId}");
                var embedding = await embeddingService.GenerateEmbeddingAsync(samples[i].Text, cancellation.Token);
                samples[i] = samples[i] with { Embedding = embedding };
                Console.WriteLine($"ChunkId: {samples[i].ChunkId}");
                Console.WriteLine($"Strategy: {samples[i].Strategy}");
                Console.WriteLine($"Размерность embedding: {samples[i].Embedding.Length}");
                Console.WriteLine("Первые 5 чисел: " + string.Join(", ", samples[i].Embedding.Take(5)
                    .Select(value => value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture))));
                Console.WriteLine();
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Генерация embeddings отменена.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось получить embeddings: {ex.Message}");
        }
        Console.WriteLine();
        continue;
    }
    if (commandParts[0].Equals("/chunk-docs", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 1)
        {
            Console.WriteLine("Команда не требует аргументов: /chunk-docs.");
            continue;
        }
        try
        {
            var result = await chunkingService.ChunkDocumentsAsync(cancellation.Token);
            Console.WriteLine($"Найдено документов: {result.DocumentCount}");
            Console.WriteLine($"Чанков fixed: {result.FixedChunks.Count}");
            Console.WriteLine($"Чанков structure: {result.StructureChunks.Count}");
            foreach (var chunks in new[] { result.FixedChunks, result.StructureChunks })
            {
                foreach (var chunk in chunks.Take(3))
                {
                    Console.WriteLine();
                    Console.WriteLine($"ChunkId: {chunk.ChunkId}");
                    Console.WriteLine($"Source: {chunk.Source}");
                    Console.WriteLine($"Title: {chunk.Title}");
                    Console.WriteLine($"Section: {chunk.Section}");
                    Console.WriteLine($"Strategy: {chunk.Strategy}");
                    Console.WriteLine($"Длина Text: {chunk.Text.Length} символов");
                    Console.WriteLine("Text (первые 500 символов):");
                    var previewLength = Math.Min(500, chunk.Text.Length);
                    if (previewLength < chunk.Text.Length && char.IsHighSurrogate(chunk.Text[previewLength - 1]))
                        previewLength--;
                    Console.WriteLine(chunk.Text[..previewLength]);
                    if (previewLength < chunk.Text.Length)
                        Console.WriteLine("[...]");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Разбиение документов отменено.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось разбить документы: {ex.Message}");
        }
        Console.WriteLine();
        continue;
    }
    if (commandParts[0].Equals("/orchestrate", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 2 || commandParts[1].Length != 3 ||
            commandParts[1].Any(c => !((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))))
        {
            Console.WriteLine("Укажите код валюты из трёх латинских букв: /orchestrate USD.");
            continue;
        }

        try
        {
            Console.WriteLine("Orchestration MCP:");
            var result = await mcpClient.RunOrchestrationAsync(
                commandParts[1].ToUpperInvariant(), Console.WriteLine, cancellation.Token);
            Console.WriteLine("Финальный результат:");
            var textBlocks = result.Content.OfType<TextContentBlock>().ToArray();
            foreach (var block in textBlocks)
                Console.WriteLine(block.Text);
            if (textBlocks.Length == 0)
                Console.WriteLine(result.StructuredContent?.GetRawText() ?? "MCP-инструмент не вернул текстовый результат или структурированные данные.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Оркестрация MCP отменена.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось выполнить оркестрацию MCP: {ex.Message}");
        }
        Console.WriteLine();
        continue;
    }
    if (commandParts[0].Equals("/currency-pipeline", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 2 || commandParts[1].Length != 3 ||
            commandParts[1].Any(c => !((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))))
        {
            Console.WriteLine("Укажите код валюты из трёх латинских букв: /currency-pipeline USD.");
            continue;
        }

        try
        {
            Console.WriteLine($"Подключение к MCP: {McpClientService.ServerUrl}...");
            Console.WriteLine("Pipeline MCP:");
            var result = await mcpClient.RunCurrencyPipelineAsync(
                commandParts[1].ToUpperInvariant(), Console.WriteLine, cancellation.Token);
            Console.WriteLine("Финальный результат:");
            var textBlocks = result.Content.OfType<TextContentBlock>().ToArray();
            foreach (var block in textBlocks)
                Console.WriteLine(block.Text);
            if (textBlocks.Length == 0)
                Console.WriteLine(result.StructuredContent?.GetRawText() ?? "MCP-инструмент не вернул текстовый результат или структурированные данные.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(cancellation.IsCancellationRequested
                ? "Pipeline MCP отменён."
                : "Pipeline MCP остановлен: тайм-аут (30 секунд).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось выполнить pipeline MCP: {ex.Message}");
        }
        Console.WriteLine();
        continue;
    }
    if (commandParts[0].Equals("/currency-summary", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 1)
        {
            Console.WriteLine("Команда не требует аргументов: /currency-summary.");
            continue;
        }

        try
        {
            Console.WriteLine($"Получение сводки курсов через MCP: {McpClientService.ServerUrl}...");
            var (toolName, result) = await mcpClient.GetCurrencySummaryAsync(cancellation.Token);
            Console.WriteLine($"MCP-инструмент: {toolName}");
            if (result.IsError == true)
                Console.WriteLine("MCP-инструмент вернул ошибку:");

            var textBlocks = result.Content.OfType<TextContentBlock>().ToArray();
            foreach (var block in textBlocks)
                Console.WriteLine(block.Text);
            if (textBlocks.Length == 0)
                Console.WriteLine(result.StructuredContent?.GetRawText() ?? "MCP-инструмент не вернул текстовый результат или структурированные данные.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(cancellation.IsCancellationRequested
                ? "Запрос MCP отменён."
                : "Не удалось получить сводку: тайм-аут MCP (30 секунд). Попробуйте ещё раз.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось получить сводку через MCP: {ex.Message}");
        }
        Console.WriteLine();
        continue;
    }
    if (commandParts[0].Equals("/currency", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 2 || commandParts[1].Length != 3 ||
            commandParts[1].Any(c => !((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))))
        {
            Console.WriteLine("Укажите код валюты из трёх латинских букв: /currency USD или /currency EUR.");
            continue;
        }

        try
        {
            var currency = commandParts[1].ToUpperInvariant();
            Console.WriteLine($"Получение курса {currency} через MCP: {McpClientService.ServerUrl}...");
            var (toolName, result) = await mcpClient.GetCurrencyAsync(currency, cancellation.Token);
            Console.WriteLine($"MCP-инструмент: {toolName}");
            if (result.IsError == true)
                Console.WriteLine("MCP-инструмент вернул ошибку:");

            var textBlocks = result.Content.OfType<TextContentBlock>().ToArray();
            foreach (var block in textBlocks)
                Console.WriteLine(block.Text);
            if (textBlocks.Length == 0)
                Console.WriteLine(result.StructuredContent?.GetRawText() ?? "MCP-инструмент не вернул текстовый результат или структурированные данные.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(cancellation.IsCancellationRequested
                ? "Запрос MCP отменён."
                : "Не удалось получить курс: тайм-аут MCP (30 секунд). Попробуйте ещё раз.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось получить курс через MCP: {ex.Message}");
        }
        Console.WriteLine();
        continue;
    }
    if (request.Trim().Equals("/mcp-tools", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            Console.WriteLine($"Получение инструментов MCP: {McpClientService.ServerUrl}...");
            var tools = await mcpClient.ListToolsAsync(cancellation.Token);
            Console.WriteLine($"Доступные инструменты MCP: {tools.Count}");
            foreach (var tool in tools)
            {
                Console.WriteLine($"Имя: {tool.Name}");
                Console.WriteLine($"Описание: {(string.IsNullOrWhiteSpace(tool.Description) ? "не указано сервером" : tool.Description)}");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(cancellation.IsCancellationRequested
                ? "Запрос MCP отменён."
                : "Сервер MCP не ответил вовремя (тайм-аут 30 секунд). Попробуйте ещё раз.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось подключиться к MCP-серверу или получить список инструментов: {ex.Message}");
            Console.WriteLine("Проверьте доступность сервера и адрес MCP endpoint.");
        }
        Console.WriteLine();
        continue;
    }
    Console.WriteLine($"Агент: {await agent.AskAsync(request, cancellation.Token)}");
    if (agent.Statistics is not null)
        Console.WriteLine($"\n{agent.Statistics}");
    Console.WriteLine();
}
