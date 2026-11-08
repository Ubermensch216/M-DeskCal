using System.Net;
using System.Net.Sockets;
using System.Text;
using DeskCal.Core;
using DeskCal.Data;
using DeskCal.Sync;

namespace DeskCal.Tests;

public class SyncPlannerTests
{
    private static SyncState S(bool localExists = true, bool deleted = false, long stamp = 10, bool linked = true, string? linkEtag = "v1", long linkStamp = 10,
                               bool prot = false, bool remote = true, string? remoteEtag = "v1", bool twoWay = true) =>
        new(localExists, deleted, stamp, linked, linkEtag, linkStamp, prot, remote, remoteEtag, twoWay);

    [Fact]
    public void Nothing_changed_means_nothing_to_do() => Assert.Equal(SyncAction.None, SyncPlanner.Decide(S()));

    [Fact]
    public void Only_the_server_changed_pulls_and_only_this_program_changed_pushes()
    {
        Assert.Equal(SyncAction.PullUpdate, SyncPlanner.Decide(S(remoteEtag: "v2")));
        Assert.Equal(SyncAction.PushUpdate, SyncPlanner.Decide(S(stamp: 11)));
    }

    [Fact]
    public void Both_changed_is_a_conflict_in_either_mode()
    {
        Assert.Equal(SyncAction.Conflict, SyncPlanner.Decide(S(stamp: 11, remoteEtag: "v2")));
        Assert.Equal(SyncAction.Conflict, SyncPlanner.Decide(S(stamp: 11, remoteEtag: "v2", twoWay: false)));
    }

    [Fact]
    public void One_way_accounts_and_foreign_events_are_never_written_to_the_server()
    {
        Assert.Equal(SyncAction.None, SyncPlanner.Decide(S(stamp: 11, twoWay: false)));
        Assert.Equal(SyncAction.None, SyncPlanner.Decide(S(stamp: 11, prot: true)));
        Assert.Equal(SyncAction.None, SyncPlanner.Decide(S(deleted: true, twoWay: false)));
        Assert.Equal(SyncAction.None, SyncPlanner.Decide(S(deleted: true, prot: true)));
        Assert.Equal(SyncAction.PullNew, SyncPlanner.Decide(S(localExists: false, linked: false)));
    }

    [Fact]
    public void A_delete_only_wins_over_a_copy_nobody_edited()
    {
        Assert.Equal(SyncAction.DeleteRemote, SyncPlanner.Decide(S(deleted: true)));
        Assert.Equal(SyncAction.Restore, SyncPlanner.Decide(S(deleted: true, remoteEtag: "v2")));           // edited elsewhere: keep it
        Assert.Equal(SyncAction.DeleteLocal, SyncPlanner.Decide(S(remote: false)));                         // server deleted, unchanged here
        Assert.Equal(SyncAction.PushNew, SyncPlanner.Decide(S(remote: false, stamp: 11)));                  // server deleted, edited here: put it back
        Assert.Equal(SyncAction.DropLink, SyncPlanner.Decide(S(remote: false, stamp: 11, twoWay: false)));  // one-way: keep the local copy, forget the link
        Assert.Equal(SyncAction.DropLink, SyncPlanner.Decide(S(remote: false, deleted: true)));             // gone on both sides
    }

    [Fact]
    public void Unlinked_pairs()
    {
        Assert.Equal(SyncAction.PullNew, SyncPlanner.Decide(S(localExists: false, linked: false, linkEtag: null)));
        Assert.Equal(SyncAction.PushNew, SyncPlanner.Decide(S(linked: false, linkEtag: null, remote: false)));
        Assert.Equal(SyncAction.None, SyncPlanner.Decide(S(linked: false, linkEtag: null, remote: false, twoWay: false)));
        Assert.Equal(SyncAction.AdoptAndPush, SyncPlanner.Decide(S(linked: false, linkEtag: null)));
        Assert.Equal(SyncAction.AdoptAndPull, SyncPlanner.Decide(S(linked: false, linkEtag: null, twoWay: false)));
        Assert.Equal(SyncAction.AdoptAndPull, SyncPlanner.Decide(S(linked: false, linkEtag: null, prot: true)));
        Assert.Equal(SyncAction.Restore, SyncPlanner.Decide(S(linked: false, linkEtag: null, deleted: true)));
        Assert.Equal(SyncAction.None, SyncPlanner.Decide(S(localExists: true, deleted: true, linked: false, linkEtag: null, remote: false)));
    }
}

/// <summary>A CalDAV server in memory, reachable over real HTTP on this computer.</summary>
internal sealed class FakeCalDavServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private int _counter;
    public string Url { get; }
    public Dictionary<string, (string Etag, string Data)> Store { get; } = [];
    public string User = "me", Password = "pw";
    public int PutCount, DeleteCount, ReportCount;
    public string? LastIfMatch, LastIfNoneMatch;
    public Action<string>? BeforePut;                 // lets a test change the server between the engine's list and its write
    public bool OmitEtagOnPut;

    public FakeCalDavServer()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        Url = $"http://localhost:{port}/dav/cal/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _loop = Task.Run(Loop);
    }

    public string Put(string name, string data) { lock (Store) { Store[name] = ("\"v" + ++_counter + "\"", data); return Store[name].Etag; } }
    public string? Data(string name) { lock (Store) return Store.TryGetValue(name, out var v) ? v.Data : null; }
    public string? Etag(string name) { lock (Store) return Store.TryGetValue(name, out var v) ? v.Etag : null; }
    public int Count { get { lock (Store) return Store.Count; } }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            try { Handle(ctx); } catch { try { ctx.Response.StatusCode = 500; } catch { } }
            finally { try { ctx.Response.Close(); } catch { } }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request; var resp = ctx.Response;
        string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Password));
        if (req.Headers["Authorization"] != expected) { resp.StatusCode = 401; return; }

        string path = req.Url!.AbsolutePath;
        string name = Uri.UnescapeDataString(path[(new Uri(Url).AbsolutePath.Length)..]);

        if (req.HttpMethod == "REPORT")
        {
            ReportCount++;
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">");
            lock (Store)
                foreach (var (n, v) in Store)
                    sb.Append($"<d:response><d:href>/dav/cal/{Uri.EscapeDataString(n)}</d:href><d:propstat><d:prop><d:getetag>{v.Etag}</d:getetag>" +
                              $"<c:calendar-data>{System.Security.SecurityElement.Escape(v.Data)}</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
            sb.Append("</d:multistatus>");
            Write(resp, 207, sb.ToString(), "application/xml");
        }
        else if (req.HttpMethod == "GET")
        {
            string? d; string? e; lock (Store) { d = Data(name); e = Etag(name); }
            if (d is null) { resp.StatusCode = 404; return; }
            resp.AddHeader("ETag", e!);
            Write(resp, 200, d, "text/calendar");
        }
        else if (req.HttpMethod == "PUT")
        {
            BeforePut?.Invoke(name);
            string body = new StreamReader(req.InputStream, Encoding.UTF8).ReadToEnd();
            LastIfMatch = req.Headers["If-Match"]; LastIfNoneMatch = req.Headers["If-None-Match"];
            lock (Store)
            {
                bool exists = Store.TryGetValue(name, out var cur);
                if ((LastIfNoneMatch == "*" && exists) || (LastIfMatch is not null && (!exists || cur.Etag != LastIfMatch))) { resp.StatusCode = 412; return; }
                PutCount++;
                var tag = Put(name, body);
                if (!OmitEtagOnPut) resp.AddHeader("ETag", tag);
                resp.StatusCode = exists ? 204 : 201;
            }
        }
        else if (req.HttpMethod == "DELETE")
        {
            lock (Store)
            {
                if (!Store.TryGetValue(name, out var cur)) { resp.StatusCode = 404; return; }
                string? m = req.Headers["If-Match"];
                if (m is not null && m != cur.Etag) { resp.StatusCode = 412; return; }
                Store.Remove(name); DeleteCount++; resp.StatusCode = 204;
            }
        }
        else resp.StatusCode = 405;
    }

    private static void Write(HttpListenerResponse r, int status, string body, string type)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        r.StatusCode = status; r.ContentType = type + "; charset=utf-8"; r.ContentLength64 = bytes.Length;
        r.OutputStream.Write(bytes, 0, bytes.Length);
    }

    public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }

    public static string Foreign(string uid, string summary, string start = "20261110T100000", string end = "20261110T110000", string extra = "") =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Other Corp//Calendar//EN\r\nBEGIN:VEVENT\r\nUID:" + uid + "\r\nDTSTAMP:20261101T000000Z\r\nDTSTART:" + start +
        "\r\nDTEND:" + end + "\r\nSUMMARY:" + summary + "\r\n" + extra + "END:VEVENT\r\nEND:VCALENDAR\r\n";
}

internal sealed class FakeProtector : ISecretProtector
{
    public string Protect(string plain) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
    public string? Unprotect(string stored) => stored.StartsWith("enc:") ? Encoding.UTF8.GetString(Convert.FromBase64String(stored[4..])) : null;
}

public class SyncEngineTests : IDisposable
{
    private readonly DbFixture _f = new();
    private readonly FakeCalDavServer _srv = new();
    private readonly FakeProtector _prot = new();
    public void Dispose() { _srv.Dispose(); _f.Dispose(); }

    private SyncRepository Repo => new(_f.Db, _f.Clock);
    private SyncAccount Account(bool twoWay = true) =>
        Repo.ListAccounts().FirstOrDefault() ?? Repo.AddAccount(SyncRepository.KindCalDav, "테스트", _srv.Url, _srv.User, _prot.Protect(_srv.Password), twoWay);

    private async Task<SyncReport> Sync(bool twoWay = true)
    {
        var acc = Account(twoWay);
        _f.Clock.Advance(1000);
        return await new SyncEngine(_f.Db, _f.Clock).RunAsync(acc, CalDavProvider.Create(acc, _prot));
    }

    private EventEntry Only(string title) => _f.Events.GetAllActive().Single(e => e.Title == title);

    [Fact]
    public async Task A_server_event_is_pulled_once_and_a_second_run_changes_nothing()
    {
        _srv.Put("a.ics", FakeCalDavServer.Foreign("abc-1@example.com", "치과", extra: "DESCRIPTION:정기 검진\r\n"));
        var r = await Sync();
        Assert.Equal(1, r.PulledNew);
        var e = Only("치과");
        Assert.Equal(("2026-11-10T10:00", "2026-11-10T11:00", "정기 검진"), (e.Start, e.End, e.Body));

        var again = await Sync();
        Assert.Equal("변경 없음", again.Summary());
        Assert.Equal(0, _srv.PutCount);                                               // a foreign event is never written back
        Assert.Single(_f.Events.GetAllActive());
    }

    [Fact]
    public async Task A_local_event_is_pushed_with_its_own_uid_and_is_stable_afterwards()
    {
        var ev = _f.Events.Add("회의", "2026-11-12T09:00", "2026-11-12T10:00");
        var r = await Sync();
        Assert.Equal(1, r.PushedNew);
        Assert.Equal("*", _srv.LastIfNoneMatch);                                      // creating must not overwrite something that appeared meanwhile
        string data = _srv.Data(ev.Id + "@m-deskcal.ics")!;
        Assert.Contains("UID:" + ev.Id + "@m-deskcal", data);
        Assert.Contains("SUMMARY:회의", data);

        Assert.Equal("변경 없음", (await Sync()).Summary());
        Assert.Equal(1, _srv.PutCount);
    }

    [Fact]
    public async Task Editing_an_own_event_updates_the_server_only_if_it_still_has_the_known_version()
    {
        var ev = _f.Events.Add("회의", "2026-11-12T09:00");
        await Sync();
        string etagBefore = _srv.Store.Values.Single().Etag;

        _f.Clock.Advance(); _f.Events.Update(_f.Events.Get(ev.Id)! with { Title = "회의(수정)" });
        var r = await Sync();
        Assert.Equal(1, r.PushedUpdated);
        Assert.Equal(etagBefore, _srv.LastIfMatch);
        Assert.Contains("SUMMARY:회의(수정)", _srv.Store.Values.Single().Data);
        Assert.Equal("변경 없음", (await Sync()).Summary());
    }

    [Fact]
    public async Task An_event_made_by_another_program_is_never_overwritten_or_deleted()
    {
        _srv.Put("a.ics", FakeCalDavServer.Foreign("abc-1@example.com", "치과", extra: "ATTENDEE:mailto:x@example.com\r\n"));
        await Sync();
        string before = _srv.Data("a.ics")!;

        _f.Clock.Advance(); _f.Events.Update(Only("치과") with { Title = "치과(내가 고침)" });
        var r = await Sync();
        Assert.Equal(1, r.NotSent);
        Assert.Equal(before, _srv.Data("a.ics"));
        Assert.Equal(0, _srv.PutCount);

        _f.Clock.Advance(); _f.Events.SoftDelete(Only("치과(내가 고침)").Id);
        await Sync();
        Assert.Equal(0, _srv.DeleteCount);
        Assert.NotNull(_srv.Data("a.ics"));
    }

    [Fact]
    public async Task A_change_on_the_server_reaches_this_program()
    {
        var ev = _f.Events.Add("회의", "2026-11-12T09:00");
        await Sync();
        string name = _srv.Store.Keys.Single();
        _srv.Put(name, _srv.Data(name)!.Replace("SUMMARY:회의", "SUMMARY:회의(서버에서 수정)").Replace("20261112T090000", "20261112T140000"));

        var r = await Sync();
        Assert.Equal(1, r.PulledUpdated);
        var e = _f.Events.Get(ev.Id)!;
        Assert.Equal(("회의(서버에서 수정)", "2026-11-12T14:00"), (e.Title, e.Start));
        Assert.Equal("변경 없음", (await Sync()).Summary());
    }

    [Fact]
    public async Task When_both_sides_changed_the_server_wins_and_the_local_text_is_kept_as_a_copy()
    {
        var ev = _f.Events.Add("회의", "2026-11-12T09:00");
        await Sync();
        string name = _srv.Store.Keys.Single();
        _srv.Put(name, _srv.Data(name)!.Replace("SUMMARY:회의", "SUMMARY:서버판"));
        _f.Clock.Advance(); _f.Events.Update(_f.Events.Get(ev.Id)! with { Title = "내판" });

        var r = await Sync();
        Assert.Equal(1, r.Conflicts);
        Assert.Equal("서버판", _f.Events.Get(ev.Id)!.Title);
        Assert.Contains(_f.Events.GetAllActive(), e => e.Title == "[충돌] 내판");
        Assert.Contains(r.Notes, n => n.Contains("충돌") || n.Contains("남겼"));

        Assert.Equal(1, r.PushedNew);                                                  // the kept copy goes up like any new event ...
        Assert.Equal("변경 없음", (await Sync()).Summary());                             // ... and then everything is quiet
    }

    [Fact]
    public async Task Deleting_follows_in_both_directions()
    {
        var a = _f.Events.Add("지울 일정", "2026-11-12T09:00");
        var b = _f.Events.Add("서버가 지울 일정", "2026-11-13T09:00");
        await Sync();
        Assert.Equal(2, _srv.Count);

        _f.Clock.Advance(); _f.Events.SoftDelete(a.Id);
        _srv.Store.Remove(_srv.Store.Keys.Single(k => _srv.Store[k].Data.Contains("서버가 지울 일정")));
        var r = await Sync();
        Assert.Equal((1, 1), (r.DeletedRemote, r.DeletedLocal));
        Assert.Equal(0, _srv.Count);
        Assert.True(_f.Events.Get(b.Id)!.Deleted);
        Assert.Equal("변경 없음", (await Sync()).Summary());
    }

    [Fact]
    public async Task A_server_delete_does_not_take_away_an_event_edited_here()
    {
        var ev = _f.Events.Add("회의", "2026-11-12T09:00");
        await Sync();
        _srv.Store.Clear();
        _f.Clock.Advance(); _f.Events.Update(_f.Events.Get(ev.Id)! with { Title = "회의(고침)" });

        var r = await Sync();
        Assert.Equal(1, r.PushedNew);                                                  // put back
        Assert.False(_f.Events.Get(ev.Id)!.Deleted);
        Assert.Equal(1, _srv.Count);
    }

    [Fact]
    public async Task A_read_only_account_pulls_but_never_writes_to_the_server()
    {
        _srv.Put("a.ics", FakeCalDavServer.Foreign("abc-1@example.com", "치과"));
        _f.Events.Add("내 일정", "2026-11-12T09:00");
        var r = await Sync(twoWay: false);
        Assert.Equal(1, r.PulledNew);
        Assert.Equal(0, r.PushedNew);
        Assert.Equal(0, _srv.PutCount + _srv.DeleteCount);

        _f.Clock.Advance(); _f.Events.SoftDelete(Only("치과").Id);
        await Sync(twoWay: false);
        Assert.Equal(0, _srv.DeleteCount);
        Assert.True(_f.Events.Get(IcsIdOf("abc-1@example.com"))!.Deleted);              // and it stays deleted here
    }

    private static string IcsIdOf(string uid) => DeskCal.Core.Exchange.IcsExchange.IdFromUid(uid);

    [Fact]
    public async Task A_second_device_gets_events_the_first_one_made_with_the_same_id()
    {
        var ev = _f.Events.Add("회의", "2026-11-12T09:00");
        await Sync();

        using var other = new DbFixture();
        var repo = new SyncRepository(other.Db, other.Clock);
        var acc = repo.AddAccount(SyncRepository.KindCalDav, "둘째 PC", _srv.Url, _srv.User, _prot.Protect(_srv.Password), twoWay: true);
        var r = await new SyncEngine(other.Db, other.Clock).RunAsync(acc, CalDavProvider.Create(acc, _prot));
        Assert.Equal(1, r.PulledNew);
        Assert.Equal("회의", other.Events.Get(ev.Id)!.Title);                            // same id, so the two devices stay one event
        Assert.Equal(0, r.PushedNew);
    }

    [Fact]
    public async Task A_server_that_changed_between_listing_and_writing_defers_the_write()
    {
        var ev = _f.Events.Add("회의", "2026-11-12T09:00");
        await Sync();
        string name = _srv.Store.Keys.Single();
        _f.Clock.Advance(); _f.Events.Update(_f.Events.Get(ev.Id)! with { Title = "회의(고침)" });
        _srv.BeforePut = n => { _srv.BeforePut = null; _srv.Put(n, _srv.Data(n)!.Replace("SUMMARY:회의", "SUMMARY:누군가 먼저")); };

        var r = await Sync();
        Assert.Equal(1, r.Deferred);
        Assert.Contains("누군가 먼저", _srv.Data(name));                                  // not overwritten

        var next = await Sync();                                                       // now it is a normal conflict
        Assert.Equal(1, next.Conflicts);
        Assert.Equal("누군가 먼저", _f.Events.Get(ev.Id)!.Title);
    }

    [Fact]
    public async Task A_server_that_sends_no_etag_on_put_is_asked_for_it()
    {
        _srv.OmitEtagOnPut = true;
        _f.Events.Add("회의", "2026-11-12T09:00");
        await Sync();
        Assert.Equal("변경 없음", (await Sync()).Summary());
        Assert.Equal(1, _srv.PutCount);
    }

    [Fact]
    public async Task A_wrong_password_is_reported_in_plain_words_and_changes_nothing()
    {
        _srv.Put("a.ics", FakeCalDavServer.Foreign("abc-1@example.com", "치과"));
        Account();
        _srv.Password = "other";
        var ex = await Assert.ThrowsAsync<CalendarServerException>(() => Sync());
        Assert.Contains("비밀번호", ex.Message);
        Assert.Empty(_f.Events.GetAllActive());
    }

    [Fact]
    public async Task One_unreadable_resource_does_not_stop_the_others()
    {
        _srv.Put("bad.ics", "this is not a calendar");
        _srv.Put("good.ics", FakeCalDavServer.Foreign("good-1@example.com", "치과"));
        var r = await Sync();
        Assert.Equal(1, r.PulledNew);
        Assert.Single(_f.Events.GetAllActive());
    }

    [Fact]
    public async Task Repeating_events_and_skipped_days_travel_both_ways()
    {
        var ev = _f.Events.Add("주간 회의", "2026-11-09T10:00", "2026-11-09T11:00", rrule: "FREQ=WEEKLY");
        _f.Events.AddException(ev.Id, new DateOnly(2026, 11, 16));
        await Sync();
        string ics = _srv.Store.Values.Single().Data;
        Assert.Contains("RRULE:FREQ=WEEKLY", ics);
        Assert.Contains("EXDATE:20261116T100000", ics);

        using var other = new DbFixture();
        var acc = new SyncRepository(other.Db, other.Clock).AddAccount(SyncRepository.KindCalDav, "b", _srv.Url, _srv.User, _prot.Protect(_srv.Password), true);
        await new SyncEngine(other.Db, other.Clock).RunAsync(acc, CalDavProvider.Create(acc, _prot));
        Assert.Equal("FREQ=WEEKLY", other.Events.Get(ev.Id)!.RRule);
        Assert.Contains(new DateOnly(2026, 11, 16), other.Events.GetExceptions(ev.Id));
    }

    [Fact]
    public void Skipping_a_day_counts_as_an_edit_so_it_is_noticed_by_the_sync()
    {
        var ev = _f.Events.Add("주간", "2026-11-09T10:00", rrule: "FREQ=WEEKLY");
        long before = _f.Events.Get(ev.Id)!.UpdatedAt;
        _f.Clock.Advance();
        _f.Events.AddException(ev.Id, new DateOnly(2026, 11, 16));
        Assert.True(_f.Events.Get(ev.Id)!.UpdatedAt > before);
    }
}

public class SyncSetupTests : IDisposable
{
    private readonly DbFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Theory]
    [InlineData("https://cal.example.com/dav/me/work/", true)]
    [InlineData("http://localhost:8080/dav/", true)]
    [InlineData("http://127.0.0.1/dav/", true)]
    [InlineData("http://cal.example.com/dav/", false)]          // the password would travel in the clear
    [InlineData("https://user:pw@cal.example.com/", false)]      // a password inside the address ends up in logs and history
    [InlineData("ftp://cal.example.com/", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void Only_safe_addresses_are_accepted(string url, bool ok)
    {
        Assert.Equal(ok, CalDavProvider.TryParseUrl(url, out _, out var error));
        Assert.Equal(!ok, error is not null);
    }

    [Fact]
    public void The_password_is_stored_protected_and_accounts_can_be_changed_and_removed()
    {
        var repo = new SyncRepository(_f.Db, _f.Clock);
        var p = new FakeProtector();
        var a = repo.AddAccount("caldav", " 내 캘린더 ", "https://x.example/dav/", " me ", p.Protect("비밀!"), twoWay: false);
        Assert.Equal(("내 캘린더", "me"), (a.Name, a.Username));
        Assert.DoesNotContain("비밀!", repo.GetAccount(a.Id)!.Secret);
        Assert.Equal("비밀!", p.Unprotect(repo.GetAccount(a.Id)!.Secret));

        repo.UpdateAccount(a.Id, "이름2", "https://y.example/dav/", "me2", protectedSecret: null, twoWay: true, enabled: false);
        var b = repo.GetAccount(a.Id)!;
        Assert.Equal(("이름2", true, false), (b.Name, b.TwoWay, b.Enabled));
        Assert.Equal(a.Secret, b.Secret);                                               // null keeps the stored password

        repo.UpsertLink(new SyncLink(a.Id, "e1", "https://x/a.ics", "u", "\"1\"", 5, false));
        Assert.True(repo.DeleteAccount(a.Id));
        Assert.Empty(repo.GetLinks(a.Id));
        Assert.Null(repo.GetAccount(a.Id));
    }

    [Fact]
    public void A_server_address_belongs_to_one_event_and_a_link_is_replaced_not_duplicated()
    {
        var repo = new SyncRepository(_f.Db, _f.Clock);
        repo.UpsertLink(new SyncLink("a", "e1", "https://x/a.ics", "u1", "\"1\"", 5, false));
        repo.UpsertLink(new SyncLink("a", "e1", "https://x/a.ics", "u1", "\"2\"", 6, false));
        repo.UpsertLink(new SyncLink("a", "e2", "https://x/a.ics", "u2", "\"3\"", 7, true));
        var links = repo.GetLinks("a");
        Assert.Equal("e2", Assert.Single(links).EventId);
    }

    [Fact]
    public void Resource_addresses_that_differ_only_in_encoding_are_the_same_resource()
    {
        Assert.Equal(SyncEngine.KeyOf("https://h/dav/cal/a%40b.ics"), SyncEngine.KeyOf("/dav/cal/a@b.ics"));
        Assert.Equal(SyncEngine.KeyOf("https://h/dav/cal/x.ics"), SyncEngine.KeyOf("https://h/dav/cal/x.ics/"));
    }
}
