using System.Collections.Concurrent;

namespace DeskCal.Core.Calendars;

public enum HolidayKind
{
    /// <summary>Statutory public holiday (공휴일).</summary>
    Public,
    /// <summary>Substitute holiday (대체공휴일) granted when a holiday falls on a weekend or overlaps another.</summary>
    Substitute,
    /// <summary>Temporary holiday or election day (임시공휴일, 선거일) - only known from announcements.</summary>
    Temporary,
    /// <summary>Memorial day or traditional day that is not a day off (기념일, 명절 외 절일).</summary>
    Observance,
}

public sealed record Holiday(DateOnly Date, string Name, HolidayKind Kind)
{
    public bool IsDayOff => Kind != HolidayKind.Observance;
}

/// <summary>A correction to the computed table: add a day, or remove what the rules produced.</summary>
public sealed record HolidayOverride(DateOnly Date, string Name, bool IsDayOff, bool Remove = false);

/// <summary>
/// South Korean holidays derived from the statutory rules: fixed solar dates, lunar-calendar holidays
/// (설날, 추석, 부처님오신날) and the substitute-holiday rules as they changed over time. Temporary holidays and
/// election days cannot be derived, so known ones are built in and <see cref="HolidayOverride"/> lets the user add new ones.
/// Rules are implemented for 2000-2050.
/// </summary>
public sealed class KoreanHolidays
{
    public const int MinYear = 2000, MaxYear = 2050;

    // Temporary holidays (임시공휴일) and election days that are not derivable. Known as of 2026-06.
    private static readonly HolidayOverride[] BuiltInExtras =
    [
        new(new(2015, 8, 14), "임시공휴일", true), new(new(2016, 5, 6), "임시공휴일", true),
        new(new(2017, 5, 9), "대통령선거일", true), new(new(2017, 10, 2), "임시공휴일", true),
        new(new(2018, 6, 13), "지방선거일", true), new(new(2020, 4, 15), "국회의원선거일", true),
        new(new(2020, 8, 17), "임시공휴일", true), new(new(2022, 3, 9), "대통령선거일", true),
        new(new(2022, 6, 1), "지방선거일", true), new(new(2023, 10, 2), "임시공휴일", true),
        new(new(2024, 4, 10), "국회의원선거일", true), new(new(2024, 10, 1), "국군의 날(임시공휴일)", true),
        new(new(2025, 1, 27), "임시공휴일", true), new(new(2025, 6, 3), "대통령선거일", true),
        new(new(2026, 6, 3), "지방선거일", true),
    ];

    private readonly List<HolidayOverride> _overrides;
    private readonly ConcurrentDictionary<int, Dictionary<DateOnly, List<Holiday>>> _cache = new();

    public KoreanHolidays(IEnumerable<HolidayOverride>? overrides = null)
    {
        _overrides = [.. BuiltInExtras, .. overrides ?? []];
    }

    /// <summary>Holidays on a date, day-offs first. Empty outside 2000-2050.</summary>
    public IReadOnlyList<Holiday> On(DateOnly date)
    {
        if (date.Year < MinYear || date.Year > MaxYear) return [];
        return Year(date.Year).TryGetValue(date, out var list) ? list : [];
    }

    public IReadOnlyList<Holiday> ForYear(int year) =>
        year < MinYear || year > MaxYear
            ? []
            : Year(year).Values.SelectMany(l => l).OrderBy(h => h.Date).ThenByDescending(h => h.IsDayOff).ToList();

    public bool IsDayOff(DateOnly date) => On(date).Any(h => h.IsDayOff);

    private Dictionary<DateOnly, List<Holiday>> Year(int year) => _cache.GetOrAdd(year, Build);

    // ---- construction ------------------------------------------------------

    private sealed record Base(Holiday Holiday, string Group);

    private Dictionary<DateOnly, List<Holiday>> Build(int year)
    {
        var all = new List<Base>();
        void Add(DateOnly d, string name, HolidayKind kind, string? group = null) =>
            all.Add(new Base(new Holiday(d, name, kind), group ?? name));

        // ---- fixed solar dates
        Add(new(year, 1, 1), "신정", HolidayKind.Public);
        Add(new(year, 3, 1), "삼일절", HolidayKind.Public);
        Add(new(year, 5, 5), "어린이날", HolidayKind.Public);
        Add(new(year, 6, 6), "현충일", HolidayKind.Public);
        Add(new(year, 8, 15), "광복절", HolidayKind.Public);
        Add(new(year, 10, 3), "개천절", HolidayKind.Public);
        if (year >= 2013) Add(new(year, 10, 9), "한글날", HolidayKind.Public);
        Add(new(year, 12, 25), "성탄절", HolidayKind.Public);
        // used to be days off
        Add(new(year, 7, 17), "제헌절", year <= 2007 ? HolidayKind.Public : HolidayKind.Observance);
        Add(new(year, 4, 5), "식목일", year <= 2005 ? HolidayKind.Public : HolidayKind.Observance);

        // ---- lunar-calendar holidays
        if (KoreanLunar.ToSolar(year, 1, 1) is { } seollal)
        {
            Add(seollal.AddDays(-1), "설날 연휴", HolidayKind.Public, "설날");
            Add(seollal, "설날", HolidayKind.Public);
            Add(seollal.AddDays(1), "설날 연휴", HolidayKind.Public, "설날");
        }
        if (KoreanLunar.ToSolar(year, 8, 15) is { } chuseok)
        {
            Add(chuseok.AddDays(-1), "추석 연휴", HolidayKind.Public, "추석");
            Add(chuseok, "추석", HolidayKind.Public);
            Add(chuseok.AddDays(1), "추석 연휴", HolidayKind.Public, "추석");
        }
        if (KoreanLunar.ToSolar(year, 4, 8) is { } buddha) Add(buddha, "부처님오신날", HolidayKind.Public);

        // ---- days that are not days off
        Add(new(year, 5, 1), "근로자의 날", HolidayKind.Observance);
        Add(new(year, 5, 8), "어버이날", HolidayKind.Observance);
        Add(new(year, 5, 15), "스승의 날", HolidayKind.Observance);
        Add(new(year, 5, 21), "부부의 날", HolidayKind.Observance);
        Add(new(year, 10, 1), "국군의 날", HolidayKind.Observance);
        Add(NthWeekday(year, 5, DayOfWeek.Monday, 3), "성년의 날", HolidayKind.Observance);
        if (KoreanLunar.ToSolar(year, 1, 15) is { } daeboreum) Add(daeboreum, "정월대보름", HolidayKind.Observance);
        if (KoreanLunar.ToSolar(year, 5, 5) is { } dano) Add(dano, "단오", HolidayKind.Observance);
        if (KoreanLunar.ToSolar(year, 7, 7) is { } chilseok) Add(chilseok, "칠석", HolidayKind.Observance);

        // ---- announced days: temporary holidays, election days, and the user's own corrections
        foreach (var o in _overrides.Where(o => o.Date.Year == year))
        {
            all.RemoveAll(b => b.Holiday.Date == o.Date && o.Remove);
            if (o.Remove) continue;
            Add(o.Date, o.Name, o.IsDayOff ? HolidayKind.Temporary : HolidayKind.Observance, "override:" + o.Date);
        }

        AddSubstitutes(year, all);

        var map = new Dictionary<DateOnly, List<Holiday>>();
        foreach (var b in all.OrderByDescending(b => b.Holiday.IsDayOff))
            (map.TryGetValue(b.Holiday.Date, out var l) ? l : map[b.Holiday.Date] = []).Add(b.Holiday);
        return map;
    }

    /// <summary>
    /// 대체공휴일. Which holidays qualify, and whether Saturday counts, depends on the date (laws changed in
    /// 2014, 2021 and 2023). A group (e.g. the 3-day 설날 block) gets at most one substitute, and two groups that
    /// overlap on the same day share one (as with 어린이날 + 부처님오신날 in 2025).
    /// </summary>
    private static void AddSubstitutes(int year, List<Base> all)
    {
        // (group, first date in force, Saturday also counts from)
        (string Group, DateOnly Since, DateOnly SaturdayFrom)[] rules =
        [
            ("설날", new(2014, 1, 1), DateOnly.MaxValue), ("추석", new(2014, 1, 1), DateOnly.MaxValue),
            ("어린이날", new(2014, 1, 1), new(2021, 1, 1)),
            ("삼일절", new(2021, 7, 7), new(2021, 7, 7)), ("광복절", new(2021, 7, 7), new(2021, 7, 7)),
            ("개천절", new(2021, 7, 7), new(2021, 7, 7)), ("한글날", new(2021, 7, 7), new(2021, 7, 7)),
            ("부처님오신날", new(2023, 5, 4), new(2023, 5, 4)), ("성탄절", new(2023, 5, 4), new(2023, 5, 4)),
        ];

        var dayOffs = new HashSet<DateOnly>(all.Where(b => b.Holiday.IsDayOff).Select(b => b.Holiday.Date));
        var claimed = new HashSet<DateOnly>();
        var groups = rules
            .Select(r => (Rule: r, Days: all.Where(b => b.Group == r.Group && b.Holiday.IsDayOff).Select(b => b.Holiday.Date).OrderBy(d => d).ToList()))
            .Where(g => g.Days.Count > 0)
            .OrderBy(g => g.Days[0]);

        foreach (var (rule, days) in groups)
        {
            var triggers = days.Where(d =>
                d >= rule.Since &&
                (d.DayOfWeek == DayOfWeek.Sunday
                 || (d.DayOfWeek == DayOfWeek.Saturday && d >= rule.SaturdayFrom)
                 || all.Any(b => b.Holiday.Date == d && b.Holiday.IsDayOff && b.Group != rule.Group)))
                .ToList();
            if (triggers.Count == 0 || triggers.All(claimed.Contains)) continue;

            var sub = days[^1].AddDays(1);
            while (sub.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || dayOffs.Contains(sub)) sub = sub.AddDays(1);
            foreach (var t in triggers) claimed.Add(t);
            dayOffs.Add(sub);
            if (sub.Year == year)
                all.Add(new Base(new Holiday(sub, rule.Group + " 대체공휴일", HolidayKind.Substitute), "sub:" + rule.Group));
        }
    }

    private static DateOnly NthWeekday(int year, int month, DayOfWeek dow, int n)
    {
        var d = new DateOnly(year, month, 1);
        while (d.DayOfWeek != dow) d = d.AddDays(1);
        return d.AddDays(7 * (n - 1));
    }
}
