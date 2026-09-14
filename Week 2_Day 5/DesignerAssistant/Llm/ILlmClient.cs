using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public interface ILlmClient
{
    Task<TokenCountResult> CountTextTokensAsync(
        IReadOnlyCollection<string> texts,
        CancellationToken cancellationToken = default);

    Task<LlmResponse> GenerateAsync(
        string instructions,
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default);
}
