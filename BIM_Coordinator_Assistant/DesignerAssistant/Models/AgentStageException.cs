namespace DesignerAssistant.Models;

public sealed class AgentStageException(
    string message,
    IReadOnlyList<string> traces,
    Exception innerException) : InvalidOperationException(message, innerException)
{
    public IReadOnlyList<string> Traces { get; } = traces;
}
