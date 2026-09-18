namespace DesignerAssistant.Models;

public sealed record AgentResponse(
    LlmResponse ModelResponse,
    int FullHistoryTokens,
    bool FullHistoryTokensAreEstimated,
    int SentHistoryTokens,
    IReadOnlyList<string>? ToolTraces = null);
