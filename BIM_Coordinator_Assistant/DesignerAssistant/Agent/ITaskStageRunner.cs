using DesignerAssistant.Models;

namespace DesignerAssistant.Agent;

public interface ITaskStageRunner
{
    TaskPlan? LastStructuredPlan => null;

    PromptUnderstanding? LastPromptUnderstanding => null;

    Task<PromptUnderstanding> BuildPromptAsync(
        string query,
        CancellationToken cancellationToken = default,
        string? revisionContext = null) => Task.FromResult(new PromptUnderstanding(
            query,
            Goal: query,
            Constraints: [],
            RequiredData: [],
            SuccessCriteria: [],
            Model: "test",
            UsedFallback: true,
            FailureReason: "Prompt-builder не сконфигурирован в тесте."));

    Task<AgentResponse> PlanTaskAsync(
        string query,
        string? revisionContext = null,
        CancellationToken cancellationToken = default,
        string? storedUserMessage = null,
        PromptUnderstanding? promptUnderstanding = null);

    Task<AgentResponse> ExecuteTaskAsync(
        TaskContext context,
        CancellationToken cancellationToken = default);

    Task<AgentResponse> ValidateTaskAsync(
        TaskContext context,
        CancellationToken cancellationToken = default);
}
