using DesignerAssistant.Models;

namespace DesignerAssistant.Agent;

public interface ITaskStageRunner
{
    TaskPlan? LastStructuredPlan => null;

    Task<AgentResponse> PlanTaskAsync(
        string query,
        string? revisionContext = null,
        CancellationToken cancellationToken = default,
        string? storedUserMessage = null);

    Task<AgentResponse> ExecuteTaskAsync(
        TaskContext context,
        CancellationToken cancellationToken = default);

    Task<AgentResponse> ValidateTaskAsync(
        TaskContext context,
        CancellationToken cancellationToken = default);
}
