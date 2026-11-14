using System.Runtime.InteropServices;
using DeskCal.Core;

namespace DeskCal.Data;

public static class EmbeddingEntity
{
    public const string Item = "item", Event = "event";
}

/// <summary>A stored vector with the text hash it was made from and the model that made it.</summary>
public sealed record EmbeddingRow(string Entity, string EntityId, string Model, string Hash, float[] Vector);

/// <summary>
/// The meaning-search index: one unit-length vector per to-do or event, kept with a hash of the text it came from (so only changed text
/// is embedded again) and the name of the model (vectors of different models are not comparable). The index is derived data: it can be
/// cleared and rebuilt at any time, and it holds nothing the database does not already hold.
/// </summary>
public sealed class EmbeddingRepository(DeskCalDb db, IClock clock) : RepoBase(db, clock)
{
    /// <summary>For every indexed text: which model made its vector and the hash of the text.</summary>
    public IReadOnlyDictionary<(string Entity, string Id), (string Model, string Hash)> Stamps()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT entity, entity_id, model, content_hash FROM embeddings");
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<(string, string), (string, string)>();
        while (r.Read()) map[(r.GetString(0), r.GetString(1))] = (r.GetString(2), r.GetString(3));
        return map;
    }

    public void UpsertMany(IEnumerable<EmbeddingRow> rows)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        long now = Clock.NowMs;
        foreach (var e in rows)
        {
            using var cmd = Cmd(c,
                "INSERT INTO embeddings(entity, entity_id, model, dim, vec, content_hash, updated_at) VALUES($e,$i,$m,$d,$v,$h,$u) " +
                "ON CONFLICT(entity, entity_id) DO UPDATE SET model=$m, dim=$d, vec=$v, content_hash=$h, updated_at=$u", tx,
                ("$e", e.Entity), ("$i", e.EntityId), ("$m", e.Model), ("$d", e.Vector.Length),
                ("$v", MemoryMarshal.AsBytes<float>(e.Vector).ToArray()), ("$h", e.Hash), ("$u", now));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>All vectors made by this model (rows of another model, or damaged ones, are left out).</summary>
    public IReadOnlyList<EmbeddingRow> Load(string model)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT entity, entity_id, model, content_hash, dim, vec FROM embeddings WHERE model=$m", null, ("$m", model));
        using var r = cmd.ExecuteReader();
        var list = new List<EmbeddingRow>();
        while (r.Read())
        {
            var blob = (byte[])r["vec"];
            int dim = r.GetInt32(4);
            if (dim <= 0 || blob.Length != dim * sizeof(float)) continue;
            list.Add(new EmbeddingRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), MemoryMarshal.Cast<byte, float>(blob).ToArray()));
        }
        return list;
    }

    public int Remove(IEnumerable<(string Entity, string Id)> keys)
    {
        int n = 0;
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        foreach (var (entity, id) in keys)
        {
            using var cmd = Cmd(c, "DELETE FROM embeddings WHERE entity=$e AND entity_id=$i", tx, ("$e", entity), ("$i", id));
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    public int Clear()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "DELETE FROM embeddings");
        return cmd.ExecuteNonQuery();
    }

    public int Count()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT COUNT(*) FROM embeddings");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Changes whenever the index changes (row count and latest write): lets a cache of the vectors know that it is stale.</summary>
    public (int Count, long Latest) Stamp()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT COUNT(*), COALESCE(MAX(updated_at), 0) FROM embeddings");
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetInt64(1));
    }
}
