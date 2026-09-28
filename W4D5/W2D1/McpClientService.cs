using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace W2D1;

internal sealed class McpClientService
{
    public const string ServerUrl = "http://77.95.203.113:5000";
    public const string FileServerUrl = "http://77.95.203.113:5001";

    public async Task<IReadOnlyList<(string Name, string Description)>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        return await WithClientAsync(async (client, token) =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: token);
            return tools.Select(tool => (tool.Name, tool.Description ?? "")).ToArray();
        }, cancellationToken);
    }

    public async Task<(string ToolName, CallToolResult Result)> GetCurrencyAsync(
        string currency, CancellationToken cancellationToken = default)
    {
        return await WithClientAsync(async (client, token) =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: token);
            // Discover the actual tool name from the server's input schema.
            var candidates = tools.Where(tool =>
                tool.JsonSchema.ValueKind == JsonValueKind.Object &&
                tool.JsonSchema.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty("currency", out _)).ToArray();

            if (candidates.Length == 0)
                throw new InvalidOperationException("MCP-сервер не вернул инструмент с параметром currency.");
            if (candidates.Length > 1)
                throw new InvalidOperationException(
                    $"Найдено несколько MCP-инструментов с параметром currency: {string.Join(", ", candidates.Select(tool => tool.Name))}. Невозможно однозначно выбрать инструмент курса валют.");

            var tool = candidates[0];
            var result = await client.CallToolAsync(tool.Name,
                new Dictionary<string, object?> { ["currency"] = currency },
                cancellationToken: token);
            return (tool.Name, result);
        }, cancellationToken);
    }

    public async Task<(string ToolName, CallToolResult Result)> GetCurrencySummaryAsync(
        CancellationToken cancellationToken = default)
    {
        return await WithClientAsync(async (client, token) =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: token);
            var tool = tools.FirstOrDefault(tool =>
                string.Equals(tool.Name, "get_currency_summary", StringComparison.Ordinal));
            if (tool is null)
                throw new InvalidOperationException("MCP-сервер не вернул инструмент get_currency_summary.");

            var result = await client.CallToolAsync(tool.Name, cancellationToken: token);
            return (tool.Name, result);
        }, cancellationToken);
    }

    public async Task<CallToolResult> RunCurrencyPipelineAsync(
        string currency, Action<string> progress, CancellationToken cancellationToken = default)
    {
        return await WithClientAsync(async (client, token) =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: token);
            var names = new[] { "get_currency_rate", "analyze_currency", "save_currency_report" };
            var missing = names.Where(name => !tools.Any(tool =>
                string.Equals(tool.Name, name, StringComparison.Ordinal))).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    $"На MCP-сервере отсутствуют инструменты: {string.Join(", ", missing)}. Pipeline не запущен.");

            async Task<CallToolResult> CallStageAsync(string name, Dictionary<string, object?> arguments)
            {
                try
                {
                    var result = await client.CallToolAsync(name, arguments, cancellationToken: token);
                    if (result.IsError == true)
                        throw new InvalidOperationException(string.Join(Environment.NewLine,
                            result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
                    return result;
                }
                catch (OperationCanceledException)
                {
                    progress($"Этап {name} прерван.");
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Ошибка этапа {name}: {ex.Message}", ex);
                }
            }

            static string GetText(CallToolResult result, string name)
            {
                var text = string.Join(Environment.NewLine,
                    result.Content.OfType<TextContentBlock>().Select(block => block.Text));
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException(
                        $"Этап {name} не вернул текстовый результат. Pipeline остановлен.");
                return text;
            }

            var rate = await CallStageAsync(names[0], new() { ["currency"] = currency });
            var rateText = GetText(rate, names[0]);
            progress("1. get_currency_rate -> получены данные");

            var report = await CallStageAsync(names[1], new() { ["input"] = rateText });
            var reportText = GetText(report, names[1]);
            progress("2. analyze_currency -> отчет сформирован");

            var saveArguments = new Dictionary<string, object?> { ["content"] = reportText };
            var saveTool = tools.First(tool => tool.Name == names[2]);
            if (saveTool.JsonSchema.ValueKind == JsonValueKind.Object &&
                saveTool.JsonSchema.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty("filename", out _))
                saveArguments["filename"] = "currency-report.txt";

            var saved = await CallStageAsync(names[2], saveArguments);
            progress("3. save_currency_report -> отчет сохранен");
            return saved;
        }, cancellationToken);
    }

    public async Task<CallToolResult> RunOrchestrationAsync(
        string currency, Action<string> progress, CancellationToken cancellationToken = default)
    {
        var server = $"Currency Server ({ServerUrl})";
        var stage = "подключение и получение списка инструментов";
        try
        {
            var reportText = await WithClientAsync(ServerUrl, async (client, token) =>
            {
                var tools = await client.ListToolsAsync(cancellationToken: token);
                foreach (var name in new[] { "get_currency_rate", "analyze_currency" })
                    if (!tools.Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal)))
                        throw new InvalidOperationException($"Отсутствует инструмент {name}.");

                stage = "get_currency_rate";
                var rate = await client.CallToolAsync(stage,
                    new Dictionary<string, object?> { ["currency"] = currency }, cancellationToken: token);
                EnsureSuccess(rate);
                var rateText = GetText(rate);
                progress("1. Currency Server -> get_currency_rate -> получены данные");

                stage = "analyze_currency";
                var report = await client.CallToolAsync(stage,
                    new Dictionary<string, object?> { ["input"] = rateText }, cancellationToken: token);
                EnsureSuccess(report);
                var text = GetText(report);
                progress("2. Currency Server -> analyze_currency -> отчет сформирован");
                return text;
            }, cancellationToken);

            server = $"File Server ({FileServerUrl})";
            stage = "подключение и получение списка инструментов";
            return await WithClientAsync(FileServerUrl, async (client, token) =>
            {
                var tools = await client.ListToolsAsync(cancellationToken: token);
                if (!tools.Any(tool => string.Equals(tool.Name, "save_text", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Отсутствует инструмент save_text.");

                stage = "save_text";
                var saved = await client.CallToolAsync(stage,
                    new Dictionary<string, object?>
                    {
                        ["content"] = reportText,
                        ["filename"] = "orchestration-report.txt"
                    }, cancellationToken: token);
                EnsureSuccess(saved);
                progress("3. File Server -> save_text -> отчет сохранен");
                return saved;
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"{server}, этап {stage}: тайм-аут (30 секунд на работу с сервером). Оркестрация остановлена.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{server}, этап {stage}: {ex.Message} Оркестрация остановлена.", ex);
        }

        static void EnsureSuccess(CallToolResult result)
        {
            if (result.IsError == true)
            {
                var details = string.Join(Environment.NewLine,
                    result.Content.OfType<TextContentBlock>().Select(block => block.Text));
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                    ? "Инструмент вернул ошибку без текстового описания."
                    : details);
            }
        }

        static string GetText(CallToolResult result)
        {
            var text = string.Join(Environment.NewLine,
                result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("Инструмент не вернул текстовый результат.");
            return text;
        }
    }

    private static Task<T> WithClientAsync<T>(
        Func<McpClient, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
        => WithClientAsync(ServerUrl, action, cancellationToken);

    private static async Task<T> WithClientAsync<T>(
        string serverUrl, Func<McpClient, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(serverUrl),
            TransportMode = HttpTransportMode.StreamableHttp
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        return await action(client, timeout.Token);
    }
}
