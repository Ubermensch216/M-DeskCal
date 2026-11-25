using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.Tests;

public class LineReconcilerTests
{
    private static string[] L(params string[] lines) => lines;

    private static List<LineOp> Ops(string[] o, string[] n) => LineReconciler.Reconcile(o, n).ToList();

    [Fact]
    public void Identical_lists_keep_everything()
    {
        var ops = Ops(L("a", "b", "c"), L("a", "b", "c"));
        Assert.Equal([new LineOp(LineOpKind.Keep, 0, 0), new LineOp(LineOpKind.Keep, 1, 1), new LineOp(LineOpKind.Keep, 2, 2)], ops);
    }

    [Fact]
    public void Deleting_the_first_line_keeps_the_rest_by_identity()
    {
        var ops = Ops(L("a", "b", "c"), L("b", "c"));
        Assert.Contains(new LineOp(LineOpKind.Delete, 0, -1), ops);
        Assert.Contains(new LineOp(LineOpKind.Keep, 1, 0), ops);
        Assert.Contains(new LineOp(LineOpKind.Keep, 2, 1), ops);
        Assert.Equal(3, ops.Count);
    }

    [Fact]
    public void Inserting_at_the_top_keeps_existing_lines()
    {
        var ops = Ops(L("b", "c"), L("a", "b", "c"));
        Assert.Contains(new LineOp(LineOpKind.Add, -1, 0), ops);
        Assert.Contains(new LineOp(LineOpKind.Keep, 0, 1), ops);
        Assert.Contains(new LineOp(LineOpKind.Keep, 1, 2), ops);
    }

    [Fact]
    public void Editing_a_middle_line_is_an_update_not_delete_plus_add()
    {
        var ops = Ops(L("a", "b", "c"), L("a", "B", "c"));
        Assert.Equal([new LineOp(LineOpKind.Keep, 0, 0), new LineOp(LineOpKind.Update, 1, 1), new LineOp(LineOpKind.Keep, 2, 2)], ops);
    }

    [Fact]
    public void Empty_sides()
    {
        Assert.Empty(Ops(L(), L()));
        Assert.Equal([new LineOp(LineOpKind.Add, -1, 0), new LineOp(LineOpKind.Add, -1, 1)], Ops(L(), L("x", "y")));
        Assert.Equal([new LineOp(LineOpKind.Delete, 0, -1), new LineOp(LineOpKind.Delete, 1, -1)], Ops(L("x", "y"), L()));
    }

    [Fact]
    public void Surplus_after_pairing_becomes_add_or_delete()
    {
        var grow = Ops(L("a"), L("x", "y", "z"));          // no common line: 1 update + 2 adds
        Assert.Equal(1, grow.Count(o => o.Kind == LineOpKind.Update));
        Assert.Equal(2, grow.Count(o => o.Kind == LineOpKind.Add));
        var shrink = Ops(L("x", "y", "z"), L("a"));
        Assert.Equal(1, shrink.Count(o => o.Kind == LineOpKind.Update));
        Assert.Equal(2, shrink.Count(o => o.Kind == LineOpKind.Delete));
    }

    [Fact]
    public void Random_inputs_always_form_a_valid_minimal_mapping()
    {
        var rng = new Random(12345);
        for (int iter = 0; iter < 600; iter++)
        {
            var o = Enumerable.Range(0, rng.Next(0, 9)).Select(_ => "abcd"[rng.Next(4)].ToString()).ToArray();
            var n = Enumerable.Range(0, rng.Next(0, 9)).Select(_ => "abcd"[rng.Next(4)].ToString()).ToArray();
            var ops = Ops(o, n);

            // every old index handled exactly once; every new index produced exactly once
            var oldSeen = ops.Where(x => x.Kind != LineOpKind.Add).Select(x => x.OldIndex).OrderBy(x => x).ToList();
            var newSeen = ops.Where(x => x.Kind != LineOpKind.Delete).Select(x => x.NewIndex).OrderBy(x => x).ToList();
            Assert.Equal(Enumerable.Range(0, o.Length), oldSeen);
            Assert.Equal(Enumerable.Range(0, n.Length), newSeen);

            // kept lines really are equal, in the same relative order, and as many as the LCS length
            var keeps = ops.Where(x => x.Kind == LineOpKind.Keep).ToList();
            Assert.All(keeps, k => Assert.Equal(o[k.OldIndex], n[k.NewIndex]));
            Assert.Equal(keeps.OrderBy(k => k.OldIndex).Select(k => k.NewIndex), keeps.OrderBy(k => k.NewIndex).Select(k => k.NewIndex));
            Assert.Equal(LcsLength(o, n), keeps.Count);
            // an update never targets a line that is identical (that would be a missed keep)
            Assert.All(ops.Where(x => x.Kind == LineOpKind.Update), u => Assert.True(o[u.OldIndex] != n[u.NewIndex] || LcsLength(o, n) > keeps.Count));
        }
    }

    private static int LcsLength(string[] a, string[] b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = a[i - 1] == b[j - 1] ? d[i - 1, j - 1] + 1 : Math.Max(d[i - 1, j], d[i, j - 1]);
        return d[a.Length, b.Length];
    }
}

public class ReplaceDayTests
{
    private const string Day = "2026-11-08";

    [Fact]
    public void Creates_items_in_order_dropping_blank_lines_and_trailing_spaces()
    {
        using var f = new DbFixture();
        var items = f.Items.ReplaceDay(Day, ["첫째   ", "", "   ", "  들여쓰기 유지", "셋째 ✅"]);
        Assert.Equal(["첫째", "  들여쓰기 유지", "셋째 ✅"], items.Select(i => i.Content));
        Assert.Equal([0, 1, 2], items.Select(i => i.Sort));
    }

    [Fact]
    public void Removing_the_first_line_preserves_identity_and_flags_of_the_others()
    {
        using var f = new DbFixture();
        var before = f.Items.ReplaceDay(Day, ["a", "b", "c"]);
        f.Items.SetDone(before[1].Id, true);
        f.Items.SetColor(before[1].Id, "#112233");
        f.Items.SetTextColor(before[1].Id, "#FF0000");

        var after = f.Items.ReplaceDay(Day, ["b", "c"]);
        Assert.Equal([before[1].Id, before[2].Id], after.Select(i => i.Id));
        Assert.True(after[0].Done);
        Assert.Equal("#112233", after[0].BgColor);
        Assert.Equal("#FF0000", after[0].TextColor);
        Assert.Equal([0, 1], after.Select(i => i.Sort));
        Assert.True(f.Items.Get(before[0].Id)!.Deleted);
    }

    [Fact]
    public void Inserting_a_line_at_the_top_keeps_existing_ids()
    {
        using var f = new DbFixture();
        var before = f.Items.ReplaceDay(Day, ["b", "c"]);
        var after = f.Items.ReplaceDay(Day, ["a", "b", "c"]);
        Assert.Equal(["a", "b", "c"], after.Select(i => i.Content));
        Assert.Equal([before[0].Id, before[1].Id], after.Skip(1).Select(i => i.Id));
        Assert.Equal([0, 1, 2], after.Select(i => i.Sort));
    }

    [Fact]
    public void Editing_a_line_keeps_the_item_and_records_history()
    {
        using var f = new DbFixture();
        var before = f.Items.ReplaceDay(Day, ["a", "old", "c"]);
        f.Clock.Advance();
        var after = f.Items.ReplaceDay(Day, ["a", "new", "c"]);
        Assert.Equal(before[1].Id, after[1].Id);
        Assert.Equal("new", after[1].Content);
        Assert.Equal(["old"], f.History.List(EntityNames.Item, before[1].Id).Select(h => h.Content));
        Assert.Empty(f.History.List(EntityNames.Item, before[0].Id));       // untouched lines get no history
    }

    [Fact]
    public void Saving_identical_text_changes_nothing()
    {
        using var f = new DbFixture();
        var before = f.Items.ReplaceDay(Day, ["a", "b"]);
        f.Clock.Advance(60_000);
        var after = f.Items.ReplaceDay(Day, ["a", "b"]);
        Assert.Equal(before.Select(i => (i.Id, i.UpdatedAt)), after.Select(i => (i.Id, i.UpdatedAt)));
    }

    [Fact]
    public void Clearing_the_text_deletes_everything_but_other_days_are_untouched()
    {
        using var f = new DbFixture();
        var other = f.Items.Add("2026-11-09", "other day");
        var items = f.Items.ReplaceDay(Day, ["a", "b"]);
        Assert.Empty(f.Items.ReplaceDay(Day, []));
        Assert.All(items, i => Assert.True(f.Items.Get(i.Id)!.Deleted));
        Assert.Equal([other.Id], f.Items.GetByDay("2026-11-09").Select(i => i.Id));
    }

    [Fact]
    public void Duplicate_lines_are_handled()
    {
        using var f = new DbFixture();
        f.Items.ReplaceDay(Day, ["x", "x", "x"]);
        var after = f.Items.ReplaceDay(Day, ["x", "x"]);
        Assert.Equal(["x", "x"], after.Select(i => i.Content));
        Assert.Equal(2, f.Items.GetByDay(Day).Count);
    }

    [Fact]
    public void Restoring_from_history_after_a_line_edit_works()
    {
        using var f = new DbFixture();
        var item = f.Items.ReplaceDay(Day, ["v1"])[0];
        f.Items.ReplaceDay(Day, ["v2"]);
        var h = f.History.List(EntityNames.Item, item.Id).Single();
        Assert.Equal("v1", f.Items.RestoreFromHistory(h.Id)!.Content);
        Assert.Equal("v1", f.Items.GetByDay(Day).Single().Content);
    }

    [Fact]
    public void Text_color_roundtrips_and_can_be_cleared()
    {
        using var f = new DbFixture();
        var item = f.Items.Add(Day, "x");
        Assert.Null(f.Items.Get(item.Id)!.TextColor);
        Assert.True(f.Items.SetTextColor(item.Id, "#74C0FC"));
        Assert.Equal("#74C0FC", f.Items.Get(item.Id)!.TextColor);
        Assert.True(f.Items.SetTextColor(item.Id, null));
        Assert.Null(f.Items.Get(item.Id)!.TextColor);
    }
}

public class MigrationUpgradeTests
{
    [Fact]
    public void A_v1_database_upgrades_to_v2_and_keeps_its_items()
    {
        using var f = new DbFixture(migrate: false);
        using (var c = f.Db.Open())
        {
            using var create = c.CreateCommand();
            create.CommandText = Migrations.Steps[0] + "; PRAGMA user_version = 1;";
            create.ExecuteNonQuery();
            using var insert = c.CreateCommand();
            insert.CommandText = "INSERT INTO items(id, day, content, sort, done, created_at, updated_at, deleted) VALUES('old','2026-11-08','legacy',0,1,1,1,0)";
            insert.ExecuteNonQuery();
        }
        Assert.Equal(1, f.Db.SchemaVersion());

        f.Db.Migrate();

        Assert.Equal(9, f.Db.SchemaVersion());
        var item = f.Items.Get("old")!;
        Assert.Equal("legacy", item.Content);
        Assert.True(item.Done);
        Assert.Null(item.TextColor);
        Assert.True(f.Items.SetTextColor("old", "#FFFFFF"));
    }
}
