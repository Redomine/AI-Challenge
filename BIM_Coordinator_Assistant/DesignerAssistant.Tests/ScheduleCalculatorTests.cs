using DesignerAssistant.Models;
using DesignerAssistant.Scheduling;

namespace DesignerAssistant.Tests;

public sealed class ScheduleCalculatorTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void DailyScheduleMovesToTomorrowAfterScheduledTime()
    {
        var next = ScheduleCalculator.GetNextRun(
            SchedulePeriod.Daily, new TimeOnly(9, 0), null, null,
            new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero), Utc);

        Assert.Equal(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void WeeklyScheduleUsesSelectedWeekday()
    {
        var next = ScheduleCalculator.GetNextRun(
            SchedulePeriod.Weekly, new TimeOnly(8, 30), DayOfWeek.Monday, null,
            new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero), Utc);

        Assert.Equal(new DateTimeOffset(2026, 9, 28, 8, 30, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void MonthlyScheduleClampsDayToEndOfShortMonth()
    {
        var next = ScheduleCalculator.GetNextRun(
            SchedulePeriod.Monthly, new TimeOnly(9, 0), null, 31,
            new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero), Utc);

        Assert.Equal(new DateTimeOffset(2026, 2, 28, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void MonthlyScheduleMovesToNextMonthAfterRun()
    {
        var next = ScheduleCalculator.GetNextRun(
            SchedulePeriod.Monthly, new TimeOnly(9, 0), null, 31,
            new DateTimeOffset(2026, 2, 28, 10, 0, 0, TimeSpan.Zero), Utc);

        Assert.Equal(new DateTimeOffset(2026, 3, 31, 9, 0, 0, TimeSpan.Zero), next);
    }
}
