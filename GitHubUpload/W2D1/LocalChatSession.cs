using System.Text.Json;

namespace W2D1;

// A new isolated in-memory history is created on each entry into /local-chat.
internal sealed class LocalChatSession
{
    private readonly OllamaChatService _ollama;
    private readonly List<LocalChatMessage> _history = new();

    public LocalChatSession(OllamaChatService ollama) => _ollama = ollama;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"Локальный чат: модель {OllamaChatService.Model} (Qwen2.5 3B).");
        Console.WriteLine($"Генерация выполняется локально через Ollama: {OllamaChatService.ServerUrl}.");
        Console.WriteLine("История хранится только в текущей локальной сессии. /exit — возврат в основное меню.");

        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("Локальный чат, Вы: ");
            var input = Console.ReadLine()?.Trim();
            if (input is null || cancellationToken.IsCancellationRequested
                || input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                return;
            if (input.Length == 0) continue;

            try
            {
                var userMessage = new LocalChatMessage("user", input);
                var messages = _history.Append(userMessage).ToArray();
                var answer = await _ollama.ChatAsync(messages, cancellationToken);
                // Commit a turn only after success so a failed request can be retried.
                _history.Add(userMessage);
                _history.Add(new LocalChatMessage("assistant", answer));
                Console.WriteLine($"Qwen2.5: {answer}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Превышен тайм-аут HTTP-запроса к Ollama (120 секунд). Повторите запрос или введите /exit.");
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Не удалось выполнить HTTP-запрос к Ollama: {ex.Message}");
                Console.WriteLine($"Проверьте, что Ollama запущена (ollama serve) и доступна по {OllamaChatService.ServerUrl}.");
            }
            catch (JsonException)
            {
                Console.WriteLine("Ollama вернула некорректный JSON. Проверьте локальный сервер и повторите запрос.");
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine(ex.Message);
            }
            Console.WriteLine();
        }
    }
}
