using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeskCal.Core.Exchange;

/// <summary>A reminder as written to / read from a file: minutes before the start, plus an optional clock time for all-day owners.</summary>
public sealed record ExReminder(int OffsetMinutes, string? CustomTime);

public sealed record ExItem(
    string Id, string Day, string Content, bool Done, string? BgColor, string? TextColor, int Sort,
    IReadOnlyList<ExReminder> Reminders);

/// <param name="Exceptions">Skipped occurrence dates ("yyyy-MM-dd") of a repeating event.</param>
public sealed record ExEvent(
    string Id, string Title, string Body, string Start, string? End, bool AllDay, string? RRule, string? ExtRule, string? Color,
    IReadOnlyList<string> Exceptions, IReadOnlyList<ExReminder> Reminders);

public sealed record ExNote(string Id, string Title, string Content, string? Color);

/// <summary>Everything one file can carry. CSV and ICS only carry items and events.</summary>
public sealed record ExchangeBundle(
    IReadOnlyList<ExItem> Items, IReadOnlyList<ExEvent> Events, IReadOnlyList<Countdown> Countdowns, IReadOnlyList<ExNote> Notes)
{
    public static ExchangeBundle Empty { get; } = new([], [], [], []);
    public int Total => Items.Count + Events.Count + Countdowns.Count + Notes.Count;
}

/// <summary>A parsed file: the cleaned data plus human-readable notes about anything that was skipped or simplified.</summary>
public sealed record ExchangeReadResult(ExchangeBundle Bundle, IReadOnlyList<string> Warnings);

/// <summary>Raised for a file that cannot be used at all (wrong format, unreadable). The message is meant for the user.</summary>
public sealed class ExchangeFormatException(string message) : Exception(message);

public static partial class ExchangeIds
{
    /// <summary>
    /// A repeatable id for records that came without one (CSV rows, ICS entries): the same source gives the same id, so
    /// importing the same file twice adds nothing the second time.
    /// </summary>
    public static string Stable(string prefix, string key) =>
        prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();

    public static string New() => Guid.NewGuid().ToString("N");

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    public static string? CleanColor(string? c) => c is not null && HexColor().IsMatch(c.Trim()) ? c.Trim().ToUpperInvariant() : null;
}

/// <summary>
/// Validates and normalises whatever a reader produced, so every format shares one set of rules and the database never
/// sees bad dates, runaway text or unusable repeat rules. Problems become warnings; the rest of the file still imports.
/// </summary>
public static class ExchangeCleaner
{
    public const int MaxText = 2000, MaxTitle = 500, MaxBody = 20000, MaxReminders = 10, MaxRecords = 200_000;

    public static ExchangeReadResult Clean(ExchangeBundle raw, IEnumerable<string>? earlierWarnings = null)
    {
        var warnings = new List<string>(earlierWarnings ?? []);
        var seen = new HashSet<string>();
        int skipped = 0;

        var items = new List<ExItem>();
        foreach (var i in raw.Items ?? [])
        {
            if (items.Count >= MaxRecords) { warnings.Add("할 일이 너무 많아 일부만 가져옵니다."); break; }
            string content = (i.Content ?? "").ReplaceLineEndings(" ").Trim();
            if (content.Length == 0 || !CountdownCalc.TryParseDate(i.Day, out var day)) { skipped++; continue; }
            if (content.Length > MaxText) content = content[..MaxText];
            string id = string.IsNullOrWhiteSpace(i.Id) ? ExchangeIds.Stable("item-", $"{i.Day}|{content}|{items.Count}") : i.Id.Trim();
            if (!seen.Add("i:" + id)) { skipped++; continue; }
            items.Add(new ExItem(id, CountdownCalc.Format(day), content, i.Done, ExchangeIds.CleanColor(i.BgColor), ExchangeIds.CleanColor(i.TextColor),
                i.Sort, CleanReminders(i.Reminders)));
        }

        var events = new List<ExEvent>();
        foreach (var e in raw.Events ?? [])
        {
            if (events.Count >= MaxRecords) { warnings.Add("일정이 너무 많아 일부만 가져옵니다."); break; }
            string title = (e.Title ?? "").ReplaceLineEndings(" ").Trim();
            if (title.Length == 0) title = "(제목 없음)";
            if (title.Length > MaxTitle) title = title[..MaxTitle];
            if (!EventTime.TryParse(e.Start, out var d0, out var t0)) { skipped++; warnings.Add($"일정 '{Short(title)}': 시작 날짜를 읽을 수 없어 건너뜀 ({e.Start})"); continue; }
            string start = EventTime.Format(d0, t0);

            string? end = null;
            if (!string.IsNullOrWhiteSpace(e.End))
            {
                if (EventTime.TryParse(e.End, out var d1, out var t1) && (t0 is null) == (t1 is null)
                    && (d1 > d0 || (d1 == d0 && t0 is { } a && t1 is { } b && b > a)))
                    end = EventTime.Format(d1, t1);
                else
                    warnings.Add($"일정 '{Short(title)}': 종료 시각이 올바르지 않아 무시함 ({e.End})");
            }

            string? rr = e.RRule, ext = e.ExtRule;
            if (!RecurrenceRule.TryParse(rr, ext, out _))
            {
                warnings.Add($"일정 '{Short(title)}': 반복 규칙을 해석할 수 없어 한 번만 발생하는 일정으로 가져옴 ({rr ?? ext})");
                rr = ext = null;
            }
            bool repeating = !string.IsNullOrWhiteSpace(rr) || !string.IsNullOrWhiteSpace(ext);

            string body = e.Body ?? "";
            if (body.Length > MaxBody) body = body[..MaxBody];
            string id = string.IsNullOrWhiteSpace(e.Id) ? ExchangeIds.Stable("event-", $"{start}|{title}|{events.Count}") : e.Id.Trim();
            if (!seen.Add("e:" + id)) { skipped++; continue; }

            var exceptions = repeating
                ? (e.Exceptions ?? []).Where(x => CountdownCalc.TryParseDate(x, out _)).Distinct().ToList()
                : [];
            events.Add(new ExEvent(id, title, body, start, end, t0 is null, repeating ? NullIfBlank(rr) : null, repeating ? NullIfBlank(ext) : null,
                ExchangeIds.CleanColor(e.Color), exceptions, CleanReminders(e.Reminders)));
        }

        var countdowns = new List<Countdown>();
        foreach (var c in raw.Countdowns ?? [])
        {
            if (CountdownCalc.Validate(c.Title, c.Date, c.Lunar) is not null || !Enum.IsDefined(c.Type)) { skipped++; continue; }
            string id = string.IsNullOrWhiteSpace(c.Id) ? ExchangeIds.Stable("cd-", $"{c.Title}|{c.Date}|{countdowns.Count}") : c.Id.Trim();
            if (!seen.Add("c:" + id)) { skipped++; continue; }
            countdowns.Add(c with { Id = id, Title = c.Title.Trim() });
        }

        var notes = new List<ExNote>();
        foreach (var n in raw.Notes ?? [])
        {
            if (string.IsNullOrWhiteSpace(n.Title) && string.IsNullOrWhiteSpace(n.Content)) { skipped++; continue; }
            string id = string.IsNullOrWhiteSpace(n.Id) ? ExchangeIds.Stable("note-", $"{n.Title}|{n.Content}|{notes.Count}") : n.Id.Trim();
            if (!seen.Add("n:" + id)) { skipped++; continue; }
            notes.Add(n with { Id = id, Title = n.Title ?? "", Content = (n.Content ?? "").Length > MaxBody ? n.Content![..MaxBody] : n.Content ?? "", Color = ExchangeIds.CleanColor(n.Color) });
        }

        if (skipped > 0) warnings.Add($"읽을 수 없거나 중복된 항목 {skipped}건을 건너뜀");
        return new ExchangeReadResult(new ExchangeBundle(items, events, countdowns, notes), warnings);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Short(string s) => s.Ellipsize(30);

    private static List<ExReminder> CleanReminders(IReadOnlyList<ExReminder>? list)
    {
        var result = new List<ExReminder>();
        foreach (var r in list ?? [])
        {
            if (r.OffsetMinutes is < 0 or > 525_600 || result.Count >= MaxReminders) continue;
            string? clock = ReminderPlanner.TryParseClock(r.CustomTime, out var t) ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : null;
            if (!result.Any(x => x.OffsetMinutes == r.OffsetMinutes && x.CustomTime == clock)) result.Add(new ExReminder(r.OffsetMinutes, clock));
        }
        return result;
    }
}
