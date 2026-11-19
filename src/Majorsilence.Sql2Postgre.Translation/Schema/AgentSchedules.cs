using System.Globalization;
using Majorsilence.Sql2Postgre.Model;

namespace Majorsilence.Sql2Postgre.Translation.Schema;

/// <summary>
/// SQL Server Agent schedules as cron expressions (minute hour day-of-month month day-of-week), for
/// pg_cron. Daily (every day), weekly (every week, on its days) and monthly (every month, on a day)
/// schedules map, at one time of day or every N minutes or hours between the start and end times.
/// What cron cannot say (once, on Agent start, when idle, every N days or weeks, relative days
/// such as "the second Tuesday", every N seconds) gives null and a reason.
/// </summary>
public static class AgentSchedules
{
    public static string? Cron(AgentSchedule s, out string? problem)
    {
        problem = null;
        string? days = s.FreqType switch
        {
            4 when s.FreqInterval <= 1 => "* * *",
            4 => Fail($"it runs every {s.FreqInterval} days, which cron cannot count", out problem),
            8 when s.FreqRecurrenceFactor > 1 => Fail($"it runs every {s.FreqRecurrenceFactor} weeks, which cron cannot count", out problem),
            8 => WeekDays(s.FreqInterval) is { Length: > 0 } list ? $"* * {list}" : Fail("it names no day of the week", out problem),
            16 when s.FreqRecurrenceFactor > 1 => Fail($"it runs every {s.FreqRecurrenceFactor} months, which cron cannot count from its start date", out problem),
            16 when s.FreqInterval is >= 1 and <= 31 => $"{s.FreqInterval.ToString(CultureInfo.InvariantCulture)} * *",
            16 => Fail("its day of the month is out of range", out problem),
            32 => Fail("it runs on a relative day (the first, second, … or last weekday of the month), which cron cannot say", out problem),
            1 => Fail("it runs once, at a fixed date and time", out problem),
            64 => Fail("it runs when SQL Server Agent starts", out problem),
            128 => Fail("it runs when the computer is idle", out problem),
            _ => Fail($"its frequency type {s.FreqType} is not one SQL Server documents", out problem),
        };
        if (days is null)
            return null;

        int startHour = s.ActiveStartTime / 10000, startMinute = s.ActiveStartTime / 100 % 100;
        int endHour = s.ActiveEndTime / 10000;
        bool wholeDay = s.ActiveStartTime == 0 && s.ActiveEndTime >= 235900;
        string Number(int n) => n.ToString(CultureInfo.InvariantCulture);
        string? time = s.FreqSubdayType switch
        {
            0 or 1 => $"{Number(startMinute)} {Number(startHour)}",
            4 when s.FreqSubdayInterval is >= 1 and < 60 && 60 % s.FreqSubdayInterval == 0 && startMinute % s.FreqSubdayInterval == 0
                => $"*/{Number(s.FreqSubdayInterval)} {(wholeDay ? "*" : $"{Number(startHour)}-{Number(endHour)}")}",
            4 => Fail($"it runs every {s.FreqSubdayInterval} minutes from {startHour:00}:{startMinute:00}, which cron's minute steps do not line up with", out problem),
            8 when s.FreqSubdayInterval is >= 1 and < 24 && (wholeDay ? 24 % s.FreqSubdayInterval == 0 : true)
                => $"{Number(startMinute)} {(wholeDay ? "*" : $"{Number(startHour)}-{Number(endHour)}")}/{Number(s.FreqSubdayInterval)}",
            8 => Fail($"it runs every {s.FreqSubdayInterval} hours, which cron's hour steps do not line up with", out problem),
            2 => Fail($"it runs every {s.FreqSubdayInterval} seconds", out problem),
            _ => Fail($"its interval type {s.FreqSubdayType} is not one SQL Server documents", out problem),
        };
        return time is null ? null : $"{time} {days}";
    }

    /// <summary>freq_interval of a weekly schedule: Sunday 1, Monday 2, … Saturday 64, as cron's 0–6.</summary>
    private static string WeekDays(int mask) =>
        string.Join(',', Enumerable.Range(0, 7).Where(d => (mask & (1 << d)) != 0).Select(d => d.ToString(CultureInfo.InvariantCulture)));

    private static string? Fail(string reason, out string? problem)
    {
        problem = reason;
        return null;
    }
}
