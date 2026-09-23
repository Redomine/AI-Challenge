using System.Text.Json;

namespace DesignerAssistant.Models;

public sealed record ToolDefinition(string Name, string Description, JsonElement Parameters);

public interface IToolProvider
{
    Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default);
    Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default);
}

public interface IToolConfirmationProvider
{
    Task<bool> ConfirmAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default);
}

public interface IToolOperationCoordinator
{
    Task<string> WaitForCompletionAsync(
        string name,
        string initialResult,
        CancellationToken cancellationToken = default);
}
