namespace DesignerAssistant.Models;

public sealed record AgentResponse(
    LlmResponse ModelResponse,
    bool CompressionEnabled,
    int FullHistoryTokens,
    bool FullHistoryTokensAreEstimated,
    int SentHistoryTokens,
    int SummarizedMessageCount,
    int RecentMessageCount,
    int CompressionBilledTokens);
