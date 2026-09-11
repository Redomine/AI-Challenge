using DesignerAssistant.Models;

namespace DesignerAssistant.Agent;

public interface IDesignAssistantAgent
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<AgentResponse> AskAsync(
        string userMessage,
        CancellationToken cancellationToken = default);

    Task<CompressionStatus> SetCompressionEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default);

    CompressionStatus GetCompressionStatus();

    Task ClearHistoryAsync(CancellationToken cancellationToken = default);
}
