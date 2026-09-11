namespace DesignerAssistant.Agent;

public interface IDesignAssistantAgent
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<string> AskAsync(
        string userMessage,
        CancellationToken cancellationToken = default);

    Task ClearHistoryAsync(CancellationToken cancellationToken = default);
}
