using DeskCal.Core;
using Microsoft.Data.Sqlite;

namespace DeskCal.Data;

/// <summary>Protects a password for storage (Windows DPAPI in the app; a stand-in in tests). The plain text is never written to the database.</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    /// <summary>The plain text, or null when the stored value cannot be read (another Windows user, corrupted).</summary>
    string? Unprotect(string stored);
}

/// <param name="Secret">The password, already protected by an <see cref="ISecretProtector"/>.</param>
/// <param name="TwoWay">Changes made here are sent to the server (otherwise the account is read-only: server → here).</param>
public sealed record SyncAccount(
    string Id, string Kind, string Name, string Url, string Username, string Secret,
    bool TwoWay, bool Enabled, long? LastSyncAt, string? LastResult);

/// <summary>The record of one event's last successful sync: which server resource, its version, and this event's <c>updated_at</c> then.</summary>
public sealed record SyncLink(string AccountId, string EventId, string Href, string Uid, string? Etag, long LocalStamp, bool Protected);

public sealed class SyncRepository(DeskCalDb db, IClock clock) : RepoBase(db, clock)
{
    public const string KindCalDav = "caldav";

    private const string AccountCols = "id, kind, name, url, username, secret, two_way, enabled, last_sync_at, last_result";

    private static SyncAccount MapAccount(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetInt32(6) != 0, r.GetInt32(7) != 0, r.IsDBNull(8) ? null : r.GetInt64(8), Str(r, 9));

    public SyncAccount AddAccount(string kind, string name, string url, string username, string protectedSecret, bool twoWay)
    {
        var a = new SyncAccount(NewId(), kind, name.Trim(), url.Trim(), username.Trim(), protectedSecret, twoWay, true, null, null);
        using var c = Db.Open();
        using var cmd = Cmd(c, $"INSERT INTO sync_accounts({AccountCols}) VALUES($i,$k,$n,$u,$un,$s,$tw,1,NULL,NULL)", null,
            ("$i", a.Id), ("$k", kind), ("$n", a.Name), ("$u", a.Url), ("$un", a.Username), ("$s", protectedSecret), ("$tw", twoWay ? 1 : 0));
        cmd.ExecuteNonQuery();
        return a;
    }

    /// <summary>Change the connection settings. A null secret keeps the stored password.</summary>
    public bool UpdateAccount(string id, string name, string url, string username, string? protectedSecret, bool twoWay, bool enabled)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c,
            "UPDATE sync_accounts SET name=$n, url=$u, username=$un, secret=COALESCE($s, secret), two_way=$tw, enabled=$en WHERE id=$i", null,
            ("$n", name.Trim()), ("$u", url.Trim()), ("$un", username.Trim()), ("$s", protectedSecret), ("$tw", twoWay ? 1 : 0), ("$en", enabled ? 1 : 0), ("$i", id));
        return cmd.ExecuteNonQuery() > 0;
    }

    public SyncAccount? GetAccount(string id)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, $"SELECT {AccountCols} FROM sync_accounts WHERE id=$i", null, ("$i", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapAccount(r) : null;
    }

    public IReadOnlyList<SyncAccount> ListAccounts()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, $"SELECT {AccountCols} FROM sync_accounts ORDER BY name, id");
        return ReadAll(cmd, MapAccount);
    }

    /// <summary>Remove the account and forget its links. The events themselves stay.</summary>
    public bool DeleteAccount(string id)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        using (var l = Cmd(c, "DELETE FROM sync_links WHERE account_id=$i", tx, ("$i", id))) l.ExecuteNonQuery();
        using var a = Cmd(c, "DELETE FROM sync_accounts WHERE id=$i", tx, ("$i", id));
        bool ok = a.ExecuteNonQuery() > 0;
        tx.Commit();
        return ok;
    }

    public void RecordResult(string id, string result)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "UPDATE sync_accounts SET last_sync_at=$t, last_result=$r WHERE id=$i", null,
            ("$t", Clock.NowMs), ("$r", result.Length > 2000 ? result[..2000] : result), ("$i", id));
        cmd.ExecuteNonQuery();
    }

    // ---- links ----------------------------------------------------------------------------------

    private static SyncLink MapLink(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), r.GetInt64(5), r.GetInt32(6) != 0);

    public IReadOnlyList<SyncLink> GetLinks(string accountId)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT account_id, event_id, href, uid, etag, local_stamp, protected FROM sync_links WHERE account_id=$a", null, ("$a", accountId));
        return ReadAll(cmd, MapLink);
    }

    public void UpsertLink(SyncLink l)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        // a server resource belongs to one event: if another event held this address, it lets go of it
        using (var free = Cmd(c, "DELETE FROM sync_links WHERE account_id=$a AND href=$h AND event_id<>$e", tx, ("$a", l.AccountId), ("$h", l.Href), ("$e", l.EventId))) free.ExecuteNonQuery();
        using var cmd = Cmd(c,
            "INSERT INTO sync_links(account_id, event_id, href, uid, etag, local_stamp, protected) VALUES($a,$e,$h,$u,$t,$s,$p) " +
            "ON CONFLICT(account_id, event_id) DO UPDATE SET href=$h, uid=$u, etag=$t, local_stamp=$s, protected=$p", tx,
            ("$a", l.AccountId), ("$e", l.EventId), ("$h", l.Href), ("$u", l.Uid), ("$t", l.Etag), ("$s", l.LocalStamp), ("$p", l.Protected ? 1 : 0));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public bool DeleteLink(string accountId, string eventId)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "DELETE FROM sync_links WHERE account_id=$a AND event_id=$e", null, ("$a", accountId), ("$e", eventId));
        return cmd.ExecuteNonQuery() > 0;
    }
}
