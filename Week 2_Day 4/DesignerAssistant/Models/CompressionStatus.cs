namespace DesignerAssistant.Models;

public sealed record CompressionStatus(
    bool IsEnabled,
    int SummarizedMessageCount,
    int RecentMessageCount,
    int CompressionBilledTokens);
