using DeskCal.Core;
using DeskCal.Data;
using Microsoft.Data.Sqlite;

namespace DeskCal.Tests;

internal sealed class TestClock : IClock
{
    public long NowMs { get; set; } = 1_700_000_000_000;
    public void Advance(long ms = 1000) => NowMs += ms;
}

/// <summary>A fresh, migrated database in a temp folder, deleted on dispose.</summary>
public sealed class DbFixture : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskcal-tests-" + Guid.NewGuid().ToString("N"));
    internal TestClock Clock { get; } = new();
    public DeskCalDb Db { get; }
    public string FilePath => Db.Path;
    internal ItemRepository Items { get; }
    internal EventRepository Events { get; }
    internal ReminderRepository Reminders { get; }
    internal CountdownRepository Countdowns { get; }
    internal NoteRepository Notes { get; }
    internal HistoryRepository History { get; }
    public SettingsStore Settings { get; }

    public DbFixture(bool migrate = true)
    {
        Directory.CreateDirectory(_dir);
        Db = new DeskCalDb(Path.Combine(_dir, "test.db"));
        if (migrate) Db.Migrate();
        Items = new(Db, Clock); Events = new(Db, Clock); Reminders = new(Db, Clock);
        Countdowns = new(Db, Clock); Notes = new(Db, Clock); History = new(Db, Clock);
        Settings = new(Db);
    }

    public string TempFile(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}

public class MigrationTests
{
    [Fact]
    public void Migrate_creates_schema_and_sets_version()
    {
        using var f = new DbFixture();
        Assert.Equal(5, f.Db.SchemaVersion());
        using var c = f.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var r = cmd.ExecuteReader();
        var tables = new List<string>();
        while (r.Read()) tables.Add(r.GetString(0));
        Assert.Equal(["countdowns", "event_exceptions", "events", "history", "items", "mcp_audit", "mcp_pending", "notes", "reminders", "settings", "sync_accounts", "sync_links", "timetable_cells"], tables);
    }

    [Fact]
    public void Migrate_is_idempotent_and_keeps_data()
    {
        using var f = new DbFixture();
        var item = f.Items.Add("2026-11-08", "keep me");
        f.Db.Migrate();
        f.Db.Migrate();
        Assert.Equal("keep me", f.Items.Get(item.Id)!.Content);
    }

    [Fact]
    public void Migrate_refuses_database_from_a_newer_program()
    {
        using var f = new DbFixture();
        using (var c = f.Db.Open())
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "PRAGMA user_version = 99"; cmd.ExecuteNonQuery(); }
        Assert.Throws<InvalidOperationException>(() => f.Db.Migrate());
    }

    [Fact]
    public void Foreign_keys_and_wal_are_enabled_on_open()
    {
        using var f = new DbFixture();
        using var c = f.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", ((string)cmd.ExecuteScalar()!).ToLowerInvariant());
    }
}

public class ItemTests
{
    [Fact]
    public void Add_assigns_increasing_sort_per_day_and_roundtrips_unicode()
    {
        using var f = new DbFixture();
        var a = f.Items.Add("2026-11-08", "회의 준비 ✅ 'quote' \"dq\"; DROP TABLE items;--");
        var b = f.Items.Add("2026-11-08", "두 번째");
        var other = f.Items.Add("2026-11-09", "다른 날");
        Assert.Equal(0, a.Sort); Assert.Equal(1, b.Sort); Assert.Equal(0, other.Sort);
        var day = f.Items.GetByDay("2026-11-08");
        Assert.Equal([a.Id, b.Id], day.Select(i => i.Id));
        Assert.Equal("회의 준비 ✅ 'quote' \"dq\"; DROP TABLE items;--", day[0].Content);
    }

    [Fact]
    public void GetRange_is_inclusive_and_excludes_deleted()
    {
        using var f = new DbFixture();
        f.Items.Add("2026-10-31", "before");
        var first = f.Items.Add("2026-11-01", "first");
        var last = f.Items.Add("2026-11-30", "last");
        f.Items.Add("2026-12-01", "after");
        var gone = f.Items.Add("2026-11-15", "gone");
        f.Items.SoftDelete(gone.Id);
        Assert.Equal([first.Id, last.Id], f.Items.GetRange("2026-11-01", "2026-11-30").Select(i => i.Id));
    }

    [Fact]
    public void UpdateContent_records_previous_content_only_when_changed()
    {
        using var f = new DbFixture();
        var item = f.Items.Add("2026-11-08", "v1");
        f.Clock.Advance();
        f.Items.UpdateContent(item.Id, "v1");               // unchanged -> no history
        Assert.Empty(f.History.List(EntityNames.Item, item.Id));
        f.Items.UpdateContent(item.Id, "v2");
        f.Clock.Advance();
        f.Items.UpdateContent(item.Id, "v3");
        var hist = f.History.List(EntityNames.Item, item.Id);
        Assert.Equal(["v2", "v1"], hist.Select(h => h.Content));  // newest first
        Assert.Equal("v3", f.Items.Get(item.Id)!.Content);
    }

    [Fact]
    public void UpdateContent_on_missing_item_returns_null()
    {
        using var f = new DbFixture();
        Assert.Null(f.Items.UpdateContent("nope", "x"));
    }

    [Fact]
    public void RestoreFromHistory_restores_and_keeps_current_as_history()
    {
        using var f = new DbFixture();
        var item = f.Items.Add("2026-11-08", "original");
        f.Items.UpdateContent(item.Id, "edited");
        var h = f.History.List(EntityNames.Item, item.Id).Single();
        var restored = f.Items.RestoreFromHistory(h.Id)!;
        Assert.Equal("original", restored.Content);
        Assert.Equal(["edited", "original"], f.History.List(EntityNames.Item, item.Id).Select(x => x.Content));
    }

    [Fact]
    public void RestoreFromHistory_rejects_other_entity_history()
    {
        using var f = new DbFixture();
        var note = f.Notes.Add("t", "a");
        f.Notes.Update(note.Id, "t", "b");
        var h = f.History.List(EntityNames.Note, note.Id).Single();
        Assert.Null(f.Items.RestoreFromHistory(h.Id));
        Assert.Null(f.Items.RestoreFromHistory(123456));
    }

    [Fact]
    public void Done_color_move_and_delete_undelete()
    {
        using var f = new DbFixture();
        var item = f.Items.Add("2026-11-08", "task");
        Assert.True(f.Items.SetDone(item.Id, true));
        Assert.True(f.Items.SetColor(item.Id, "#FF0000"));
        Assert.True(f.Items.Move(item.Id, "2026-11-20"));
        var got = f.Items.Get(item.Id)!;
        Assert.True(got.Done); Assert.Equal("#FF0000", got.BgColor); Assert.Equal("2026-11-20", got.Day);
        Assert.True(f.Items.SoftDelete(item.Id));
        Assert.Empty(f.Items.GetByDay("2026-11-20"));
        Assert.False(f.Items.SetDone(item.Id, false));     // deleted items are not editable
        Assert.True(f.Items.Undelete(item.Id));
        Assert.Single(f.Items.GetByDay("2026-11-20"));
        Assert.False(f.Items.SetDone("missing", true));
    }

    [Fact]
    public void Timestamps_come_from_the_clock()
    {
        using var f = new DbFixture();
        var item = f.Items.Add("2026-11-08", "a");
        Assert.Equal(f.Clock.NowMs, item.CreatedAt);
        f.Clock.Advance(5000);
        var updated = f.Items.UpdateContent(item.Id, "b")!;
        Assert.Equal(item.CreatedAt + 5000, updated.UpdatedAt);
        Assert.Equal(item.CreatedAt, f.Items.Get(item.Id)!.CreatedAt);
    }
}

public class EventTests
{
    [Fact]
    public void Add_get_and_range_query()
    {
        using var f = new DbFixture();
        var e1 = f.Events.Add("스탠드업", "2026-11-08T00:00:00Z", "2026-11-08T00:30:00Z", tz: "Asia/Seoul", rrule: "FREQ=DAILY");
        f.Events.Add("later", "2026-12-01T00:00:00Z");
        var got = f.Events.Get(e1.Id)!;
        Assert.Equal("스탠드업", got.Title); Assert.Equal("FREQ=DAILY", got.RRule); Assert.Equal("Asia/Seoul", got.Tz);
        Assert.Equal([e1.Id], f.Events.GetStartingBetween("2026-11-01T00:00:00Z", "2026-12-01T00:00:00Z").Select(x => x.Id));
    }

    [Fact]
    public void Update_saves_previous_body_to_history_and_changes_fields()
    {
        using var f = new DbFixture();
        var e = f.Events.Add("t", "2026-11-08T00:00:00Z", body: "old");
        Assert.True(f.Events.Update(e with { Title = "new", Body = "new body", AllDay = true, ExtRule = "weekday" }));
        var got = f.Events.Get(e.Id)!;
        Assert.Equal("new", got.Title); Assert.True(got.AllDay); Assert.Equal("weekday", got.ExtRule);
        Assert.Equal(["old"], f.History.List(EntityNames.Event, e.Id).Select(h => h.Content));
        Assert.False(f.Events.Update(e with { Id = "missing" }));
    }

    [Fact]
    public void Recurring_lists_only_repeating_non_deleted_events()
    {
        using var f = new DbFixture();
        f.Events.Add("once", "2026-11-08T00:00:00Z");
        var a = f.Events.Add("rrule", "2026-11-08T00:00:00Z", rrule: "FREQ=WEEKLY");
        var b = f.Events.Add("ext", "2026-11-09T00:00:00Z", extRule: "ebbinghaus");
        var d = f.Events.Add("deleted", "2026-11-10T00:00:00Z", rrule: "FREQ=DAILY");
        f.Events.SoftDelete(d.Id);
        Assert.Equal([a.Id, b.Id], f.Events.GetRecurring().Select(x => x.Id));
        Assert.Empty(f.Events.GetStartingBetween("2026-11-10T00:00:00Z", "2026-11-11T00:00:00Z"));
    }
}

public class ReminderTests
{
    [Fact]
    public void Add_list_mark_fired_and_delete_by_owner()
    {
        using var f = new DbFixture();
        var ev = f.Events.Add("e", "2026-11-08T00:00:00Z");
        var r1 = f.Reminders.Add(ReminderOwner.Event, ev.Id, 10, sound: "dong");
        f.Reminders.Add(ReminderOwner.Event, ev.Id, 1440);
        f.Reminders.Add(ReminderOwner.Item, "other-owner", 5);
        var list = f.Reminders.GetByOwner(ReminderOwner.Event, ev.Id);
        Assert.Equal([1440, 10], list.Select(r => r.OffsetMinutes));
        Assert.True(f.Reminders.MarkFired(r1.Id, 42));
        Assert.Equal(42, f.Reminders.GetByOwner(ReminderOwner.Event, ev.Id).Single(r => r.Id == r1.Id).FiredAt);
        Assert.Equal(2, f.Reminders.DeleteByOwner(ReminderOwner.Event, ev.Id));
        Assert.Single(f.Reminders.GetByOwner(ReminderOwner.Item, "other-owner"));
    }
}

public class CountdownNoteTests
{
    [Fact]
    public void Countdown_crud()
    {
        using var f = new DbFixture();
        var b = f.Countdowns.Add("생일", "2027-03-01", CountdownType.Birthday, showCard: true, lunar: true);
        f.Countdowns.Add("시험", "2026-12-01", CountdownType.Exam, showBottom: true);
        Assert.Equal(["시험", "생일"], f.Countdowns.List().Select(c => c.Title));   // ordered by date
        f.Countdowns.Upsert(b with { Title = "생일(수정)", ShowCard = false });
        var got = f.Countdowns.List().Single(c => c.Id == b.Id);
        Assert.Equal("생일(수정)", got.Title); Assert.False(got.ShowCard); Assert.True(got.Lunar);
        Assert.True(f.Countdowns.Delete(b.Id));
        Assert.False(f.Countdowns.Delete(b.Id));
    }

    [Fact]
    public void Note_update_history_and_soft_delete()
    {
        using var f = new DbFixture();
        var n = f.Notes.Add("제목", "내용1");
        f.Clock.Advance();
        f.Notes.Update(n.Id, "제목2", "내용2");
        Assert.Equal("제목2", f.Notes.Get(n.Id)!.Title);
        Assert.Equal(["내용1"], f.History.List(EntityNames.Note, n.Id).Select(h => h.Content));
        Assert.Single(f.Notes.List());
        f.Notes.SoftDelete(n.Id);
        Assert.Empty(f.Notes.List());
        Assert.Null(f.Notes.Update("missing", "x", "y"));
    }

    [Fact]
    public void History_can_be_deleted_per_entity()
    {
        using var f = new DbFixture();
        var a = f.Items.Add("2026-11-08", "a1"); f.Items.UpdateContent(a.Id, "a2");
        var b = f.Items.Add("2026-11-08", "b1"); f.Items.UpdateContent(b.Id, "b2");
        Assert.Equal(1, f.History.Delete(EntityNames.Item, a.Id));
        Assert.Empty(f.History.List(EntityNames.Item, a.Id));
        Assert.Single(f.History.List(EntityNames.Item, b.Id));
    }
}

public class SettingsTests
{
    [Fact]
    public void Typed_roundtrip_and_fallbacks()
    {
        using var f = new DbFixture();
        Assert.Equal(5, f.Settings.GetInt("board.max_rows", 5));
        Assert.True(f.Settings.GetBool("board.show_lunar", true));
        f.Settings.SetInt("board.max_rows", 6);
        f.Settings.SetBool("board.show_lunar", false);
        f.Settings.SetString("board.font", "맑은 고딕");
        Assert.Equal(6, f.Settings.GetInt("board.max_rows", 5));
        Assert.False(f.Settings.GetBool("board.show_lunar", true));
        Assert.Equal("맑은 고딕", f.Settings.GetString("board.font"));
        f.Settings.SetInt("board.max_rows", 7);                       // overwrite
        Assert.Equal(7, f.Settings.GetInt("board.max_rows", 5));
        f.Settings.SetString("board.bad_int", "abc");
        Assert.Equal(3, f.Settings.GetInt("board.bad_int", 3));       // unparsable -> fallback
    }

    [Fact]
    public void Prefix_queries_treat_like_wildcards_literally()
    {
        using var f = new DbFixture();
        f.Settings.SetString("board_a", "1");     // '_' must not match any character
        f.Settings.SetString("boardXa", "2");
        f.Settings.SetString("app.x", "3");
        Assert.Equal(["board_a"], f.Settings.GetAll("board_").Keys);
        Assert.Equal(3, f.Settings.GetAll().Count);
        Assert.Equal(1, f.Settings.ResetPrefix("board_"));
        Assert.Equal(["app.x", "boardXa"], f.Settings.GetAll().Keys);
        Assert.True(f.Settings.Remove("app.x"));
        Assert.False(f.Settings.Remove("app.x"));
    }
}

public class BackupTests
{
    [Fact]
    public void Backup_produces_an_openable_copy_with_the_data()
    {
        using var f = new DbFixture();
        var item = f.Items.Add("2026-11-08", "백업 대상");
        f.Settings.SetInt("k", 1);
        var dest = f.TempFile("backup/copy.db");
        f.Db.Backup(dest);

        var copy = new DeskCalDb(dest);
        copy.Migrate();                                   // already current: no-op
        Assert.Equal(5, copy.SchemaVersion());
        var items = new ItemRepository(copy, f.Clock);
        Assert.Equal("백업 대상", items.Get(item.Id)!.Content);
        Assert.Equal(1, new SettingsStore(copy).GetInt("k", 0));
    }
}
