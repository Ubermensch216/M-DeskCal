using System.Text.Json;
using System.Text.Json.Nodes;
using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.Ai;

public sealed record BatchEntry(string Kind, string Id, string Summary);
public sealed record BatchFailure(string Summary, string Error);

/// <param name="BackupPath">The database copy made before a larger batch (null when none was needed).</param>
public sealed record BatchResult(IReadOnlyList<BatchEntry> Applied, IReadOnlyList<BatchFailure> Failed, string? BackupPath);

public sealed record LastBatch(long At, IReadOnlyList<BatchEntry> Entries);

public sealed record UndoResult(int Reverted, int AlreadyGone);

/// <summary>
/// Adds the proposals the user accepted, as one batch: a copy of the database first when there are several, every addition carried
/// out by the same checked code the MCP approvals use, and a note of what was created so that the whole batch can be taken back with
/// one click ("마지막 AI 추가 되돌리기"). Only the latest batch is remembered.
/// </summary>
public sealed class AiBatchService(DeskCalDb db, McpChanges changes, SettingsStore settings, IClock clock, string backupDir)
{
    /// <summary>A batch of this many additions or more is backed up before it is applied.</summary>
    public const int BackupFromCount = 2;
    public const int KeepBackups = 10;
    private const string Key = "ai.last_batch";

    public BatchResult Apply(IReadOnlyList<Proposal> proposals)
    {
        string? backup = null;
        if (proposals.Count >= BackupFromCount)
        {
            try { backup = Backup(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                // no safety copy, no batch: nothing is changed
                return new BatchResult([], proposals.Select(p => new BatchFailure(p.Summary, "백업을 만들지 못해 추가하지 않았습니다: " + ex.Message)).ToList(), null);
            }
        }

        var applied = new List<BatchEntry>();
        var failed = new List<BatchFailure>();
        foreach (var p in proposals)
        {
            try
            {
                var r = changes.ApplyWithTrace(p.Tool, p.ArgsJson);
                if (r.CreatedKind is not null && r.CreatedId is not null) applied.Add(new BatchEntry(r.CreatedKind, r.CreatedId, r.Summary));
            }
            catch (McpArgumentException ex) { failed.Add(new BatchFailure(p.Summary, ex.Message)); }
        }
        if (applied.Count > 0) Remember(applied);
        return new BatchResult(applied, failed, backup);
    }

    /// <summary>The batch that can still be taken back, or null.</summary>
    public LastBatch? Last()
    {
        if (settings.GetString(Key) is not { Length: > 0 } json) return null;
        try
        {
            var o = JsonNode.Parse(json)?.AsObject();
            var entries = o?["entries"]?.AsArray().Select(e => new BatchEntry(e!["kind"]!.GetValue<string>(), e["id"]!.GetValue<string>(), e["summary"]?.GetValue<string>() ?? "")).ToList();
            return entries is { Count: > 0 } ? new LastBatch(o!["at"]!.GetValue<long>(), entries) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException or FormatException) { return null; }
    }

    /// <summary>Take back everything the last batch created that still exists, and forget the batch.</summary>
    public UndoResult UndoLast()
    {
        var last = Last();
        if (last is null) return new UndoResult(0, 0);
        int reverted = 0, gone = 0;
        foreach (var e in last.Entries.AsEnumerable().Reverse())
            if (changes.Revert(e.Kind, e.Id)) reverted++; else gone++;
        settings.Remove(Key);
        return new UndoResult(reverted, gone);
    }

    private void Remember(List<BatchEntry> entries)
    {
        var o = new JsonObject
        {
            ["at"] = clock.NowMs,
            ["entries"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject { ["kind"] = e.Kind, ["id"] = e.Id, ["summary"] = e.Summary }).ToArray()),
        };
        settings.SetString(Key, o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private string Backup()
    {
        Directory.CreateDirectory(backupDir);
        string path = Path.Combine(backupDir, $"before-ai-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");
        db.Backup(path);
        foreach (var old in new DirectoryInfo(backupDir).GetFiles("before-ai-*.db").OrderByDescending(f => f.Name).Skip(KeepBackups))
        {
            try { old.Delete(); } catch (IOException) { /* an old copy that cannot be deleted is harmless */ }
        }
        return path;
    }
}
