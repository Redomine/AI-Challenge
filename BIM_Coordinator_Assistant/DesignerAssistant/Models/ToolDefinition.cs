using System.Text.Json;

namespace DesignerAssistant.Models;

public sealed record ToolDefinition(string Name, string Description, JsonElement Parameters);

public interface IToolProvider
{
    Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default);
    Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default);
}
