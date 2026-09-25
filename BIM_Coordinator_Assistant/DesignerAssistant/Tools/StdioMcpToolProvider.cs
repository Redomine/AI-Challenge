using System.Text.Json;
using DesignerAssistant.Models;
using ModelContextProtocol.Client;

namespace DesignerAssistant.Tools;

public sealed class StdioMcpToolProvider : IToolProvider, IAsyncDisposable
{
    private readonly string _name;
    private readonly string _serverDll;
    private McpClient? _client;

    public StdioMcpToolProvider(string name, string serverDll)
    {
        if (name is not ("log" or "ops")) throw new ArgumentException("Unsupported MCP server.", nameof(name));
        _name = name;
        _serverDll = Path.GetFullPath(serverDll);
    }

    private async Task<McpClient> ClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null) return _client;
        if (!File.Exists(_serverDll)) throw new FileNotFoundException($"MCP server '{_name}' is not built.", _serverDll);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = $"{_name}-mcp",
            Command = "dotnet",
            Arguments = [_serverDll, _name]
        });
        _client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        return _client;
    }

    public async Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = await (await ClientAsync(cancellationToken)).ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Select(tool => new ToolDefinition(tool.Name, tool.Description ?? tool.Name, tool.JsonSchema)).ToArray();
    }

    public async Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!name.StartsWith(_name + "_", StringComparison.Ordinal))
            throw new InvalidOperationException($"Tool '{name}' does not belong to {_name}-mcp.");
        var values = arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().ToDictionary(item => item.Name, item => (object?)item.Value.Clone())
            : new Dictionary<string, object?>();
        var response = await (await ClientAsync(cancellationToken)).CallToolAsync(name, values, cancellationToken: cancellationToken);
        var root = JsonSerializer.SerializeToElement(response);
        if (root.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            return JsonSerializer.Serialize(new { ok = false, message = root.GetRawText() });
        if (root.TryGetProperty("content", out var content))
        {
            var value = content.EnumerateArray().FirstOrDefault(item =>
                item.TryGetProperty("type", out var type) && type.GetString() == "text");
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("text", out var text))
                return text.GetString() ?? "null";
        }
        return root.GetRawText();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }
}
