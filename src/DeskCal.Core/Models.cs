namespace DeskCal.Core;

/// <summary>Clock abstraction so timestamps are deterministic in tests. Unix milliseconds (UTC).</summary>
public interface IClock { long NowMs { get; } }

public sealed class SystemClock : IClock
{
    public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// Text/to-do entry that lives in a calendar day cell. <see cref="Day"/> is "yyyy-MM-dd".
/// <see cref="SeriesId"/> is set on items that were created by a repeat rule (every occurrence is an ordinary item of its own);
/// <see cref="CountdownId"/> links the item to the D-day it counts down to.
/// </summary>
public sealed record Item(
    string Id, string Day, string Content, string? BgColor, int Sort, bool Done,
    long CreatedAt, long UpdatedAt, bool Deleted = false, string? TextColor = null,
    string? SeriesId = null, string? CountdownId = null);

/// <summary>Event with optional recurrence. Start/End are ISO-8601 strings (UTC unless <see cref="AllDay"/>).</summary>
public sealed record EventEntry(
    string Id, string Title, string Body, string Start, string? End, bool AllDay, string? Tz,
    string? RRule, string? ExtRule, int Status, string? Color,
    long CreatedAt, long UpdatedAt, bool Deleted = false);

public enum ReminderOwner { Item = 0, Event = 1 }

public sealed record Reminder(
    string Id, ReminderOwner OwnerType, string OwnerId, int OffsetMinutes,
    string? CustomTime, string? Sound, long? FiredAt);

public enum CountdownType { Custom = 0, Birthday = 1, Memorial = 2, Festival = 3, Exam = 4 }

public sealed record Countdown(
    string Id, string Title, string Date, CountdownType Type,
    bool ShowCard, bool ShowBottom, bool Lunar);

public sealed record Note(
    string Id, string Title, string Content, string? Color, int Status,
    long CreatedAt, long UpdatedAt, bool Deleted = false);

/// <summary>Previous content of an entity, recorded whenever its content changes.</summary>
public sealed record HistoryEntry(long Id, string Entity, string EntityId, string Content, long At);

public static class EntityNames
{
    public const string Item = "item";
    public const string Event = "event";
    public const string Note = "note";
}
