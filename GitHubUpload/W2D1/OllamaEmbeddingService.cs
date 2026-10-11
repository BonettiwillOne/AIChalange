using System.Text;
using System.Text.Json;

namespace W2D1;

internal sealed class OllamaContextLengthException : InvalidOperationException
{
    public OllamaContextLengthException(string message) : base(message) { }
}

internal sealed class OllamaEmbeddingService : IDisposable
{
    public const string Model = "bge-m3";
    public const int Dimensions = 1024;
    private readonly HttpClient _client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    })
    {
        BaseAddress = new Uri(OllamaChatService.ServerUrl),
        Timeout = TimeSpan.FromMinutes(2)
    };

    public async Task<float[]> GenerateEmbeddingAsync(string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Текст для embedding не должен быть пустым.", nameof(text));

        using var content = new StringContent(JsonSerializer.Serialize(new
        {
            model = Model,
            input = text,
            // Never silently discard text; the local index builder splits rejected chunks.
            truncate = false
        }), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _client.PostAsync("/api/embed", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = $"Проверьте наличие {Model} и длину фрагмента.";
                try
                {
                    using var details = JsonDocument.Parse(body);
                    if (details.RootElement.ValueKind == JsonValueKind.Object
                        && details.RootElement.TryGetProperty("error", out var message)
                        && message.ValueKind == JsonValueKind.String)
                        error = message.GetString() ?? error;
                }
                catch (JsonException) { }
                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest
                    && error.Contains("input length exceeds the context length", StringComparison.OrdinalIgnoreCase))
                    throw new OllamaContextLengthException($"Ollama embeddings: {error}");
                throw new InvalidOperationException($"Ollama embeddings: HTTP {(int)response.StatusCode}. {error}");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("embeddings", out var embeddings)
                || embeddings.ValueKind != JsonValueKind.Array || embeddings.GetArrayLength() != 1
                || embeddings[0].ValueKind != JsonValueKind.Array
                || embeddings[0].GetArrayLength() != Dimensions)
                throw new JsonException();

            var vector = new float[Dimensions];
            var index = 0;
            foreach (var value in embeddings[0].EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Number
                    || !value.TryGetSingle(out var number) || !float.IsFinite(number))
                    throw new JsonException();
                vector[index++] = number;
            }
            if (vector.All(value => value == 0))
                throw new JsonException();
            return vector;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Ollama недоступна по {OllamaChatService.ServerUrl}. Проверьте, что локальный сервер запущен (ollama serve).", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Тайм-аут Ollama embeddings (120 секунд). Повторите запрос.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Ollama вернула некорректный embedding {Model}; ожидаемая размерность: {Dimensions}.", ex);
        }
    }

    public void Dispose() => _client.Dispose();
}
