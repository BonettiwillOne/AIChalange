using ModelContextProtocol.Client;

namespace W2D1;

internal sealed class McpClientService
{
    public const string ServerUrl = "http://77.95.203.113:5000";

    public async Task<IReadOnlyList<(string Name, string Description)>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(ServerUrl),
            TransportMode = HttpTransportMode.StreamableHttp
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);

        return tools.Select(tool => (tool.Name, tool.Description ?? "")).ToArray();
    }
}
