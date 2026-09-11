using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public interface ILlmClient
{
    Task<string> GenerateAsync(
        string instructions,
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default);
}
