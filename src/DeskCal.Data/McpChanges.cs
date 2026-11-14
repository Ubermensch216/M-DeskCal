using System.Globalization;
using System.Text.Json;
using DeskCal.Core;

namespace DeskCal.Data;

/// <summary>
/// The changes an AI client may ask for, and how they are carried out once the user approves. The MCP server only
/// <see cref="Validate"/>s a request and queues it; <see cref="Apply"/> runs in the app, after approval. Both check the
/// arguments completely, because the queue is a database table and its content is never trusted just for being there.
/// </summary>
public sealed class McpChanges(DeskCalDb db, IClock clock, Func<DateOnly, bool>? isDayOff = null)
{
    public static readonly IReadOnlyList<string> WriteTools =
        ["create_event", "update_event", "delete_event", "add_item", "set_item_done", "create_countdown", "add_note", "move_item"];

    public const int MaxTitle = 500, MaxBody = 20000, MaxItem = 2000, MaxReminders = 5, MaxCountdownTitle = 200, MaxNoteTitle = 200;

    /// <summary>The D-day kinds an AI client can ask for, by the name used in the tool arguments and the Korean word shown to the user.</summary>
    public static readonly IReadOnlyList<(string Name, CountdownType Type, string Korean)> CountdownTypes =
    [
        ("custom", CountdownType.Custom, "일반"), ("birthday", CountdownType.Birthday, "생일"), ("memorial", CountdownType.Memorial, "기념일(만난 날)"),
        ("festival", CountdownType.Festival, "명절"), ("exam", CountdownType.Exam, "시험"),
    ];

    public static string CountdownTypeName(CountdownType type) => CountdownTypes.FirstOrDefault(t => t.Type == type).Name ?? "custom";

    private readonly EventRepository _events = new(db, clock);
    private readonly ItemRepository _items = new(db, clock);
    private readonly CountdownRepository _countdowns = new(db, clock);
    private readonly NoteRepository _notes = new(db, clock);
    private readonly EventSeriesService _series = new(db, clock, isDayOff);

    /// <summary>Is this a request that can be queued? <c>Summary</c> is the sentence the user reads when deciding.</summary>
    public (string? Error, string Summary) Validate(string tool, JsonElement args)
    {
        try { return (null, Plan(tool, args, apply: false).Summary); }
        catch (McpArgumentException ex) { return (ex.Message, ""); }
    }

    /// <summary>Carry the request out. Returns a sentence describing what happened.</summary>
    /// <exception cref="McpArgumentException">The request is invalid or no longer applies (the event was deleted meanwhile).</exception>
    public string Apply(string tool, string argsJson) => ApplyWithTrace(tool, argsJson).Summary;

    /// <summary>Like <see cref="Apply"/>, and also says what was created (so that it can be undone with <see cref="Revert"/>).</summary>
    public AppliedChange ApplyWithTrace(string tool, string argsJson)
    {
        JsonElement args;
        try { args = JsonDocument.Parse(argsJson).RootElement.Clone(); }
        catch (JsonException) { throw new McpArgumentException("요청 내용을 읽을 수 없습니다."); }
        return Plan(tool, args, apply: true);
    }

    /// <summary>Take back something <see cref="ApplyWithTrace"/> created (an event, to-do, D-day or note). False when it is already gone.</summary>
    public bool Revert(string kind, string id)
    {
        switch (kind)
        {
            case "event":
                if (_events.Get(id) is not { Deleted: false }) return false;
                _series.DeleteAll(id);
                return true;
            case "item": return _items.Get(id) is { Deleted: false } && _items.SoftDelete(id);
            case "countdown": return _countdowns.Delete(id);
            case "note": return _notes.Get(id) is { Deleted: false } && _notes.SoftDelete(id);
            default: return false;
        }
    }

    private AppliedChange Plan(string tool, JsonElement a, bool apply)
    {
        if (a.ValueKind != JsonValueKind.Object) throw new McpArgumentException("인자가 객체가 아닙니다.");
        switch (tool)
        {
            case "create_event":
            {
                var draft = DraftFrom(a, null);
                string summary = $"일정 추가: {Clip(draft.Title)} ({Describe(draft)})";
                var created = apply ? _series.Create(draft) : null;
                return new(summary, created is null ? null : "event", created?.Id);
            }
            case "update_event":
            {
                var e = Existing(a);
                var draft = DraftFrom(a, EventDraft.From(e, new ReminderRepository(db, clock).GetByOwner(ReminderOwner.Event, e.Id)));
                string summary = $"일정 수정: {Clip(e.Title)} → {Clip(draft.Title)} ({Describe(draft)})";
                if (apply) _series.UpdateAll(e.Id, draft);
                return new(summary);
            }
            case "delete_event":
            {
                var e = Existing(a);
                string summary = $"일정 삭제: {Clip(e.Title)} ({e.Start}{(RecurrenceRule.From(e).IsRepeating ? ", 반복 일정 전체" : "")})";
                if (apply) _series.DeleteAll(e.Id);
                return new(summary);
            }
            case "add_item":
            {
                string day = Text(a, "day", required: true, 10)!;
                if (!CountdownCalc.TryParseDate(day, out var d)) throw new McpArgumentException("day 는 yyyy-MM-dd 형식이어야 합니다.");
                string content = Text(a, "content", required: true, MaxItem)!.ReplaceLineEndings(" ").Trim();
                if (content.Length == 0) throw new McpArgumentException("content 가 비어 있습니다.");
                var added = apply ? _items.Add(CountdownCalc.Format(d), content) : null;
                return new($"할 일 추가: {CountdownCalc.Format(d)} — {Clip(content)}", added is null ? null : "item", added?.Id);
            }
            case "set_item_done":
            {
                string id = Text(a, "id", required: true, 64)!;
                if (!a.TryGetProperty("done", out var doneEl) || doneEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new McpArgumentException("done 은 true 또는 false 여야 합니다.");
                var item = _items.Get(id);
                if (item is null || item.Deleted) throw new McpArgumentException("해당 할 일을 찾을 수 없습니다.");
                bool done = doneEl.GetBoolean();
                if (apply) _items.SetDone(id, done);
                return new($"할 일 {(done ? "완료" : "완료 취소")}: {item.Day} — {Clip(item.Content)}");
            }
            case "move_item":
            {
                string id = Text(a, "id", required: true, 64)!;
                string day = Text(a, "day", required: true, 10)!;
                if (!CountdownCalc.TryParseDate(day, out var target)) throw new McpArgumentException("day 는 yyyy-MM-dd 형식이어야 합니다.");
                var item = _items.Get(id);
                if (item is null || item.Deleted) throw new McpArgumentException("해당 할 일을 찾을 수 없습니다.");
                if (item.CountdownId is not null) throw new McpArgumentException("D-day 에 묶인 할 일은 옮기면 D-day 날짜도 함께 바뀌므로 이 방법으로 옮길 수 없습니다.");
                if (item.SeriesId is not null) throw new McpArgumentException("반복 항목의 할 일은 옮길 수 없습니다.");
                string to = CountdownCalc.Format(target);
                if (item.Day == to) throw new McpArgumentException("이미 그 날짜에 있는 할 일입니다.");
                if (apply) _items.Move(id, to);
                return new($"할 일 이동: {item.Day} → {to} — {Clip(item.Content)}");
            }
            case "create_countdown":
            {
                string title = Text(a, "title", required: true, MaxCountdownTitle)!.ReplaceLineEndings(" ").Trim();
                string date = Text(a, "date", required: true, 10)!.Trim();
                bool lunar = OptionalBool(a, "lunar");
                string typeName = Text(a, "type", required: false, 20)?.Trim().ToLowerInvariant() ?? "custom";
                var kind = CountdownTypes.FirstOrDefault(t => t.Name == typeName);
                if (kind.Name is null) throw new McpArgumentException($"type 은 {string.Join(", ", CountdownTypes.Select(t => t.Name))} 중 하나여야 합니다.");
                bool showCard = OptionalBool(a, "show_card"), showBottom = OptionalBool(a, "show_bottom");
                if (CountdownCalc.Validate(title, date, lunar) is { } problem) throw new McpArgumentException(problem);
                string summary = $"디데이 추가: {Clip(title)} ({date}{(lunar ? ", 음력" : "")}, {kind.Korean})";
                var cd = apply ? _countdowns.Add(title, date, kind.Type, showCard, showBottom, lunar) : null;
                return new(summary, cd is null ? null : "countdown", cd?.Id);
            }
            case "add_note":
            {
                string title = Text(a, "title", required: false, MaxNoteTitle)?.ReplaceLineEndings(" ").Trim() ?? "";
                string content = Text(a, "content", required: false, MaxBody) ?? "";
                if (title.Length == 0 && content.Trim().Length == 0) throw new McpArgumentException("title 과 content 가 모두 비어 있습니다.");
                string shown = title.Length > 0 ? title : content.Trim().Split('\n', 2)[0].Trim();
                string summary = $"메모 추가: {Clip(shown)} ({content.Length}자)";
                var note = apply ? _notes.Add(title, content) : null;
                return new(summary, note is null ? null : "note", note?.Id);
            }
            default:
                throw new McpArgumentException($"알 수 없는 변경 종류입니다: {tool}");
        }
    }

    private static bool OptionalBool(JsonElement a, string name)
    {
        if (!a.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return false;
        return el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean() : throw new McpArgumentException($"{name} 는 true 또는 false 여야 합니다.");
    }

    private EventEntry Existing(JsonElement a)
    {
        string id = Text(a, "id", required: true, 64)!;
        var e = _events.Get(id);
        if (e is null || e.Deleted) throw new McpArgumentException("해당 일정을 찾을 수 없습니다 (이미 삭제되었을 수 있습니다).");
        return e;
    }

    /// <summary>A new event from the arguments, or an existing event with the given fields replaced.</summary>
    private static EventDraft DraftFrom(JsonElement a, EventDraft? basis)
    {
        string title = a.TryGetProperty("title", out _) ? Text(a, "title", required: true, MaxTitle)!.ReplaceLineEndings(" ").Trim() : basis?.Title ?? "";
        string body = a.TryGetProperty("body", out _) ? Text(a, "body", required: false, MaxBody) ?? "" : basis?.Body ?? "";

        DateOnly sd; TimeOnly? st;
        if (a.TryGetProperty("start", out _))
        {
            if (!EventTime.TryParse(Text(a, "start", required: true, 19), out sd, out st)) throw new McpArgumentException("start 는 yyyy-MM-dd 또는 yyyy-MM-ddTHH:mm 형식이어야 합니다.");
        }
        else if (basis is not null) { sd = basis.StartDate; st = basis.StartTime; }
        else throw new McpArgumentException("start 가 필요합니다.");

        DateOnly? ed = basis?.EndDate; TimeOnly? et = basis?.EndTime;
        if (a.TryGetProperty("end", out _))
        {
            string? end = Text(a, "end", required: false, 19);
            if (string.IsNullOrWhiteSpace(end)) { ed = null; et = null; }
            else
            {
                if (!EventTime.TryParse(end, out var d1, out var t1)) throw new McpArgumentException("end 는 yyyy-MM-dd 또는 yyyy-MM-ddTHH:mm 형식이어야 합니다.");
                if ((t1 is null) != (st is null)) throw new McpArgumentException("start 와 end 는 같은 형식(날짜만 / 날짜+시간)이어야 합니다.");
                ed = d1; et = t1;
            }
        }
        else if (a.TryGetProperty("start", out _) && basis is not null && (st is null) != (basis.StartTime is null)) { ed = null; et = null; }   // all-day <-> timed: the old end no longer fits

        var rule = basis?.Rule ?? RecurrenceRule.None;
        if (a.TryGetProperty("rrule", out _))
        {
            string? rr = Text(a, "rrule", required: false, 200);
            if (string.IsNullOrWhiteSpace(rr)) rule = RecurrenceRule.None;
            else if (!RecurrenceRule.TryParse(rr, null, out rule)) throw new McpArgumentException("rrule 을 해석할 수 없습니다. 예: FREQ=WEEKLY, FREQ=MONTHLY;INTERVAL=2");
        }

        var reminders = basis?.Reminders ?? [];
        if (a.TryGetProperty("reminder_minutes", out var rm))
        {
            if (rm.ValueKind != JsonValueKind.Array || rm.GetArrayLength() > MaxReminders) throw new McpArgumentException($"reminder_minutes 는 정수 배열(최대 {MaxReminders}개)이어야 합니다.");
            var list = new List<ReminderSpec>();
            foreach (var m in rm.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Number || !m.TryGetInt32(out int minutes) || minutes is < 0 or > 525_600) throw new McpArgumentException("알림은 0~525600 사이의 분 단위 정수여야 합니다.");
                if (list.All(x => x.OffsetMinutes != minutes)) list.Add(new ReminderSpec(minutes));
            }
            reminders = list;
        }

        var draft = new EventDraft(title, body, sd, st, ed, et, rule, basis?.Color, reminders);
        var errors = draft.Validate();
        if (errors.Count > 0) throw new McpArgumentException(string.Join(" ", errors));
        return draft;
    }

    private static string? Text(JsonElement a, string name, bool required, int max)
    {
        if (!a.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null)
            return required ? throw new McpArgumentException($"{name} 가 필요합니다.") : null;
        if (el.ValueKind != JsonValueKind.String) throw new McpArgumentException($"{name} 는 문자열이어야 합니다.");
        string s = el.GetString() ?? "";
        if (required && s.Trim().Length == 0) throw new McpArgumentException($"{name} 가 비어 있습니다.");
        if (s.Length > max) throw new McpArgumentException($"{name} 가 너무 깁니다 (최대 {max}자).");
        return s;
    }

    private static string Describe(EventDraft d)
    {
        string day = d.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string when = d.StartTime is { } t ? $"{day} {t:HH\\:mm}" : day;
        if (d.EndTime is { } et) when += $"~{(d.EndDate is { } e && e != d.StartDate ? e.ToString("MM-dd ", CultureInfo.InvariantCulture) : "")}{et:HH\\:mm}";
        else if (d.EndDate is { } e2 && e2 != d.StartDate) when += $"~{e2:yyyy-MM-dd}";
        if (d.Rule.IsRepeating) when += ", 반복";
        return when;
    }

    private static string Clip(string s)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Ellipsize(60);
    }
}

/// <summary>What a change did: the sentence for the user, and (for something newly created) its kind and id, so that it can be taken back.</summary>
public sealed record AppliedChange(string Summary, string? CreatedKind = null, string? CreatedId = null);

/// <summary>A request that cannot be carried out; the message is meant for the AI client and the user.</summary>
public sealed class McpArgumentException(string message) : Exception(message);
