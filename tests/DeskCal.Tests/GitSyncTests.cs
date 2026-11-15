using System.Diagnostics;
using DeskCal.Core;
using DeskCal.Data;
using DeskCal.Sync;

namespace DeskCal.Tests;

public class GitMergeTests
{
    private static SyncDoc D(string key, string text, long stamp = 0) => new(key, text, stamp);
    private static Dictionary<string, SyncDoc> Docs(params SyncDoc[] d) => d.ToDictionary(x => x.Key);
    private static Dictionary<string, string> Base(params SyncDoc[] d) => d.ToDictionary(x => x.Key, x => x.Hash);
    private static readonly Dictionary<string, long> NoneDeleted = [];

    [Fact]
    public void Nothing_changed_does_nothing_and_agrees_on_everything()
    {
        var a = D("items/1", "a");
        var p = GitMerge.Plan(Docs(a), NoneDeleted, Docs(a), Base(a));
        Assert.False(p.ChangesLocal); Assert.False(p.ChangesRemote);
        Assert.Equal(a.Hash, p.Agreed["items/1"]);
    }

    [Fact]
    public void A_change_on_one_side_goes_to_the_other()
    {
        var old = D("items/1", "old");
        var p = GitMerge.Plan(Docs(old), NoneDeleted, Docs(D("items/1", "new")), Base(old));
        Assert.Equal("new", Assert.Single(p.ApplyLocal).Text);
        Assert.Empty(p.WriteRemote);

        p = GitMerge.Plan(Docs(D("items/1", "new")), NoneDeleted, Docs(old), Base(old));
        Assert.Equal("new", Assert.Single(p.WriteRemote).Text);
        Assert.Empty(p.ApplyLocal);
    }

    [Fact]
    public void Deletions_travel_both_ways_when_the_other_side_did_not_edit()
    {
        var a = D("items/1", "a");
        var p = GitMerge.Plan(Docs(), NoneDeleted, Docs(a), Base(a));
        Assert.Equal("items/1", Assert.Single(p.DeleteRemote));
        Assert.False(p.Agreed.ContainsKey("items/1"));

        p = GitMerge.Plan(Docs(a), NoneDeleted, Docs(), Base(a));
        Assert.Equal("items/1", Assert.Single(p.DeleteLocal));
    }

    [Fact]
    public void Both_edited_keeps_the_later_edit_and_counts_a_conflict()
    {
        var b = D("items/1", "base", 1);
        var p = GitMerge.Plan(Docs(D("items/1", "here", 5)), NoneDeleted, Docs(D("items/1", "there", 9)), Base(b));
        Assert.Equal("there", Assert.Single(p.ApplyLocal).Text);
        Assert.Equal(1, p.Conflicts);

        p = GitMerge.Plan(Docs(D("items/1", "here", 9)), NoneDeleted, Docs(D("items/1", "there", 5)), Base(b));
        Assert.Equal("here", Assert.Single(p.WriteRemote).Text);

        p = GitMerge.Plan(Docs(D("countdowns/1", "here")), NoneDeleted, Docs(D("countdowns/1", "there")), Base(D("countdowns/1", "base")));
        Assert.Equal("here", Assert.Single(p.WriteRemote).Text);              // no times known: this side wins
    }

    [Fact]
    public void An_edit_beats_a_deletion_unless_the_deletion_is_later()
    {
        var b = D("items/1", "base", 1);
        var p = GitMerge.Plan(Docs(), NoneDeleted, Docs(D("items/1", "edited", 5)), Base(b));
        Assert.Equal("edited", Assert.Single(p.ApplyLocal).Text);              // deleted here, edited there: keep it

        p = GitMerge.Plan(Docs(D("items/1", "edited", 5)), NoneDeleted, Docs(), Base(b));
        Assert.Equal("edited", Assert.Single(p.WriteRemote).Text);             // edited here, deleted there: put it back

        p = GitMerge.Plan(Docs(), new Dictionary<string, long> { ["items/1"] = 9 }, Docs(D("items/1", "edited", 5)), Base(b));
        Assert.Equal("items/1", Assert.Single(p.DeleteRemote));                 // the deletion here came after the edit there

        p = GitMerge.Plan(Docs(), new Dictionary<string, long> { ["items/1"] = 9 }, Docs(D("items/1", "new elsewhere", 5)), Base());
        Assert.Equal("items/1", Assert.Single(p.DeleteRemote));                 // never synced, but deleted here after it was made there
    }

    [Fact]
    public void The_first_sync_is_a_union()
    {
        var p = GitMerge.Plan(Docs(D("items/1", "mine")), NoneDeleted, Docs(D("items/2", "theirs")), Base());
        Assert.Equal("items/2", Assert.Single(p.ApplyLocal).Key);
        Assert.Equal("items/1", Assert.Single(p.WriteRemote).Key);
        Assert.Equal(0, p.Conflicts);
        Assert.Equal(2, p.Agreed.Count);
    }
}

public sealed class CalendarSnapshotTests : IDisposable
{
    private readonly DbFixture _a = new(), _b = new();
    public void Dispose() { _a.Dispose(); _b.Dispose(); }

    private static CalendarSnapshot Snap(DbFixture f) => new(f.Db, f.Clock, f.TempFile("holidays.json"));

    internal static void Seed(DbFixture f)
    {
        var item = f.Items.Add("2026-11-20", "치과 예약 확인");
        f.Items.SetColor(item.Id, "#FFE066");
        f.Reminders.Add(ReminderOwner.Item, item.Id, 30, "09:00");
        var ev = f.Events.Add("팀 회의", "2026-11-18T10:00", "2026-11-18T11:00", rrule: "FREQ=WEEKLY", body: "회의실 \"A\"\n줄바꿈");
        f.Events.AddException(ev.Id, new DateOnly(2026, 11, 25));
        f.Reminders.Add(ReminderOwner.Event, ev.Id, 10);
        f.Countdowns.Add("수능", "2026-11-19", CountdownType.Exam, showCard: true);
        f.Notes.Add("장보기", "우유, 계란");
        var tt = new TimetableRepository(f.Db, f.Clock);
        tt.SetCell(DayOfWeek.Monday, 1, "수학", "301");
        tt.SaveConfig(f.Settings, new TimetableConfig { Periods = 6 });
        new ItemSeriesRepository(f.Db, f.Clock).SetRepeat(f.Items.Add("2026-11-16", "운동").Id, new RecurrenceRule { Kind = RepeatKind.Weekly }, new DateOnly(2026, 11, 16));
        File.WriteAllText(f.TempFile("holidays.json"), "// 주석\r\n[ { \"date\": \"2026-12-24\", \"name\": \"휴가\" } ]\r\n");
        var gone = f.Items.Add("2026-11-21", "지운 것");
        f.Items.SoftDelete(gone.Id);
    }

    [Fact]
    public void Every_kind_of_record_survives_a_trip_to_another_database()
    {
        Seed(_a);
        var docs = Snap(_a).Collect();
        Assert.Contains(docs.Docs.Keys, k => k.StartsWith("items/"));
        Assert.Contains(docs.Docs.Keys, k => k.StartsWith("series/"));
        Assert.Contains("timetable", docs.Docs.Keys);
        Assert.Contains("holidays", docs.Docs.Keys);
        Assert.Single(docs.DeletedAt);                                         // the soft-deleted item is a tombstone, not a record
        Assert.All(docs.Docs.Values, d => Assert.Equal(d.Text, CalendarSnapshot.ParseRemote(CalendarSnapshot.KindOf(d.Key), d.Text).Text));   // canonical

        var r = Snap(_b).Apply(docs.Docs.Values.ToList(), [], new Dictionary<string, string>());
        Assert.Empty(r.Skipped);
        Assert.True(r.HolidaysChanged);

        var back = Snap(_b).Collect().Docs;
        Assert.Equal(docs.Docs.Keys.Order(), back.Keys.Order());
        foreach (var (k, d) in docs.Docs) Assert.Equal(d.Text, back[k].Text);
        Assert.Equal("수학", new TimetableRepository(_b.Db, _b.Clock).GetCells().Single().Subject);
        Assert.Equal(6, new TimetableRepository(_b.Db, _b.Clock).LoadConfig(_b.Settings).Periods);
    }

    [Fact]
    public void Deleting_through_a_sync_soft_deletes_and_keeps_history_of_overwritten_text()
    {
        var item = _a.Items.Add("2026-11-20", "원래 글");
        var snap = Snap(_a);
        var before = snap.Collect().Docs[$"items/{item.Id}"];

        var changed = CalendarSnapshot.ParseRemote("items", before.Text.Replace("원래 글", "다른 컴퓨터에서 고친 글"));
        snap.Apply([changed], [], new Dictionary<string, string> { [before.Key] = before.Hash });
        Assert.Equal("다른 컴퓨터에서 고친 글", _a.Items.Get(item.Id)!.Content);
        Assert.Contains(_a.History.List(EntityNames.Item, item.Id), h => h.Content == "원래 글");

        var now = snap.Collect().Docs[before.Key];
        snap.Apply([], [before.Key], new Dictionary<string, string> { [before.Key] = now.Hash });
        Assert.True(_a.Items.Get(item.Id)!.Deleted);
    }

    [Fact]
    public void A_record_edited_while_the_sync_ran_is_left_for_the_next_sync()
    {
        var item = _a.Items.Add("2026-11-20", "처음");
        var snap = Snap(_a);
        var seen = snap.Collect().Docs[$"items/{item.Id}"];
        _a.Items.UpdateContent(item.Id, "그 사이에 고침");

        var incoming = CalendarSnapshot.ParseRemote("items", seen.Text.Replace("처음", "저쪽"));
        var r = snap.Apply([incoming], [], new Dictionary<string, string> { [seen.Key] = seen.Hash });
        Assert.Contains(seen.Key, r.Skipped);
        Assert.Equal("그 사이에 고침", _a.Items.Get(item.Id)!.Content);
    }

    [Fact]
    public void Broken_records_are_refused()
    {
        Assert.Throws<FormatException>(() => CalendarSnapshot.ParseRemote("items", "{ not json"));
        Assert.Throws<FormatException>(() => CalendarSnapshot.ParseRemote("items", """{ "id": "x", "day": "어제", "content": "" }"""));
        Assert.Throws<FormatException>(() => CalendarSnapshot.ParseRemote("events", """{ "id": "x" }"""));
    }

    [Fact]
    public void Repeating_items_made_on_two_computers_are_the_same_items()
    {
        var series = new ItemSeriesRepository(_a.Db, _a.Clock);
        var first = _a.Items.Add("2026-11-16", "운동");
        series.SetRepeat(first.Id, new RecurrenceRule { Kind = RepeatKind.Weekly }, new DateOnly(2026, 11, 16));
        Snap(_b).Apply(Snap(_a).Collect().Docs.Values.ToList(), [], new Dictionary<string, string>());

        // a week later both computers extend the series on their own
        new ItemSeriesRepository(_a.Db, _a.Clock).Extend(new DateOnly(2026, 11, 30));
        new ItemSeriesRepository(_b.Db, _b.Clock).Extend(new DateOnly(2026, 11, 30));
        var a = Snap(_a).Collect().Docs.Keys.Where(k => k.StartsWith("items/")).Order().ToList();
        var b = Snap(_b).Collect().Docs.Keys.Where(k => k.StartsWith("items/")).Order().ToList();
        Assert.Equal(a, b);
        Assert.Equal(a.Count, _a.Items.GetRange("2026-01-01", "2028-12-31").Select(i => i.Day).Distinct().Count());   // one per day, no doubles
    }
}

/// <summary>Two "computers" (two databases, two checkouts) sharing a calendar folder through a real git repository on disk.</summary>
public sealed class GitCalendarSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskcal-git-" + Guid.NewGuid().ToString("N"));
    private readonly DbFixture _a = new(), _b = new();
    private readonly string _remote;

    public GitCalendarSyncTests()
    {
        Directory.CreateDirectory(_dir);
        _remote = Path.Combine(_dir, "remote.git");
        Git(_dir, "init", "--bare", "-q", "-b", "main", _remote);
        // the repository already holds other things (like a notes vault) that the sync must leave alone
        var seed = Path.Combine(_dir, "seed");
        Git(_dir, "clone", "-q", _remote, seed);
        File.WriteAllText(Path.Combine(seed, "note.md"), "# 다른 메모\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "seed");
        Git(seed, "push", "-q", "origin", "main");
    }

    public void Dispose()
    {
        _a.Dispose(); _b.Dispose();
        try
        {
            foreach (var f in new DirectoryInfo(_dir).EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal;
            Directory.Delete(_dir, true);
        }
        catch (IOException) { }
    }

    private GitSyncOptions Options(string pc) =>
        new(_remote, "", GitSyncOptions.DefaultFolder, Path.Combine(_dir, pc, "repo"), Path.Combine(_dir, pc, "base.json"), Path.Combine(_dir, pc, "backup"));

    private Task<GitSyncReport> Sync(DbFixture f, string pc) =>
        new GitCalendarSync(f.Db, new CalendarSnapshot(f.Db, f.Clock, f.TempFile("holidays.json")), new GitCli()).RunAsync(Options(pc), CancellationToken.None);

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {e}");
        return o;
    }

    private string RemoteFiles() => Git(_dir, "--git-dir", _remote, "ls-tree", "-r", "--name-only", "main");

    [Fact]
    public async Task Two_computers_share_one_calendar()
    {
        CalendarSnapshotTests.Seed(_a);
        var first = await Sync(_a, "pcA");
        Assert.True(first.Pushed);
        Assert.True(first.Sent > 5);
        var files = RemoteFiles();
        Assert.Contains("note.md", files);                                     // the rest of the repository is untouched
        Assert.Contains("my_calendar_sync/README.md", files);
        Assert.Contains("my_calendar_sync/timetable.json", files);
        Assert.Contains("my_calendar_sync/holidays.json", files);
        Assert.Contains("my_calendar_sync/events/", files);

        var second = await Sync(_b, "pcB");                                    // a new computer gets everything
        Assert.Equal(first.Sent, second.Received);
        Assert.False(second.Pushed);
        Assert.Equal(new CalendarSnapshot(_a.Db, _a.Clock, _a.TempFile("holidays.json")).Collect().Docs.Keys.Order(),
                     new CalendarSnapshot(_b.Db, _b.Clock, _b.TempFile("holidays.json")).Collect().Docs.Keys.Order());
        Assert.True(File.Exists(_b.TempFile("holidays.json")));

        // B edits one, deletes one, adds one; A picks it all up
        _b.Clock.Advance(60_000);
        var note = _b.Notes.List().Single();
        _b.Notes.Update(note.Id, note.Title, "우유, 계란, 빵");
        var task = _b.Items.GetByDay("2026-11-20").Single();
        _b.Items.SoftDelete(task.Id);
        _b.Items.Add("2026-11-22", "B에서 추가");
        var fromB = await Sync(_b, "pcB");
        Assert.Equal(2, fromB.Sent); Assert.Equal(1, fromB.RemovedThere);

        var toA = await Sync(_a, "pcA");
        Assert.Equal(2, toA.Received); Assert.Equal(1, toA.RemovedHere);
        Assert.Equal("우유, 계란, 빵", _a.Notes.Get(note.Id)!.Content);
        Assert.True(_a.Items.Get(task.Id)!.Deleted);
        Assert.Equal("B에서 추가", _a.Items.GetByDay("2026-11-22").Single().Content);

        var quiet = await Sync(_a, "pcA");                                     // settled: nothing more to do
        Assert.Equal("바뀐 것이 없습니다.", quiet.Summary());
        Assert.False(quiet.Pushed);
    }

    [Fact]
    public async Task Edits_on_both_sides_merge_and_the_later_edit_of_one_record_wins()
    {
        var shared = _a.Items.Add("2026-11-20", "공유");
        await Sync(_a, "pcA");
        await Sync(_b, "pcB");

        _a.Clock.Advance(1000);
        _a.Items.UpdateContent(shared.Id, "A가 먼저 고침");
        _a.Items.Add("2026-11-21", "A만의 할 일");
        _b.Clock.Advance(5000);
        _b.Items.UpdateContent(shared.Id, "B가 나중에 고침");
        _b.Items.Add("2026-11-23", "B만의 할 일");

        await Sync(_a, "pcA");
        var b = await Sync(_b, "pcB");
        Assert.Equal(1, b.Conflicts);
        await Sync(_a, "pcA");

        foreach (var f in new[] { _a, _b })
        {
            Assert.Equal("B가 나중에 고침", f.Items.Get(shared.Id)!.Content);
            Assert.Single(f.Items.GetByDay("2026-11-21"));
            Assert.Single(f.Items.GetByDay("2026-11-23"));
        }
    }

    [Fact]
    public async Task Commits_made_by_others_in_the_repository_are_kept()
    {
        _a.Items.Add("2026-11-20", "A");
        await Sync(_a, "pcA");
        await Sync(_b, "pcB");

        // somebody else (another notes app) commits to the repository; B's checkout is behind it when B syncs
        var other = Path.Combine(_dir, "other");
        Git(_dir, "clone", "-q", _remote, other);
        File.WriteAllText(Path.Combine(other, "note2.md"), "x");
        Git(other, "add", ".");
        Git(other, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "other");
        Git(other, "push", "-q", "origin", "main");

        _b.Items.Add("2026-11-21", "B");
        var r = await Sync(_b, "pcB");
        Assert.True(r.Pushed);
        var files = RemoteFiles();
        Assert.Contains("note2.md", files);
        Assert.Contains("note.md", files);
    }

    [Fact]
    public async Task Two_computers_syncing_at_the_same_moment_both_get_their_changes_in()
    {
        await Sync(_a, "pcA");
        await Sync(_b, "pcB");
        for (int i = 0; i < 3; i++)
        {
            _a.Items.Add("2026-11-20", $"A{i}");
            _b.Items.Add("2026-11-21", $"B{i}");
            var both = await Task.WhenAll(Task.Run(() => Sync(_a, "pcA")), Task.Run(() => Sync(_b, "pcB")));   // one of them is usually overtaken and retries
            Assert.All(both, r => Assert.True(r.Pushed));
        }
        await Sync(_a, "pcA");
        await Sync(_b, "pcB");
        foreach (var f in new[] { _a, _b })
        {
            Assert.Equal(3, f.Items.GetByDay("2026-11-20").Count);
            Assert.Equal(3, f.Items.GetByDay("2026-11-21").Count);
        }
    }

    [Fact]
    public async Task A_broken_file_in_the_folder_is_skipped_without_deleting_the_record_here()
    {
        var item = _a.Items.Add("2026-11-20", "소중한 할 일");
        await Sync(_a, "pcA");

        var other = Path.Combine(_dir, "hand");
        Git(_dir, "clone", "-q", _remote, other);
        File.WriteAllText(Path.Combine(other, "my_calendar_sync", "items", item.Id + ".json"), "{ 망가짐");
        Git(other, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-a", "-m", "broken");
        Git(other, "push", "-q", "origin", "main");

        var r = await Sync(_a, "pcA");
        Assert.Contains(r.Warnings, w => w.Contains(item.Id));
        Assert.False(_a.Items.Get(item.Id)!.Deleted);
    }

    [Fact]
    public async Task An_empty_repository_gets_its_first_commit()
    {
        var empty = Path.Combine(_dir, "empty.git");
        Git(_dir, "init", "--bare", "-q", "-b", "main", empty);
        _a.Items.Add("2026-11-20", "첫 항목");
        var o = Options("pcE") with { RepoUrl = empty };
        var r = await new GitCalendarSync(_a.Db, new CalendarSnapshot(_a.Db, _a.Clock, _a.TempFile("h.json")), new GitCli()).RunAsync(o, CancellationToken.None);
        Assert.True(r.Pushed);
        Assert.Contains("my_calendar_sync/items/", Git(_dir, "--git-dir", empty, "ls-tree", "-r", "--name-only", "main"));
    }
}
