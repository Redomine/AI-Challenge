namespace DesignerAssistant.Models;

public sealed record ComparisonRunResult(
    ContextStrategy Strategy,
    long TotalBilledTokens,
    long TotalSentHistoryTokens,
    int MaxContextTokens,
    int FinalFactsCount,
    string FinalAnswer);
