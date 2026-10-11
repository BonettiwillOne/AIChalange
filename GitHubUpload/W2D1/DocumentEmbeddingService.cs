using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace W2D1;

public sealed class DocumentEmbeddingService : IDisposable
{
    public const string Model = "text-embedding-3-small";
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public async Task<float[]> GenerateEmbeddingAsync(string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Текст чанка не должен быть пустым.", nameof(text));

        var key = await ReadApiKeyAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = Model,
            input = text,
            encoding_format = "float"
        }), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = "Запрос отклонён API.";
                try
                {
                    using var error = JsonDocument.Parse(body);
                    if (error.RootElement.TryGetProperty("error", out var details)
                        && details.ValueKind == JsonValueKind.Object
                        && details.TryGetProperty("message", out var apiMessage)
                        && apiMessage.ValueKind == JsonValueKind.String)
                        message = apiMessage.GetString() ?? message;
                }
                catch (JsonException) { }
                throw new InvalidOperationException(
                    $"OpenAI Embeddings API: HTTP {(int)response.StatusCode}. " +
                    message.Replace(key, "[ключ скрыт]", StringComparison.Ordinal));
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 1
                || !data[0].TryGetProperty("embedding", out var vector)
                || vector.ValueKind != JsonValueKind.Array || vector.GetArrayLength() == 0)
                throw new JsonException();

            var embedding = new float[vector.GetArrayLength()];
            var index = 0;
            foreach (var value in vector.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var number)
                    || !float.IsFinite(number))
                    throw new JsonException();
                embedding[index++] = number;
            }
            return embedding;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("OpenAI Embeddings API: время ожидания ответа истекло.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Не удалось подключиться к OpenAI Embeddings API. Проверьте сеть.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("OpenAI Embeddings API вернул некорректный ответ или вектор.");
        }
    }

    private static async Task<string> ReadApiKeyAsync(CancellationToken cancellationToken)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop))
                throw new InvalidOperationException("Задайте OPENAI_API_KEY: папка рабочего стола недоступна.");
            var path = Path.Combine(desktop, "Ключ");
            if (!File.Exists(path))
                path = Path.Combine(desktop, "Ключ.txt");
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    "Задайте OPENAI_API_KEY или поместите файл «Ключ» / «Ключ.txt» на рабочий стол.");
            key = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        }
        key = key.Trim();
        if (key.Length == 0 || key.Any(c => c < 33 || c > 126))
            throw new InvalidOperationException("API-ключ должен быть непустой строкой без пробелов.");
        return key;
    }

    public void Dispose() => _client.Dispose();
}
