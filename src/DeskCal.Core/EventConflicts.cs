using System.Text;

namespace DeskCal.Core;

public enum ConflictKind { Duplicate, Overlap }

/// <summary>An existing event that clashes with a new one.</summary>
public sealed record EventConflict(ConflictKind Kind, string Title, DateOnly Date, TimeOnly? Start, TimeOnly? End)
{
    /// <summary>A sentence for the user: "고객 미팅(14:00~15:30)과 시간이 겹칩니다".</summary>
    public string Describe()
    {
        string when = Start is { } s ? $"{s:HH\\:mm}" + (End is { } e ? $"~{e:HH\\:mm}" : "") : "종일";
        return Kind == ConflictKind.Duplicate
            ? $"같은 일정「{Title}」({when})이 이미 있습니다."
            : $"「{Title}」({when})과 시간이 겹칩니다.";
    }
}

/// <summary>Is a new event a duplicate of, or does it overlap, one that is already on the calendar? Plain arithmetic, no AI.</summary>
public static class EventConflicts
{
    /// <summary>
    /// <paramref name="sameDay"/> are the existing occurrences that touch <paramref name="date"/> (from <see cref="EventOccurrences.Expand"/>).
    /// A duplicate is the same title (ignoring case and spaces) at the same time (or both all-day); an overlap needs both to have a time of day.
    /// A timed event without an end counts as <see cref="FreeTimeFinder.DefaultEventMinutes"/> long.
    /// </summary>
    public static List<EventConflict> Find(IEnumerable<EventOccurrence> sameDay, DateOnly date, string title, TimeOnly? start, TimeOnly? end)
    {
        var result = new List<EventConflict>();
        string key = Normalize(title);
        int? s = start is { } st ? Minute(st) : null;
        int? e = s is { } s0 ? (end is { } en && Minute(en) > s0 ? Minute(en) : Math.Min(FreeTimeFinder.DayMinutes, s0 + FreeTimeFinder.DefaultEventMinutes)) : null;

        foreach (var o in sameDay)
        {
            if (!o.CoversDay(date)) continue;
            bool sameTitle = key.Length > 0 && Normalize(o.Event.Title) == key;
            bool sameStart = o.StartTime == start && o.Date == date;
            if (sameTitle && sameStart) { result.Add(new EventConflict(ConflictKind.Duplicate, o.Event.Title, o.Date, o.StartTime, o.EndTime)); continue; }

            if (s is { } a && e is { } b && FreeTimeFinder.IntervalOn(o, date) is { } other && a < other.EndMinute && other.StartMinute < b)
                result.Add(new EventConflict(ConflictKind.Overlap, o.Event.Title, o.Date, o.StartTime, o.EndTime));
        }
        return result;
    }

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s.Normalize(NormalizationForm.FormC))
            if (!char.IsWhiteSpace(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static int Minute(TimeOnly t) => t.Hour * 60 + t.Minute;
}
