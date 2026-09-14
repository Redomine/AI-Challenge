namespace DesignerAssistant.Models;

public sealed record AgentResponse(
    LlmResponse ModelResponse,
    ContextStrategy Strategy,
    int FullHistoryTokens,
    bool FullHistoryTokensAreEstimated,
    int SentHistoryTokens,
    int FactsCount,
    string ActiveBranch,
    int MemoryBilledTokens);
