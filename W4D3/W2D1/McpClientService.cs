using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace W2D1;

internal sealed class McpClientService
{
    public const string ServerUrl = "http://77.95.203.113:5000";

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

    private static async Task<T> WithClientAsync<T>(
        Func<McpClient, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(ServerUrl),
            TransportMode = HttpTransportMode.StreamableHttp
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        return await action(client, timeout.Token);
    }
}
