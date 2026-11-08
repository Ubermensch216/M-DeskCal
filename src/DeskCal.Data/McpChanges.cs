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
    public static readonly IReadOnlyList<string> WriteTools = ["create_event", "update_event", "delete_event", "add_item", "set_item_done"];

    public const int MaxTitle = 500, MaxBody = 20000, MaxItem = 2000, MaxReminders = 5;

    private readonly EventRepository _events = new(db, clock);
    private readonly ItemRepository _items = new(db, clock);
    private readonly EventSeriesService _series = new(db, clock, isDayOff);

    /// <summary>Is this a request that can be queued? <c>Summary</c> is the sentence the user reads when deciding.</summary>
    public (string? Error, string Summary) Validate(string tool, JsonElement args)
    {
        try { return (null, Plan(tool, args, apply: false)); }
        catch (McpArgumentException ex) { return (ex.Message, ""); }
    }

    /// <summary>Carry the request out. Returns a sentence describing what happened.</summary>
    /// <exception cref="McpArgumentException">The request is invalid or no longer applies (the event was deleted meanwhile).</exception>
    public string Apply(string tool, string argsJson)
    {
        JsonElement args;
        try { args = JsonDocument.Parse(argsJson).RootElement.Clone(); }
        catch (JsonException) { throw new McpArgumentException("요청 내용을 읽을 수 없습니다."); }
        return Plan(tool, args, apply: true);
    }

    private string Plan(string tool, JsonElement a, bool apply)
    {
        if (a.ValueKind != JsonValueKind.Object) throw new McpArgumentException("인자가 객체가 아닙니다.");
        switch (tool)
        {
            case "create_event":
            {
                var draft = DraftFrom(a, null);
                string summary = $"일정 추가: {Clip(draft.Title)} ({Describe(draft)})";
                if (apply) _series.Create(draft);
                return summary;
            }
            case "update_event":
            {
                var e = Existing(a);
                var draft = DraftFrom(a, EventDraft.From(e, new ReminderRepository(db, clock).GetByOwner(ReminderOwner.Event, e.Id)));
                string summary = $"일정 수정: {Clip(e.Title)} → {Clip(draft.Title)} ({Describe(draft)})";
                if (apply) _series.UpdateAll(e.Id, draft);
                return summary;
            }
            case "delete_event":
            {
                var e = Existing(a);
                string summary = $"일정 삭제: {Clip(e.Title)} ({e.Start}{(RecurrenceRule.From(e).IsRepeating ? ", 반복 일정 전체" : "")})";
                if (apply) _series.DeleteAll(e.Id);
                return summary;
            }
            case "add_item":
            {
                string day = Text(a, "day", required: true, 10)!;
                if (!CountdownCalc.TryParseDate(day, out var d)) throw new McpArgumentException("day 는 yyyy-MM-dd 형식이어야 합니다.");
                string content = Text(a, "content", required: true, MaxItem)!.ReplaceLineEndings(" ").Trim();
                if (content.Length == 0) throw new McpArgumentException("content 가 비어 있습니다.");
                if (apply) _items.Add(CountdownCalc.Format(d), content);
                return $"할 일 추가: {CountdownCalc.Format(d)} — {Clip(content)}";
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
                return $"할 일 {(done ? "완료" : "완료 취소")}: {item.Day} — {Clip(item.Content)}";
            }
            default:
                throw new McpArgumentException($"알 수 없는 변경 종류입니다: {tool}");
        }
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
        return s.Length <= 60 ? s : s[..60] + "…";
    }
}

/// <summary>A request that cannot be carried out; the message is meant for the AI client and the user.</summary>
public sealed class McpArgumentException(string message) : Exception(message);
