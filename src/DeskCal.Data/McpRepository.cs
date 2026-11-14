using System.Security.Cryptography;
using System.Text;
using DeskCal.Core;
using Microsoft.Data.Sqlite;

namespace DeskCal.Data;

public enum PendingStatus { Waiting = 0, Approved = 1, Rejected = 2, Failed = 3, Expired = 4 }

/// <summary>A change an AI client asked for. Nothing in <see cref="Args"/> has been applied until the user approves it.</summary>
public sealed record PendingChange(string Id, long CreatedAt, string Tool, string Args, string Summary, PendingStatus Status, long? DecidedAt, string? Result);

public sealed record AuditEntry(long Id, long At, string Tool, string Args, string Outcome);

/// <summary>The approval queue and the audit log shared by the app and the MCP server process (both open the same database).</summary>
public sealed class McpRepository(DeskCalDb db, IClock clock, int maxAudit = McpRepository.MaxAudit) : RepoBase(db, clock)
{
    public const int MaxWaiting = 50;
    public const int MaxAudit = 2000;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private const string Cols = "id, created_at, tool, args, summary, status, decided_at, result";

    private static PendingChange Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), (PendingStatus)r.GetInt32(5),
        r.IsDBNull(6) ? null : r.GetInt64(6), Str(r, 7));

    /// <exception cref="InvalidOperationException">Too many requests are already waiting.</exception>
    public PendingChange AddPending(string tool, string args, string summary)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        using (var count = Cmd(c, "SELECT COUNT(*) FROM mcp_pending WHERE status=0", tx))
            if (Convert.ToInt32(count.ExecuteScalar()) >= MaxWaiting)
                throw new InvalidOperationException($"승인 대기 중인 요청이 이미 {MaxWaiting}건입니다. 사용자가 먼저 처리해야 합니다.");
        var p = new PendingChange(NewId(), Clock.NowMs, tool, args, summary, PendingStatus.Waiting, null, null);
        using var cmd = Cmd(c, $"INSERT INTO mcp_pending({Cols}) VALUES($i,$c,$t,$a,$s,0,NULL,NULL)", tx,
            ("$i", p.Id), ("$c", p.CreatedAt), ("$t", tool), ("$a", args), ("$s", summary));
        cmd.ExecuteNonQuery();
        tx.Commit();
        return p;
    }

    public PendingChange? GetPending(string id)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, $"SELECT {Cols} FROM mcp_pending WHERE id=$i", null, ("$i", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public IReadOnlyList<PendingChange> ListWaiting()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, $"SELECT {Cols} FROM mcp_pending WHERE status=0 ORDER BY created_at");
        return ReadAll(cmd, Map);
    }

    public int CountWaiting()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT COUNT(*) FROM mcp_pending WHERE status=0");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Record the user's decision. Only a waiting request can be decided, and only once.</summary>
    public bool Decide(string id, PendingStatus status, string? result)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "UPDATE mcp_pending SET status=$s, decided_at=$t, result=$r WHERE id=$i AND status=0", null,
            ("$s", (int)status), ("$t", Clock.NowMs), ("$r", result), ("$i", id));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Requests nobody answered within a day are dropped: a stale request must not be approved by habit.</summary>
    public int ExpireOld()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "UPDATE mcp_pending SET status=4, decided_at=$t, result='시간 초과' WHERE status=0 AND created_at < $cut", null,
            ("$t", Clock.NowMs), ("$cut", Clock.NowMs - (long)Lifetime.TotalMilliseconds));
        return cmd.ExecuteNonQuery();
    }

    // ---- audit ----------------------------------------------------------------------------------

    public void Audit(string tool, string args, string outcome)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = Cmd(c, "INSERT INTO mcp_audit(at, tool, args, outcome) VALUES($a,$t,$g,$o)", tx,
            ("$a", Clock.NowMs), ("$t", tool), ("$g", args.Ellipsize(2000)), ("$o", outcome.Ellipsize(1000)))) cmd.ExecuteNonQuery();
        using (var trim = Cmd(c, "DELETE FROM mcp_audit WHERE id <= (SELECT MAX(id) FROM mcp_audit) - $keep", tx, ("$keep", maxAudit))) trim.ExecuteNonQuery();
        tx.Commit();
    }

    public IReadOnlyList<AuditEntry> ListAudit(int limit = 200)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT id, at, tool, args, outcome FROM mcp_audit ORDER BY id DESC LIMIT $n", null, ("$n", limit));
        return ReadAll(cmd, r => new AuditEntry(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4)));
    }
}

/// <summary>
/// Whether the AI connection is switched on, and the access token. Only a hash of the token is stored: the token itself is
/// shown once when it is made, and whoever holds it (the AI client's configuration) can start the MCP server.
/// </summary>
public sealed class McpAccess(SettingsStore settings)
{
    private const string EnabledKey = "mcp.enabled", HashKey = "mcp.token_hash";

    public bool Enabled
    {
        get => settings.GetBool(EnabledKey, false);
        set => settings.SetBool(EnabledKey, value);
    }

    public bool HasToken => !string.IsNullOrEmpty(settings.GetString(HashKey));

    /// <summary>Make a new token (the old one stops working) and return it. This is the only time it can be read.</summary>
    public string NewToken()
    {
        string token = "dcal_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        settings.SetString(HashKey, Hash(token));
        return token;
    }

    public void RevokeToken() => settings.Remove(HashKey);

    public bool Verify(string? token)
    {
        string? stored = settings.GetString(HashKey);
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(token)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored), Encoding.ASCII.GetBytes(Hash(token)));
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
