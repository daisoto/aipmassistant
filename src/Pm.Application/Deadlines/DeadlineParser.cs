using System.Text.RegularExpressions;
using Pm.Domain;

namespace Pm.Application.Deadlines;

public sealed record DeadlineResult(DateTimeOffset? At, DueKind Kind, string RuleName)
{
    public static DeadlineResult None(string rule = "None") => new(null, DueKind.None, rule);
    public static DeadlineResult Vague(string rule) => new(null, DueKind.Vague, rule);
}

public interface IDeadlineParser
{
    DeadlineResult Parse(string? quote, DateOnly sourceDate);
}

/// <summary>
/// Детерминированный разбор сроков. Модель возвращает только цитату («завтра до 15:00»),
/// дату считает этот класс от даты источника. Арифметика дат — не работа LLM.
/// </summary>
public sealed partial class DeadlineParser : IDeadlineParser
{
    /// <summary>Конец рабочего дня, если время в цитате не названо.</summary>
    public static readonly TimeOnly EndOfDay = new(18, 0);

    public static readonly TimeSpan Offset = TimeSpan.FromHours(5); // Asia/Yekaterinburg

    private static readonly Dictionary<string, DayOfWeek> Weekdays = new(StringComparer.Ordinal)
    {
        ["понедельник"] = DayOfWeek.Monday,
        ["вторник"] = DayOfWeek.Tuesday,
        ["сред"] = DayOfWeek.Wednesday,
        ["четверг"] = DayOfWeek.Thursday,
        ["пятниц"] = DayOfWeek.Friday,
        ["суббот"] = DayOfWeek.Saturday,
        ["воскресень"] = DayOfWeek.Sunday
    };

    private static readonly Dictionary<string, int> Months = new(StringComparer.Ordinal)
    {
        ["январ"] = 1, ["феврал"] = 2, ["март"] = 3, ["апрел"] = 4, ["ма"] = 5, ["июн"] = 6,
        ["июл"] = 7, ["август"] = 8, ["сентябр"] = 9, ["октябр"] = 10, ["ноябр"] = 11, ["декабр"] = 12
    };

    /// <summary>Формулировки, которые упоминают срок, но срока не задают. Дату из них выдумывать нельзя.</summary>
    private static readonly string[] VagueMarkers =
    [
        "срок не критичн", "не критичн", "позже", "в следующем спринте", "следующем спринте",
        "в следующем плановом цикле", "следующем плановом цикле", "в ближайшее время",
        "по возможности", "когда будет готово", "на следующей неделе"
    ];

    public DeadlineResult Parse(string? quote, DateOnly sourceDate)
    {
        if (string.IsNullOrWhiteSpace(quote)) return DeadlineResult.None();

        var q = quote.ToLowerInvariant().Replace('ё', 'е').Trim();

        foreach (var marker in VagueMarkers)
            if (q.Contains(marker, StringComparison.Ordinal))
                return DeadlineResult.Vague("Vague");

        var time = ExtractTime(q);

        // «до 16:00 10 сентября», «14 сентября», «до 10 сентября»
        var dm = DayMonthRegex().Match(q);
        if (dm.Success)
        {
            var day = int.Parse(dm.Groups["d"].Value);
            var monthName = dm.Groups["m"].Value;
            var month = Months.FirstOrDefault(kv => monthName.StartsWith(kv.Key, StringComparison.Ordinal)).Value;
            if (month > 0 && day is >= 1 and <= 31)
            {
                var year = sourceDate.Year;
                var candidate = SafeDate(year, month, day);
                if (candidate < sourceDate.AddDays(-30)) candidate = SafeDate(year + 1, month, day);
                return new DeadlineResult(
                    At(candidate, time ?? EndOfDay),
                    DueKind.Explicit,
                    time is null ? "ExplicitDate" : "ExplicitDateTime");
            }
        }

        // «в течение 2 рабочих дней»
        var bd = BusinessDaysRegex().Match(q);
        if (bd.Success)
        {
            var n = int.Parse(bd.Groups["n"].Value);
            var d = sourceDate;
            while (n > 0)
            {
                d = d.AddDays(1);
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) n--;
            }

            return new DeadlineResult(At(d, time ?? EndOfDay), DueKind.Explicit, "BusinessDays");
        }

        if (q.Contains("послезавтра", StringComparison.Ordinal))
            return new DeadlineResult(At(sourceDate.AddDays(2), time ?? EndOfDay), DueKind.Explicit, "DayAfterTomorrow");

        if (q.Contains("завтра", StringComparison.Ordinal))
            return new DeadlineResult(
                At(sourceDate.AddDays(1), time ?? EndOfDay),
                DueKind.Explicit,
                time is null ? "TomorrowEod" : "TomorrowAtTime");

        if (q.Contains("сегодня", StringComparison.Ordinal))
            return new DeadlineResult(At(sourceDate, time ?? EndOfDay), DueKind.Explicit, "Today");

        if (q.Contains("конца недели", StringComparison.Ordinal) || q.Contains("конце недели", StringComparison.Ordinal))
            return new DeadlineResult(At(NextWeekday(sourceDate, DayOfWeek.Friday, true), time ?? EndOfDay),
                DueKind.Explicit, "EndOfWeek");

        foreach (var (stem, dow) in Weekdays)
        {
            if (!q.Contains(stem, StringComparison.Ordinal)) continue;
            return new DeadlineResult(At(NextWeekday(sourceDate, dow, true), time ?? EndOfDay),
                DueKind.Explicit, "NextWeekday");
        }

        // Время без дня («до 14:00») трактуем как сегодня.
        if (time is not null)
            return new DeadlineResult(At(sourceDate, time.Value), DueKind.Explicit, "TodayAtTime");

        return DeadlineResult.None("Unrecognized");
    }

    private static DateOnly SafeDate(int year, int month, int day)
    {
        var max = DateTime.DaysInMonth(year, month);
        return new DateOnly(year, month, Math.Min(day, max));
    }

    private static DateTimeOffset At(DateOnly date, TimeOnly time)
        => new(date.Year, date.Month, date.Day, time.Hour, time.Minute, 0, Offset);

    /// <summary>Ближайший будущий указанный день недели. Тот же день считается будущим только если allowSame.</summary>
    private static DateOnly NextWeekday(DateOnly from, DayOfWeek target, bool allowSame)
    {
        var delta = ((int)target - (int)from.DayOfWeek + 7) % 7;
        if (delta == 0 && !allowSame) delta = 7;
        if (delta == 0 && allowSame) delta = 7; // «до среды», сказанное в среду, означает следующую
        return from.AddDays(delta);
    }

    private static TimeOnly? ExtractTime(string q)
    {
        if (q.Contains("конца дня", StringComparison.Ordinal) || q.Contains("конце дня", StringComparison.Ordinal))
            return EndOfDay;
        var m = TimeRegex().Match(q);
        if (!m.Success) return null;
        var h = int.Parse(m.Groups["h"].Value);
        var mi = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value) : 0;
        if (h > 23 || mi > 59) return null;
        return new TimeOnly(h, mi);
    }

    [GeneratedRegex(@"(?<h>\d{1,2})[:.](?<m>\d{2})")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"(?<d>\d{1,2})\s+(?<m>[а-я]{3,})")]
    private static partial Regex DayMonthRegex();

    [GeneratedRegex(@"в течение\s+(?<n>\d+)\s+рабоч")]
    private static partial Regex BusinessDaysRegex();
}
