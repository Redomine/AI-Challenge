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

public interface IToolCallingLlmClient : ILlmClient
{
    Task<LlmResponse> GenerateWithToolsAsync(
        string instructions,
        IReadOnlyCollection<ChatMessage> messages,
        IToolProvider toolProvider,
        Action<string>? trace = null,
        CancellationToken cancellationToken = default);
}
