namespace DesignerAssistant.Models;

public sealed record CompressionState(
    bool IsEnabled,
    string Summary,
    int SummarizedMessageCount);
