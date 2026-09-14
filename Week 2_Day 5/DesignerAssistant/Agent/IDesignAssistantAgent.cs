using DesignerAssistant.Models;

namespace DesignerAssistant.Agent;

public interface IDesignAssistantAgent
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<AgentResponse> AskAsync(
        string userMessage,
        CancellationToken cancellationToken = default);

    Task<ContextStatus> SetStrategyAsync(
        ContextStrategy strategy,
        CancellationToken cancellationToken = default);

    Task<ContextStatus> CreateCheckpointAsync(
        CancellationToken cancellationToken = default);

    Task<ContextStatus> CreateBranchAsync(
        string branchName,
        CancellationToken cancellationToken = default);

    Task<ContextStatus> SwitchBranchAsync(
        string branchName,
        CancellationToken cancellationToken = default);

    ContextStatus GetContextStatus();

    Task ClearHistoryAsync(CancellationToken cancellationToken = default);
}
