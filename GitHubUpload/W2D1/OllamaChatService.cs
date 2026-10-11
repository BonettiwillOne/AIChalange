using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace W2D1;

internal sealed record LocalChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed class OllamaChatService : IDisposable
{
    public const string ServerUrl = "http://localhost:11434";
    public const string Model = "qwen2.5:3b";
    // Keep requests on the local endpoint, including when a proxy is configured.
    private readonly HttpClient _client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    })
    {
        BaseAddress = new Uri(ServerUrl),
        Timeout = TimeSpan.FromSeconds(120)
    };

    public async Task<string> ChatAsync(IReadOnlyList<LocalChatMessage> messages,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new
        {
            model = Model,
            messages,
            stream = false,
            options = new { num_predict = 512 }
        }), Encoding.UTF8, "application/json");

        using var response = await _client.PostAsync("/api/chat", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = "Запрос завершился неудачно.";
            try
            {
                using var errorDocument = JsonDocument.Parse(body);
                if (errorDocument.RootElement.ValueKind == JsonValueKind.Object
                    && errorDocument.RootElement.TryGetProperty("error", out var details)
                    && details.ValueKind == JsonValueKind.String)
                    error = details.GetString() ?? error;
            }
            catch (JsonException) { }

            if (response.StatusCode == HttpStatusCode.NotFound
                && error.Contains("model", StringComparison.OrdinalIgnoreCase)
                && error.Contains("not found", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Модель {Model} отсутствует в Ollama. Установите её командой: ollama pull {Model}.");

            throw new InvalidOperationException($"Ollama: HTTP {(int)response.StatusCode}. {error}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var answer)
            || answer.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(answer.GetString()))
            throw new InvalidOperationException("Ollama вернула ответ без текста message.content.");

        // Only the final answer is displayed and retained; ignore separate thinking.
        var finalAnswer = RemoveThinking(answer.GetString()!);
        if (string.IsNullOrWhiteSpace(finalAnswer))
            throw new InvalidOperationException(
                "Ollama не вернула итоговый ответ после удаления рассуждений. Повторите запрос.");
        return finalAnswer;
    }

    private static string RemoveThinking(string content)
    {
        // Also discard an unfinished block when generation hits its token limit.
        var cleaned = Regex.Replace(content, @"<think>.*?(?:</think>|\z)", "",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // Some responses omit the opening tag: everything before the closing tag
        // is still reasoning and must not enter the console or conversation history.
        var closingTag = cleaned.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (closingTag >= 0)
            cleaned = cleaned[(closingTag + "</think>".Length)..];
        return cleaned.Trim();
    }

    public void Dispose() => _client.Dispose();
}
