namespace DesignerAssistant.Models;

public enum SchedulePeriod
{
    Daily,
    Weekly,
    Monthly
}

public sealed record ScheduledAgentTask(
    Guid Id,
    string Name,
    string Prompt,
    SchedulePeriod Period,
    TimeOnly Time,
    DayOfWeek? DayOfWeek,
    int? DayOfMonth,
    bool IsEnabled,
    DateTimeOffset NextRunAt,
    DateTimeOffset? LastRunAt = null,
    string? LastResult = null,
    bool IsRunning = false);

