using System.Globalization;
using DeskCal.Core.Calendars;

namespace DeskCal.Core;

public enum RepeatKind
{
    None = 0,
    Daily,
    Weekly,
    Monthly,
    Yearly,
    /// <summary>Every Monday-Friday (평일).</summary>
    Weekday,
    /// <summary>The last day of every month (월말).</summary>
    MonthLastDay,
    /// <summary>The last day of March, June, September and December (분기말).</summary>
    SeasonLastDay,
    /// <summary>Monday-Friday except public holidays (법정 근무일).</summary>
    LegalWorkDay,
    /// <summary>The same lunar month and day every year (음력 매년).</summary>
    LunarYearly,
    /// <summary>Review schedule after the Ebbinghaus forgetting curve: the day itself, +1, +2, +4, +7, +15 and +30 days.</summary>
    Ebbinghaus,
}

public enum MonthlyMode { ByDate = 0, ByWeekday = 1 }

/// <summary>
/// A repeat rule. Persisted in two event columns: RFC 5545 style text for the standard kinds
/// (<c>FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=10</c>) and a short keyword text for the app-specific kinds
/// (<c>weekday;COUNT=5</c>).
/// </summary>
public sealed record RecurrenceRule
{
    public static readonly RecurrenceRule None = new();

    public RepeatKind Kind { get; init; } = RepeatKind.None;
    /// <summary>Every N days/weeks/months/years (Monthly, Yearly, Daily, Weekly, MonthLastDay, LunarYearly).</summary>
    public int Interval { get; init; } = 1;
    /// <summary>Weekly: bit i = DayOfWeek i (Sunday = bit 0). 0 means "the start date's weekday".</summary>
    public int WeekdaysMask { get; init; }
    public MonthlyMode Monthly { get; init; } = MonthlyMode.ByDate;
    /// <summary>Monthly by weekday: 1-4 = first..fourth, -1 = last, 0 = derive from the start date.</summary>
    public int Nth { get; init; }
    /// <summary>Stop after this many occurrences (the first one counts).</summary>
    public int? Count { get; init; }
    /// <summary>Last date (inclusive) an occurrence may fall on.</summary>
    public DateOnly? Until { get; init; }

    public bool IsRepeating => Kind != RepeatKind.None;

    // ---- serialisation ------------------------------------------------------

    private static readonly string[] Days = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    private static readonly (RepeatKind Kind, string Key)[] ExtKeys =
    [
        (RepeatKind.Weekday, "weekday"), (RepeatKind.MonthLastDay, "monthlast"), (RepeatKind.SeasonLastDay, "season"),
        (RepeatKind.LegalWorkDay, "legalworkday"), (RepeatKind.LunarYearly, "lunaryear"), (RepeatKind.Ebbinghaus, "ebbinghaus"),
    ];

    /// <summary>
    /// The (rrule, ext_rule) column values; both null for a non-repeating event. The event's start date is
    /// needed because a monthly-by-weekday rule is written as e.g. <c>BYDAY=2TU</c>.
    /// </summary>
    public (string? RRule, string? Ext) ToColumns(DateOnly start)
    {
        if (Kind == RepeatKind.None) return (null, null);
        var parts = new List<string>();

        string? freq = Kind switch
        {
            RepeatKind.Daily => "DAILY", RepeatKind.Weekly => "WEEKLY", RepeatKind.Monthly => "MONTHLY",
            RepeatKind.Yearly => "YEARLY", _ => null,
        };
        parts.Add(freq is not null ? "FREQ=" + freq : ExtKeys.First(k => k.Kind == Kind).Key);

        if (Interval > 1 && Kind != RepeatKind.Weekday && Kind != RepeatKind.LegalWorkDay
            && Kind != RepeatKind.SeasonLastDay && Kind != RepeatKind.Ebbinghaus)
            parts.Add($"INTERVAL={Interval}");
        if (Kind == RepeatKind.Weekly && WeekdaysMask != 0)
            parts.Add("BYDAY=" + string.Join(",", Enumerable.Range(0, 7).Where(i => (WeekdaysMask & (1 << i)) != 0).Select(i => Days[i])));
        if (Kind == RepeatKind.Monthly && Monthly == MonthlyMode.ByWeekday)
            parts.Add("BYDAY=" + (Nth != 0 ? Nth : NthOfStart(start)).ToString(CultureInfo.InvariantCulture) + Days[(int)start.DayOfWeek]);
        if (Count is { } c) parts.Add($"COUNT={c}");
        if (Until is { } u) parts.Add("UNTIL=" + u.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

        string text = string.Join(";", parts);
        return freq is not null ? (text, null) : (null, text);
    }

    internal static int NthOfStart(DateOnly start)
    {
        int n = (start.Day - 1) / 7 + 1;
        return n >= 5 ? -1 : n;                 // a 5th weekday is "the last one": never skips short months
    }

    /// <summary>Parse the two columns. Returns false (and <see cref="None"/>) for unrecognised text.</summary>
    public static bool TryParse(string? rrule, string? ext, out RecurrenceRule rule)
    {
        rule = None;
        if (string.IsNullOrWhiteSpace(rrule) && string.IsNullOrWhiteSpace(ext)) return true;

        string text = !string.IsNullOrWhiteSpace(rrule) ? rrule! : ext!;
        var kv = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (kv.Length == 0) return false;

        RepeatKind kind = RepeatKind.None;
        int interval = 1, mask = 0, nth = 0;
        MonthlyMode mode = MonthlyMode.ByDate;
        int? count = null;
        DateOnly? until = null;

        foreach (var part in kv)
        {
            int eq = part.IndexOf('=');
            string key = (eq < 0 ? part : part[..eq]).Trim().ToUpperInvariant();
            string val = eq < 0 ? "" : part[(eq + 1)..].Trim();

            if (eq < 0)
            {
                var k = ExtKeys.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (k.Key is null) return false;
                kind = k.Kind;
                continue;
            }
            switch (key)
            {
                case "FREQ":
                    kind = val.ToUpperInvariant() switch
                    {
                        "DAILY" => RepeatKind.Daily, "WEEKLY" => RepeatKind.Weekly,
                        "MONTHLY" => RepeatKind.Monthly, "YEARLY" => RepeatKind.Yearly, _ => RepeatKind.None,
                    };
                    if (kind == RepeatKind.None) return false;
                    break;
                case "INTERVAL":
                    if (!int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval < 1) return false;
                    break;
                case "COUNT":
                    if (!int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out int c) || c < 1) return false;
                    count = c;
                    break;
                case "UNTIL":
                    // accept yyyyMMdd or yyyyMMddTHHmmssZ; only the date matters
                    if (val.Length < 8 || !DateOnly.TryParseExact(val[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u)) return false;
                    until = u;
                    break;
                case "BYDAY":
                    foreach (var tok in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string name = tok.Length >= 2 ? tok[^2..].ToUpperInvariant() : "";
                        int idx = Array.IndexOf(Days, name);
                        if (idx < 0) return false;
                        mask |= 1 << idx;
                        string ord = tok[..^2];
                        if (ord.Length > 0)
                        {
                            if (!int.TryParse(ord, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out nth) || nth == 0 || nth < -1 || nth > 4) return false;
                            mode = MonthlyMode.ByWeekday;
                        }
                    }
                    break;
                default:
                    return false;
            }
        }
        if (kind == RepeatKind.None) return false;

        rule = new RecurrenceRule
        {
            Kind = kind, Interval = interval, Count = count, Until = until,
            // BYDAY on a weekly rule is the weekday set; on a monthly rule it only selects the mode
            WeekdaysMask = kind == RepeatKind.Weekly ? mask : 0,
            Monthly = kind == RepeatKind.Monthly ? mode : MonthlyMode.ByDate,
            Nth = kind == RepeatKind.Monthly && mode == MonthlyMode.ByWeekday ? nth : 0,
        };
        return true;
    }

    public static RecurrenceRule From(EventEntry e) => TryParse(e.RRule, e.ExtRule, out var r) ? r : None;
}
