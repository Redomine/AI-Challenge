using DesignerAssistant.Models;

namespace DesignerAssistant.Agent;

public interface IDesignAssistantAgent
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<LlmResponse> AskAsync(
        string userMessage,
        CancellationToken cancellationToken = default);

    Task ClearHistoryAsync(CancellationToken cancellationToken = default);
}
