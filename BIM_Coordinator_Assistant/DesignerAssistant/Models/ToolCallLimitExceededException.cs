namespace DesignerAssistant.Models;

public sealed class ToolCallLimitExceededException(int limit)
    : InvalidOperationException($"GigaChat превысил лимит последовательных вызовов инструментов ({limit}).")
{
    public int Limit { get; } = limit;
}
