namespace DesignerAssistant.Models;

public sealed record ComparisonRunResult(
    bool CompressionEnabled,
    long TotalBilledTokens,
    long TotalSentHistoryTokens,
    int FinalFullHistoryTokens,
    int FinalSentHistoryTokens,
    int MaxContextTokens,
    string FinalAnswer);
