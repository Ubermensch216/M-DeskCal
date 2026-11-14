using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskCal.Ai.Intent;
using DeskCal.Ai.Llm;
using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.Ai;

/// <summary>
/// A change the AI side suggests, in exactly the form the MCP approval queue uses (a tool name and its JSON arguments), so that
/// there is one place that checks and carries out changes: <see cref="McpChanges"/>. Nothing is written until the user accepts.
/// </summary>
/// <param name="Conflicts">Existing events this one overlaps or duplicates (sentences for the user); the proposal is still valid, the user decides.</param>
public sealed record Proposal(string Tool, string ArgsJson, string Summary, IReadOnlyList<string> Warnings, ParseSource Source, IReadOnlyList<string>? Conflicts = null)
{
    public IReadOnlyList<string> ConflictList => Conflicts ?? [];
}

public static class ProposalFactory
{
    /// <summary>The proposal for a complete parse result, or null with the reason (the arguments did not pass the same checks the MCP queue applies).</summary>
    public static Proposal? FromEvent(ParseResult result, McpChanges changes, out string? error, IReadOnlyList<string>? conflicts = null)
    {
        error = result.Problem;
        if (!result.IsComplete || result.Event.Date is not { } date) return null;
        var e = result.Event;

        var args = new JsonObject { ["title"] = e.Title, ["start"] = EventTime.Format(date, e.Start) };
        if (e.Start is not null && e.End is not null) args["end"] = EventTime.Format(e.EndDate ?? date, e.End);
        var (rrule, ext) = e.Rule.ToColumns(date);
        if ((rrule ?? ext) is { } rule) args["rrule"] = rule;
        if (e.ReminderMinutes.Count > 0) args["reminder_minutes"] = new JsonArray(e.ReminderMinutes.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());

        string json = args.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        using var doc = JsonDocument.Parse(json);
        var (problem, summary) = changes.Validate("create_event", doc.RootElement);
        if (problem is not null) { error = problem; return null; }
        error = null;
        return new Proposal("create_event", json, summary, e.Notes, result.Source, conflicts);
    }

    /// <summary>A to-do on a day (what a deadline in a text becomes), checked by the same rules as the MCP "add_item" request.</summary>
    public static Proposal? FromTodo(DateOnly day, string content, ParseSource source, McpChanges changes, out string? error, IReadOnlyList<string>? notes = null)
    {
        var args = new JsonObject { ["day"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["content"] = content };
        string json = args.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        using var doc = JsonDocument.Parse(json);
        var (problem, summary) = changes.Validate("add_item", doc.RootElement);
        error = problem;
        return problem is null ? new Proposal("add_item", json, summary, notes ?? [], source) : null;
    }
}

/// <summary>What one quick-input sentence turned into.</summary>
/// <param name="Proposal">Ready to accept, or null.</param>
/// <param name="Result">What was understood (possibly partly), for pre-filling the editor when there is no proposal.</param>
/// <param name="Message">Words for the user: what is missing, or why the AI could not help. Null when all is well.</param>
public sealed record QuickInputOutcome(Proposal? Proposal, ParseResult? Result, string? Message, bool UsedLlm);

/// <summary>
/// "Type one sentence, get an event": the rules first (instant, offline), the language model only for what they cannot read, and
/// always a checked proposal for the user to accept. The model is used only when AI is switched on, a model is chosen and the
/// calendar is not locked; calls are recorded in the audit log without the text.
/// </summary>
public sealed class QuickInputService(AiSettingsStore settings, AiGate gate, McpChanges changes, McpRepository audit, Func<AiSettings, ILlmClient>? clientFactory = null, ConflictService? conflicts = null)
{
    private IReadOnlyList<string>? Clashes(ParseResult r) => conflicts?.Check(r.Event).Select(c => c.Describe()).ToList();

    private readonly Func<AiSettings, ILlmClient> _factory = clientFactory ?? (s => LlmClientFactory.Create(s));

    /// <summary>True when a sentence the rules cannot read would be sent to the model (switched on, a model chosen, calendar unlocked).</summary>
    public bool AiAvailable => gate.Refusal() is null && settings.Load().Model.Length > 0;

    /// <summary>Only the rules: instant, no model, no log entry. For the live preview while typing.</summary>
    public QuickInputOutcome ParseRules(string text, DateOnly today, DayOfWeek weekStart)
    {
        if (!gate.Unlocked) return new(null, null, "달력이 잠겨 있습니다. 잠금을 먼저 푸세요.", false);
        var rules = KoreanEventParser.Parse(text, today, weekStart);
        return ProposalFactory.FromEvent(rules, changes, out string? why, Clashes(rules)) is { } p ? new(p, rules, null, false) : new(null, rules, why ?? rules.Problem, false);
    }

    public async Task<QuickInputOutcome> ParseAsync(string text, DateOnly today, DayOfWeek weekStart, CancellationToken ct)
    {
        if (!gate.Unlocked) return new(null, null, "달력이 잠겨 있습니다. 잠금을 먼저 푸세요.", false);

        var rules = KoreanEventParser.Parse(text, today, weekStart);
        if (rules.IsComplete && ProposalFactory.FromEvent(rules, changes, out _, Clashes(rules)) is { } done)
        {
            Record(text, rules: true, llm: false, "ok");
            return new(done, rules, null, false);
        }

        // the rules could not finish the job: ask the model, if the user has allowed it
        var best = rules;
        string? message = rules.Problem;
        bool used = false;
        var ai = settings.Load();
        if (gate.Refusal() is null && ai.Model.Length > 0 && text.Length <= KoreanEventParser.MaxChars)
        {
            used = true;
            try
            {
                var viaLlm = await new LlmEventParser(_factory(ai)).ParseAsync(text, today, weekStart, ct);
                // a date the rules did find is more trustworthy than the model's (in testing it read "내일" as the day after tomorrow)
                if (rules.Event.Date is { } ruleDate && viaLlm.Event.Date != ruleDate)
                {
                    var ev = viaLlm.Event with { Date = ruleDate, Notes = [.. viaLlm.Event.Notes, "AI 가 읽은 날짜 대신, 규칙이 문장에서 읽은 날짜를 썼습니다."] };
                    viaLlm = viaLlm with { Event = ev, Problem = viaLlm.Problem is "날짜를 알 수 없습니다. 예: 내일, 다음 주 화요일, 12월 3일" ? null : viaLlm.Problem };
                    if (ev.Title.Length == 0 && viaLlm.Problem is null) viaLlm = viaLlm with { Problem = "AI 가 일정 제목을 알려 주지 않았습니다." };
                }
                string? why = null;
                if (viaLlm.IsComplete && ProposalFactory.FromEvent(viaLlm, changes, out why, Clashes(viaLlm)) is { } p)
                {
                    Record(text, rules: false, llm: true, "ok");
                    return new(p, viaLlm, null, true);
                }
                message = viaLlm.Problem ?? why ?? message;
                if (viaLlm.Event.Title.Length > 0 || viaLlm.Event.Date is not null) best = viaLlm;
                Record(text, rules: false, llm: true, "incomplete");
            }
            catch (LlmException ex)
            {
                message = $"{rules.Problem} (AI 도 쓰지 못했습니다: {ex.Message})";
                Record(text, rules: false, llm: true, "error: " + ex.Kind);
            }
        }
        else Record(text, rules: true, llm: false, "incomplete");
        return new(null, best, message, used);
    }

    /// <summary>Load the model now (fire and forget when the quick-input window opens), so the real request does not wait for it. Never throws.</summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        try
        {
            var ai = settings.Load();
            if (gate.Refusal() is not null || ai.Model.Length == 0) return;
            await _factory(ai).WarmUpAsync(ct);
        }
        catch (Exception ex) when (ex is LlmException or OperationCanceledException) { /* the real request will report the problem */ }
    }

    // metadata only: how long the sentence was and which path ran, never the sentence
    private void Record(string text, bool rules, bool llm, string outcome)
    {
        try
        {
            string args = new JsonObject { ["chars"] = text.Length, ["rules"] = rules, ["llm"] = llm, ["model"] = llm ? settings.Load().Model : null }.ToJsonString();
            audit.Audit("ai:parse", args, outcome);
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* the log must never break a feature */ }
    }
}
