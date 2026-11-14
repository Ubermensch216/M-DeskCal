using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.Ai;

public sealed record RolloverEntry(string ItemId, string From, string To);
public sealed record LastRollover(long At, IReadOnlyList<RolloverEntry> Moves);
public sealed record RolloverResult(IReadOnlyList<RolloverEntry> Moved, int Skipped);
public sealed record RolloverUndo(int Reverted, int Changed);

/// <summary>
/// Moves unfinished to-dos to a later day, once the user has looked at the list. The previous day of every moved item is remembered
/// ("ai.last_rollover"), so the whole move can be taken back with one click. Nothing is deleted or changed except the day.
/// </summary>
public sealed class RolloverService(
    ItemRepository items, ActivityRepository activity, SettingsStore settings, IClock clock,
    Func<DateOnly, bool> isDayOff, Func<DateOnly, int> busyMinutes)
{
    private const string Key = "ai.last_rollover";

    /// <summary>Unfinished items of past days (see <see cref="RolloverPlanner.FindOverdue"/>), with how often each was moved before.</summary>
    public IReadOnlyList<OverdueItem> Find(DateOnly today)
    {
        var first = today.AddDays(-RolloverPlanner.LookbackDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var last = today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var candidates = items.GetRange(first, last);
        if (candidates.Count == 0) return [];
        long from = clock.NowMs - 90L * 24 * 3600 * 1000;                      // how often it was moved lately
        var moved = activity.List(from, clock.NowMs + 1, ActivityKind.Moved, 5000).GroupBy(e => e.ItemId).ToDictionary(g => g.Key, g => g.Count());
        return RolloverPlanner.FindOverdue(candidates, today, moved);
    }

    /// <summary>The proposed spread over the next working days (the user can still change every row).</summary>
    public IReadOnlyList<RolloverMove> Spread(IReadOnlyList<OverdueItem> overdue, DateOnly today, int days = 5) =>
        RolloverPlanner.Distribute(overdue, RolloverPlanner.FirstDay(today, isDayOff), isDayOff,
            d => items.GetByDay(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Count(i => !i.Done), busyMinutes, days);

    /// <summary>Moves what the user chose. An item that is gone, done or no longer on its old day (changed meanwhile) is skipped.</summary>
    public RolloverResult Apply(IReadOnlyList<RolloverMove> moves)
    {
        var done = new List<RolloverEntry>();
        int skipped = 0;
        foreach (var m in moves)
        {
            string to = m.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var item = items.Get(m.Item.Id);
            if (item is null || item.Deleted || item.Done || item.Day != m.Item.Day || item.Day == to || item.CountdownId is not null) { skipped++; continue; }
            if (items.Move(item.Id, to)) done.Add(new RolloverEntry(item.Id, item.Day, to)); else skipped++;
        }
        if (done.Count > 0) Remember(done);
        return new RolloverResult(done, skipped);
    }

    public LastRollover? Last()
    {
        if (settings.GetString(Key) is not { Length: > 0 } json) return null;
        try
        {
            var o = JsonNode.Parse(json)?.AsObject();
            var moves = o?["moves"]?.AsArray().Select(e => new RolloverEntry(e!["id"]!.GetValue<string>(), e["from"]!.GetValue<string>(), e["to"]!.GetValue<string>())).ToList();
            return moves is { Count: > 0 } ? new LastRollover(o!["at"]!.GetValue<long>(), moves) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException or FormatException) { return null; }
    }

    /// <summary>Puts back every item of the last move that is still on the day it was moved to; the others were changed since and stay.</summary>
    public RolloverUndo UndoLast()
    {
        var last = Last();
        if (last is null) return new RolloverUndo(0, 0);
        int reverted = 0, changed = 0;
        foreach (var e in last.Moves.AsEnumerable().Reverse())
        {
            var item = items.Get(e.ItemId);
            if (item is not null && !item.Deleted && item.Day == e.To && items.Move(e.ItemId, e.From)) reverted++; else changed++;
        }
        settings.Remove(Key);
        return new RolloverUndo(reverted, changed);
    }

    private void Remember(List<RolloverEntry> moves)
    {
        var o = new JsonObject
        {
            ["at"] = clock.NowMs,
            ["moves"] = new JsonArray(moves.Select(m => (JsonNode)new JsonObject { ["id"] = m.ItemId, ["from"] = m.From, ["to"] = m.To }).ToArray()),
        };
        settings.SetString(Key, o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
