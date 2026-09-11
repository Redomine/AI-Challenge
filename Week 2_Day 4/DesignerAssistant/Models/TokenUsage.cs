namespace DesignerAssistant.Models;

public sealed record TokenUsage(
    int CurrentRequestTokens,
    int HistoryTokens,
    int ContextTokens,
    int PromptTokens,
    int CachedPromptTokens,
    int CompletionTokens,
    int BilledTokens,
    bool TextCountsAreEstimated);
