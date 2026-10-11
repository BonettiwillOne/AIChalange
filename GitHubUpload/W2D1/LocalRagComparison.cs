using System.Text.Json;

namespace W2D1;

internal static class LocalRagComparison
{
    public static async Task RunAsync(SemanticSearchService search, OllamaChatService chat,
        CancellationToken cancellationToken)
    {
        const string englishQuestion = "What is encapsulation in C#?";
        const string russianQuestion = "Что такое инкапсуляция в C#?";

        // Retrieve once. Both independent generation requests use this same snapshot.
        var top = (await search.SearchAsync(englishQuestion, 3, cancellationToken)).ToArray();
        Console.WriteLine($"/local-rag-compare: английский поиск — {englishQuestion}");
        for (var i = 0; i < top.Length; i++)
            Console.WriteLine(FormattableString.Invariant(
                $"[{i + 1}] Source: {top[i].Chunk.Source}; Section: {top[i].Chunk.Section}; ChunkId: {top[i].Chunk.ChunkId}; score (cosine): {top[i].Similarity:F6}"));
        if (top.Length != 3)
            throw new InvalidOperationException("Для сравнения необходимы ровно три фрагмента. Проверьте локальный индекс.");

        var sources = top.Select((result, index) => new
        {
            Citation = $"[{index + 1}]",
            result.Chunk.Source, result.Chunk.Title, result.Chunk.Section,
            result.Chunk.ChunkId, result.Chunk.Text
        }).ToArray();
        Console.WriteLine("Оба запроса используют одинаковые Sources и системный промпт /local-rag, без истории диалога.");
        Console.WriteLine("Сравнение передаёт Top 3 в Qwen независимо от порога, чтобы проверить влияние языка Question.");

        foreach (var question in new[] { englishQuestion, russianQuestion })
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"\nQuestion: {question}");
            try
            {
                var input = JsonSerializer.Serialize(new { Question = question, Sources = sources });
                var answer = await chat.ChatAsync(new[]
                {
                    new LocalChatMessage("system", LocalRagSession.SystemPrompt),
                    new LocalChatMessage("user", input)
                }, cancellationToken);
                Console.WriteLine("Qwen2.5: " + answer);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Тайм-аут запроса к Ollama (120 секунд).");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                Console.WriteLine($"Ошибка генерации для этого Question: {ex.Message}");
            }
        }
    }
}
