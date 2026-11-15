using System.Security.Cryptography;
using System.Text;

namespace DeskCal.Core;

/// <summary>
/// One record of the calendar as it is kept in the shared folder: <see cref="Key"/> names it ("items/&lt;id&gt;", "timetable", ...),
/// <see cref="Text"/> is its canonical file content and <see cref="Stamp"/> is when it was last edited (Unix ms, 0 when the record does not know).
/// </summary>
public sealed record SyncDoc(string Key, string Text, long Stamp)
{
    public string Hash { get; } = GitMerge.HashOf(Text);
}

/// <summary>What one sync has to do on each side, and the state both sides agree on afterwards.</summary>
public sealed class MergePlan
{
    /// <summary>Records to write into this computer's calendar (new or changed elsewhere).</summary>
    public List<SyncDoc> ApplyLocal { get; } = [];
    /// <summary>Records to remove from this computer's calendar (removed elsewhere).</summary>
    public List<string> DeleteLocal { get; } = [];
    /// <summary>Records to write into the shared folder.</summary>
    public List<SyncDoc> WriteRemote { get; } = [];
    /// <summary>Records to remove from the shared folder.</summary>
    public List<string> DeleteRemote { get; } = [];
    /// <summary>Records edited on both sides since the last sync; the later edit was kept.</summary>
    public int Conflicts { get; set; }
    /// <summary>Key → hash of the record both sides hold after this sync (records that exist on neither side are left out).</summary>
    public Dictionary<string, string> Agreed { get; } = new(StringComparer.Ordinal);

    public bool ChangesLocal => ApplyLocal.Count > 0 || DeleteLocal.Count > 0;
    public bool ChangesRemote => WriteRemote.Count > 0 || DeleteRemote.Count > 0;
}

/// <summary>
/// Three-way merge of the calendar with the shared folder. Each record is compared with how it looked at the last successful sync
/// (the "base", kept per computer): a side that still matches the base did not change, so the other side's version is taken.
/// When both sides changed, the later edit wins (this side on a tie or when neither knows its time). A record deleted on one side
/// and edited on the other is kept - unless the deletion is known to be the later of the two (soft-deleted rows remember when).
/// Pure function: no I/O.
/// </summary>
public static class GitMerge
{
    public static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <param name="local">Live records on this computer.</param>
    /// <param name="deletedAt">Records deleted on this computer (still known as soft-deleted rows) → when they were deleted.</param>
    /// <param name="remote">Records in the shared folder.</param>
    /// <param name="baseHashes">Key → hash at the last successful sync (empty on the first sync).</param>
    public static MergePlan Plan(IReadOnlyDictionary<string, SyncDoc> local, IReadOnlyDictionary<string, long> deletedAt,
                                 IReadOnlyDictionary<string, SyncDoc> remote, IReadOnlyDictionary<string, string> baseHashes)
    {
        var plan = new MergePlan();
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        keys.UnionWith(local.Keys); keys.UnionWith(remote.Keys); keys.UnionWith(baseHashes.Keys);

        foreach (var key in keys)
        {
            local.TryGetValue(key, out var l);
            remote.TryGetValue(key, out var r);
            baseHashes.TryGetValue(key, out var b);
            SyncDoc? final;

            if (l is null && r is null) continue;                                   // gone on both sides
            if (l is not null && r is not null && l.Hash == r.Hash) final = l;      // already the same
            else if (l is null && r is not null && deletedAt.TryGetValue(key, out long gone) && r.Stamp > 0 && r.Stamp <= gone && r.Hash != b)
            {
                // deleted here after the last edit made elsewhere: the deletion is the newer change
                plan.DeleteRemote.Add(key); final = null;
            }
            else if (l?.Hash == b) final = TakeRemote(plan, key, r);                // unchanged here (or absent at both times)
            else if (r?.Hash == b) final = TakeLocal(plan, key, l);                 // unchanged there
            else if (l is not null && r is not null)                               // both changed (or both new)
            {
                if (b is not null) plan.Conflicts++;
                final = r.Stamp > l.Stamp ? TakeRemote(plan, key, r) : TakeLocal(plan, key, l);
            }
            else final = l is null ? TakeRemote(plan, key, r) : TakeLocal(plan, key, l);   // deleted on one side, edited on the other: keep it

            if (final is not null) plan.Agreed[key] = final.Hash;
        }
        return plan;
    }

    private static SyncDoc? TakeRemote(MergePlan plan, string key, SyncDoc? r)
    {
        if (r is null) plan.DeleteLocal.Add(key); else plan.ApplyLocal.Add(r);
        return r;
    }

    private static SyncDoc? TakeLocal(MergePlan plan, string key, SyncDoc? l)
    {
        if (l is null) plan.DeleteRemote.Add(key); else plan.WriteRemote.Add(l);
        return l;
    }
}
