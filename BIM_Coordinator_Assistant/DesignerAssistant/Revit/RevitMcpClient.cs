using System.Text.Json;
using ModelContextProtocol.Client;
using DesignerAssistant.Models;
using DesignerAssistant.Llm;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DesignerAssistant.Revit;

public sealed class RevitMcpClient : IToolProvider, IToolConfirmationProvider, IAsyncDisposable
{
    private static readonly HashSet<string> AllowedTools = new(StringComparer.Ordinal)
    {
        "revit_list_available_targets", "revit_get_current_target", "revit_switch_target",
        "revit_get_current_view_info", "revit_get_selected_elements",
        "revit_get_element_details", "revit_get_element_parameters",
        "revit_get_type_parameters", "revit_list_worksets",
        "revit_analyze_model_statistics", "revit_ai_element_filter"
        , "revit_custom_summarize_elements", "revit_custom_list_elements"
    };

    private readonly string _serverPath;
    private readonly Func<string, Task<bool>>? _confirmWriteAsync;
    private readonly HashSet<string> _discoveredWriteTools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _discoveredReadTools = new(StringComparer.Ordinal);
    private McpClient? _client;
    private string _selectedTarget = "auto";

    public RevitMcpClient(string? serverPath = null, Func<string, Task<bool>>? confirmWriteAsync = null)
    {
        _serverPath = serverPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RvtMcp", "rvt", "server", "0.6.1", "rvt-mcp.exe");
        _confirmWriteAsync = confirmWriteAsync;
    }

    public async Task<string> CallAsync(string toolName, IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken cancellationToken = default)
    {
        if (!AllowedTools.Contains(toolName) &&
            !_discoveredReadTools.Contains(toolName) &&
            !_discoveredWriteTools.Contains(toolName))
        {
            throw new InvalidOperationException($"Инструмент '{toolName}' запрещён политикой read-only.");
        }

        return await CallCoreAsync(toolName, arguments, cancellationToken);
    }

    public Task<string> ListTargetsAsync(CancellationToken token = default) => CallAsync("revit_list_available_targets", cancellationToken: token);
    public Task<string> StatusAsync(CancellationToken token = default) => CallAsync("revit_get_current_target", cancellationToken: token);
    public Task<string> SelectionAsync(CancellationToken token = default) => CallAsync("revit_get_selected_elements", cancellationToken: token);
    public async Task<string> DescribeToolsAsync(CancellationToken token = default)
    {
        return JsonSerializer.Serialize(await GetToolsAsync(token), new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = await (await GetClientAsync(cancellationToken)).ListToolsAsync(cancellationToken: cancellationToken);
        _discoveredWriteTools.Clear();
        _discoveredReadTools.Clear();
        foreach (var tool in tools.Where(tool => !AllowedTools.Contains(tool.Name) && IsPermittedWriteTool(tool.Name)))
        {
            var definition = new ToolDefinition(tool.Name, tool.Description ?? tool.Name, tool.JsonSchema);
            if (ToolCapabilityCatalog.IsWriteTool(definition)) _discoveredWriteTools.Add(tool.Name);
            else _discoveredReadTools.Add(tool.Name);
        }
        return tools.Where(tool => AllowedTools.Contains(tool.Name) || _discoveredReadTools.Contains(tool.Name) || _discoveredWriteTools.Contains(tool.Name))
            .Select(tool => new ToolDefinition(tool.Name, tool.Description ?? tool.Name, tool.JsonSchema))
            .ToArray();
    }

    public async Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var values = arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone())
            : new Dictionary<string, object?>();
        return await CallAsync(name, values, cancellationToken);
    }

    public Task<bool> ConfirmAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var proposal = $"Инструмент: {name}\nЦель Revit: {_selectedTarget}\nАргументы: {arguments.GetRawText()}";
        return _confirmWriteAsync?.Invoke(proposal) ?? Task.FromResult(false);
    }
    public Task<string> UseAsync(int year, CancellationToken token = default)
    {
        if (year is not (2022 or 2024)) throw new ArgumentException("Поддерживаются Revit 2022 и 2024.");
        _selectedTarget = year.ToString();
        return CallAsync("revit_switch_target", new Dictionary<string, object?> { ["version"] = year.ToString(), ["verify"] = false }, token);
    }

    public async Task<IReadOnlyList<RevitSession>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var targetJson = await ListTargetsAsync(cancellationToken);
        using var document = JsonDocument.Parse(targetJson);
        var routablePids = document.RootElement.GetProperty("targets")
            .EnumerateArray()
            .Where(target => target.TryGetProperty("pid", out _))
            .ToDictionary(
                target => target.GetProperty("pid").GetInt32(),
                target => int.Parse(target.GetProperty("year").GetString()!));
        var sessions = new List<RevitSession>();
        foreach (var process in Process.GetProcessesByName("Revit"))
        {
            using (process)
            {
                var title = process.MainWindowTitle;
                var match = Regex.Match(title, @"Autodesk Revit (?<year>\d{4}).*?(?: - \[(?<document>.*)\])?$");
                if (!match.Success) continue;
                var year = int.Parse(match.Groups["year"].Value);
                var documentTitle = match.Groups["document"].Success ? match.Groups["document"].Value : "Главная";
                var projectName = documentTitle.Split(" - ", 2, StringSplitOptions.None)[0];
                sessions.Add(new RevitSession(
                    process.Id,
                    year,
                    projectName,
                    $"REVIT-{year} [{projectName}]",
                    routablePids.TryGetValue(process.Id, out var targetYear) && targetYear == year));
            }
        }
        return sessions.OrderBy(session => session.Year).ThenBy(session => session.ProcessId).ToArray();
    }

    public async Task<string> SelectSessionAsync(string selector, CancellationToken cancellationToken = default)
    {
        var sessions = await ListSessionsAsync(cancellationToken);
        var matches = int.TryParse(selector.Trim(), out var processId)
            ? sessions.Where(session => session.ProcessId == processId).ToArray()
            : sessions.Where(session => session.DisplayName.Contains(selector.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) throw new ArgumentException($"Сеанс '{selector}' не найден. Выполните /revit select.");
        if (matches.Length > 1) throw new ArgumentException("Найдено несколько сеансов. Выберите уникальный ID процесса.");
        var session = matches[0];
        if (!session.IsRoutable)
        {
            throw new InvalidOperationException(
                $"{session.DisplayName}, ID {session.ProcessId}, не опубликован текущим rvt-mcp. " +
                $"Версия 0.6.1 поддерживает только один discovery-target на год.");
        }
        await UseAsync(session.Year, cancellationToken);
        _selectedTarget = $"{session.DisplayName}, ID {session.ProcessId}";
        return $"Выбран {session.DisplayName}, ID {session.ProcessId}.";
    }

    private async Task<McpClient> GetClientAsync(CancellationToken token)
    {
        if (_client is not null) return _client;
        if (!File.Exists(_serverPath)) throw new FileNotFoundException("Не найден сервер rvt-mcp.", _serverPath);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "rvt-mcp-confirmed-writes",
            Command = _serverPath,
            Arguments = ["--toolsets", "query,create,modify,delete,view,meta,organization,custom", "--disable-toolbaker"]
        });
        _client = await McpClient.CreateAsync(transport, cancellationToken: token);
        return _client;
    }

    private async Task<string> CallCoreAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
        return FormatToolResult(result);
    }

    private async Task ResetClientAsync()
    {
        var client = _client;
        _client = null;
        if (client is not null) await client.DisposeAsync();
    }

    private static bool IsPermittedWriteTool(string name) =>
        !name.Contains("send_code", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("toolbaker", StringComparison.OrdinalIgnoreCase);

    private static string FormatToolResult(object result)
    {
        var root = JsonSerializer.SerializeToElement(result);
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var texts = content.EnumerateArray()
                .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "text")
                .Select(item => item.TryGetProperty("text", out var text) ? text.GetString() : null)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();
            if (texts.Length > 0)
            {
                return string.Join(Environment.NewLine, texts!);
            }
        }

        return JsonSerializer.Serialize(root, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    public async ValueTask DisposeAsync()
    {
        await ResetClientAsync();
    }
}
