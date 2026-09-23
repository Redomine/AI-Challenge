using DesignerAssistant.Models;

namespace DesignerAssistant.Scheduling;

public static class ScheduleCalculator
{
    public static DateTimeOffset GetNextRun(
        SchedulePeriod period,
        TimeOnly time,
        DayOfWeek? dayOfWeek,
        int? dayOfMonth,
        DateTimeOffset after,
        TimeZoneInfo timeZone)
    {
        var localAfter = TimeZoneInfo.ConvertTime(after, timeZone);
        var date = localAfter.Date;

        DateTime candidate = period switch
        {
            SchedulePeriod.Daily => date.Add(time.ToTimeSpan()),
            SchedulePeriod.Weekly => NextWeekly(date, time, dayOfWeek ?? DayOfWeek.Monday),
            SchedulePeriod.Monthly => NextMonthly(date, time, dayOfMonth ?? 1),
            _ => throw new ArgumentOutOfRangeException(nameof(period))
        };

        if (candidate <= localAfter.DateTime)
        {
            candidate = period switch
            {
                SchedulePeriod.Daily => candidate.AddDays(1),
                SchedulePeriod.Weekly => candidate.AddDays(7),
                SchedulePeriod.Monthly => NextMonth(candidate, time, dayOfMonth ?? 1),
                _ => candidate
            };
        }

        if (timeZone.IsInvalidTime(candidate)) candidate = candidate.AddHours(1);
        var offset = timeZone.GetUtcOffset(candidate);
        return new DateTimeOffset(candidate, offset);
    }

    private static DateTime NextWeekly(DateTime date, TimeOnly time, DayOfWeek day)
    {
        var days = ((int)day - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(days).Add(time.ToTimeSpan());
    }

    private static DateTime NextMonthly(DateTime date, TimeOnly time, int requestedDay)
    {
        var day = Math.Min(Math.Clamp(requestedDay, 1, 31), DateTime.DaysInMonth(date.Year, date.Month));
        return new DateTime(date.Year, date.Month, day).Add(time.ToTimeSpan());
    }

    private static DateTime NextMonth(DateTime candidate, TimeOnly time, int requestedDay)
    {
        var month = new DateTime(candidate.Year, candidate.Month, 1).AddMonths(1);
        var day = Math.Min(Math.Clamp(requestedDay, 1, 31), DateTime.DaysInMonth(month.Year, month.Month));
        return new DateTime(month.Year, month.Month, day).Add(time.ToTimeSpan());
    }
}
