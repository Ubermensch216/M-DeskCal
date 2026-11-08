using System.Globalization;
using System.Text;

namespace DeskCal.Core;

public enum SearchKind { Any, Item, Event }

public enum DoneFilter { Any, Done, NotDone }

/// <summary>
/// What to look for. Blank <see cref="Text"/> matches everything (so the filters alone can be used).
/// <see cref="TextColor"/>: null = any colour, "" = items without a custom text colour, otherwise a hex like "#FF6B6B"
/// (events are compared on their bar colour). A done/not-done filter leaves events out, because events have no done flag.
/// </summary>
public sealed record SearchQuery(
    string Text = "", DateOnly? From = null, DateOnly? To = null, SearchKind Kind = SearchKind.Any,
    DoneFilter Done = DoneFilter.Any, string? TextColor = null, bool IncludeDeleted = false);

/// <summary>One search result. For a repeating event <see cref="Date"/> is its first occurrence inside the searched range.</summary>
public sealed record SearchHit(
    SearchKind Kind, string Id, DateOnly Date, string Text, bool Done, bool Deleted, string? TextColor, bool Repeating);

public static class SearchEngine
{
    /// <summary>
    /// Substring search (every whitespace-separated word must appear, case-insensitive, Unicode-normalised so that
    /// precomposed and decomposed Hangul match). FTS5 was considered and rejected: its tokenisers split on words, so
    /// a Korean query such as "회의" would not find "주간회의", and a personal calendar is small enough to scan.
    /// Results are newest first and capped at <paramref name="limit"/>.
    /// </summary>
    public static List<SearchHit> Search(
        IEnumerable<Item> items, IEnumerable<EventEntry> events, SearchQuery q,
        IReadOnlyDictionary<string, IReadOnlySet<DateOnly>>? skips = null,
        Func<DateOnly, bool>? isDayOff = null, int limit = 500)
    {
        var words = Words(q.Text);
        var hits = new List<SearchHit>();

        if (q.Kind != SearchKind.Event)
            foreach (var item in items)
            {
                if (item.Deleted && !q.IncludeDeleted) continue;
                if (!DateOnly.TryParseExact(item.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                if (q.From is { } f && day < f) continue;
                if (q.To is { } t && day > t) continue;
                if (q.Done == DoneFilter.Done && !item.Done) continue;
                if (q.Done == DoneFilter.NotDone && item.Done) continue;
                if (!ColorMatches(q.TextColor, item.TextColor)) continue;
                if (!Matches(item.Content, words)) continue;
                hits.Add(new SearchHit(SearchKind.Item, item.Id, day, item.Content, item.Done, item.Deleted, item.TextColor, false));
            }

        if (q.Kind != SearchKind.Item && q.Done == DoneFilter.Any)
            foreach (var e in events)
            {
                if (e.Deleted && !q.IncludeDeleted) continue;
                if (!ColorMatches(q.TextColor, e.Color)) continue;
                if (!Matches(e.Title + "\n" + e.Body, words)) continue;
                if (!EventTime.TryParse(e.Start, out var start, out _)) continue;

                bool repeating = RecurrenceRule.From(e).IsRepeating;
                DateOnly date = start;
                if (q.From is not null || q.To is not null)
                {
                    // Look for the first occurrence inside the range. Open ends are closed at the event's own
                    // start / one year after the range start, so a repeating series is never expanded without bound.
                    var from = q.From ?? start;
                    var to = q.To ?? (q.From is { } f2 ? f2.AddYears(1) : start);
                    if (to < from) continue;
                    var first = EventOccurrences.Expand([e], skips, from, to, isDayOff).FirstOrDefault();
                    if (first is null) continue;
                    date = first.Date;
                }
                hits.Add(new SearchHit(SearchKind.Event, e.Id, date, e.Title, false, e.Deleted, e.Color, repeating));
            }

        return hits.OrderByDescending(h => h.Date).ThenBy(h => h.Kind).ThenBy(h => h.Text, StringComparer.CurrentCulture).Take(limit).ToList();
    }

    private static bool ColorMatches(string? wanted, string? actual) =>
        wanted is null || string.Equals(wanted, actual ?? "", StringComparison.OrdinalIgnoreCase);

    private static string[] Words(string? text) =>
        Normalize(text ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static bool Matches(string haystack, string[] words)
    {
        if (words.Length == 0) return true;
        var h = Normalize(haystack);
        foreach (var w in words) if (!h.Contains(w, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string Normalize(string s) => s.Normalize(NormalizationForm.FormC);
}
