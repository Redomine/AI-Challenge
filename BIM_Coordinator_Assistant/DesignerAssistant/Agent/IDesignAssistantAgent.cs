using DesignerAssistant.Models;

namespace DesignerAssistant.Agent;

public interface IDesignAssistantAgent
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<AgentResponse> AskAsync(string userMessage, CancellationToken cancellationToken = default);
    IReadOnlyList<ChatMessage> GetHistory();
    Task<MemorySnapshot> GetMemoryAsync(CancellationToken cancellationToken = default);
    Task RememberAsync(MemoryLayer layer, string key, string value, CancellationToken cancellationToken = default);
    Task ClearHistoryAsync(CancellationToken cancellationToken = default);
    Task CompleteTaskAsync(CancellationToken cancellationToken = default);
}
