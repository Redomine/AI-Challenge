namespace DesignerAssistant.Agent;

public interface IDesignAssistantAgent
{
    Task<string> AskAsync(
        string userMessage,
        CancellationToken cancellationToken = default);

    void ClearHistory();
}
