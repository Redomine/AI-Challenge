using DesignerAssistant.Models;

namespace DesignerAssistant.Agent;

public interface ITaskStageRunner
{
    Task<AgentResponse> PlanTaskAsync(
        string query,
        string? previousExecution = null,
        CancellationToken cancellationToken = default);

    Task<AgentResponse> ExecuteTaskAsync(
        TaskContext context,
        CancellationToken cancellationToken = default);

    Task<AgentResponse> ValidateTaskAsync(
        TaskContext context,
        CancellationToken cancellationToken = default);
}
