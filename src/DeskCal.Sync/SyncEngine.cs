using DeskCal.Core;
using DeskCal.Core.Exchange;
using DeskCal.Data;

namespace DeskCal.Sync;

/// <summary>What one sync run did. <see cref="Notes"/> are things the user may want to know; <see cref="Errors"/> are resources that could not be handled.</summary>
public sealed class SyncReport
{
    public int PulledNew, PulledUpdated, PushedNew, PushedUpdated, DeletedLocal, DeletedRemote, Conflicts, NotSent, Deferred;
    public List<string> Notes { get; } = [];
    public List<string> Errors { get; } = [];

    /// <summary>The calendar on screen may need redrawing, and reminders re-planning.</summary>
    public bool ChangedLocal => PulledNew + PulledUpdated + DeletedLocal + Conflicts > 0;

    public string Summary()
    {
        var parts = new List<string>();
        void Add(string label, int n) { if (n > 0) parts.Add($"{label} {n}"); }
        Add("새로 받음", PulledNew); Add("서버 변경 반영", PulledUpdated); Add("서버에 새로 올림", PushedNew); Add("서버에 수정 반영", PushedUpdated);
        Add("서버 삭제 반영", DeletedLocal); Add("서버에서 삭제", DeletedRemote); Add("충돌", Conflicts); Add("올리지 않은 변경", NotSent); Add("다음으로 미룸", Deferred);
        Add("오류", Errors.Count);
        return parts.Count == 0 ? "변경 없음" : string.Join(" · ", parts);
    }

    internal void Note(string text) { if (Notes.Count < 50) Notes.Add(text); }
    internal void Error(string text) { if (Errors.Count < 50) Errors.Add(text); }
}

/// <summary>
/// Two-way sync of events with a calendar server. For every event/resource pair <see cref="SyncPlanner"/> decides what to do
/// from the versions recorded at the last sync; this class carries the decision out, one resource at a time, so one bad
/// resource never stops the rest. Only events are synced (not day-cell items, countdowns or notes).
/// </summary>
public sealed class SyncEngine(DeskCalDb db, IClock clock, Func<DateOnly, bool>? isDayOff = null)
{
    private readonly EventRepository _events = new(db, clock);
    private readonly ReminderRepository _reminders = new(db, clock);
    private readonly SyncRepository _sync = new(db, clock);
    private readonly ExchangeService _exchange = new(db, clock, isDayOff);

    /// <exception cref="CalendarServerException">The server could not be reached or refused the login.</exception>
    public async Task<SyncReport> RunAsync(SyncAccount account, ICalendarProvider provider, CancellationToken ct = default)
    {
        var report = new SyncReport();
        var remote = await provider.ListAsync(ct);

        var links = _sync.GetLinks(account.Id);
        var linkByKey = new Dictionary<string, SyncLink>();
        foreach (var l in links) linkByKey[KeyOf(l.Href)] = l;
        var seenKeys = new HashSet<string>();
        var handledEvents = new HashSet<string>();
        var run = new Run(account, provider, report, handledEvents);

        foreach (var res in remote)
        {
            ct.ThrowIfCancellationRequested();
            if (!seenKeys.Add(KeyOf(res.Href))) continue;
            try { await HandleRemote(run, res, linkByKey.GetValueOrDefault(KeyOf(res.Href)), ct); }
            catch (PreconditionFailedException) { report.Deferred++; report.Note("서버에서 일정이 먼저 바뀌어 다음 동기화로 미뤘습니다."); }
            catch (Exception ex) when (ex is not OperationCanceledException) { report.Error($"{Short(res.Href)}: {ex.Message}"); }
        }

        foreach (var link in links.Where(l => !seenKeys.Contains(KeyOf(l.Href))))
        {
            ct.ThrowIfCancellationRequested();
            handledEvents.Add(link.EventId);
            try { await HandleMissing(run, link, ct); }
            catch (PreconditionFailedException) { report.Deferred++; }
            catch (Exception ex) when (ex is not OperationCanceledException) { report.Error($"{link.EventId}: {ex.Message}"); }
        }

        if (account.TwoWay)
        {
            var linked = _sync.GetLinks(account.Id).Select(l => l.EventId).ToHashSet();
            foreach (var e in _events.GetAllActive())
            {
                ct.ThrowIfCancellationRequested();
                if (linked.Contains(e.Id) || handledEvents.Contains(e.Id)) continue;
                try { await Push(run, e, IcsExchange.UidFromId(e.Id), null, existing: null, isNew: true, ct); }
                catch (PreconditionFailedException) { report.Deferred++; }
                catch (Exception ex) when (ex is not OperationCanceledException) { report.Error($"{e.Title}: {ex.Message}"); }
            }
        }
        return report;
    }

    private sealed record Run(SyncAccount Account, ICalendarProvider Provider, SyncReport Report, HashSet<string> Handled);

    // ---- a resource the server has ----------------------------------------------------------------

    private async Task HandleRemote(Run run, RemoteResource res, SyncLink? link, CancellationToken ct)
    {
        string? data = res.Data ?? (await run.Provider.GetAsync(res.Href, ct))?.Data;
        if (data is null) return;
        var info = IcsExchange.Describe(data);
        if (info.Uid is null) { run.Report.Note($"UID 가 없는 서버 일정은 건너뜁니다 ({Short(res.Href)})."); return; }

        string id = link?.EventId ?? IcsExchange.IdFromUid(info.Uid);
        if (link is null && !run.Handled.Add(id)) { run.Report.Note("같은 일정이 서버에 두 번 있어 한 번만 가져옵니다."); return; }
        run.Handled.Add(id);
        bool isProtected = !info.OwnedByDeskCal;
        var local = _events.Get(id);

        var state = new SyncState(
            LocalExists: local is not null, LocalDeleted: local?.Deleted == true, LocalStamp: local?.UpdatedAt ?? 0,
            Linked: link is not null, LinkEtag: link?.Etag, LinkStamp: link?.LocalStamp ?? 0, Protected: isProtected,
            RemoteExists: true, RemoteEtag: res.Etag, TwoWay: run.Account.TwoWay);
        var action = SyncPlanner.Decide(state);

        switch (action)
        {
            case SyncAction.None:
                if (local is not null && local.UpdatedAt != (link?.LocalStamp ?? 0) && isProtected && run.Account.TwoWay && link is not null)
                {
                    run.Report.NotSent++;                                    // edited here, but the server copy belongs to another program
                    run.Report.Note($"'{Short(local.Title)}': 다른 프로그램이 만든 일정이라 수정 내용을 서버에 올리지 않았습니다.");
                }
                break;

            case SyncAction.PullNew:
            case SyncAction.PullUpdate:
            case SyncAction.Restore:
            case SyncAction.AdoptAndPull:
            case SyncAction.Conflict:
            {
                var ex = ReadMaster(data, info, run.Report);
                if (ex is null) return;
                if (action == SyncAction.Conflict && local is not null) KeepConflictCopy(local, run.Report);
                bool created = local is null;
                if (created) { _exchange.Apply(new ExchangeBundle([], [ex], [], [])); }
                else { ApplyRemote(local!, ex); }
                StampLink(run, id, res, info.Uid, isProtected);
                if (action == SyncAction.Conflict) run.Report.Conflicts++;
                else if (created) run.Report.PulledNew++;
                else run.Report.PulledUpdated++;
                break;
            }

            case SyncAction.PushUpdate:
            case SyncAction.AdoptAndPush:
                await Push(run, local!, link?.Uid ?? info.Uid, res.Etag, res.Href, isNew: false, ct);
                break;

            case SyncAction.DeleteRemote:
                await run.Provider.DeleteAsync(res.Href, res.Etag, ct);
                _sync.DeleteLink(run.Account.Id, id);
                run.Report.DeletedRemote++;
                break;

            case SyncAction.DropLink:
                _sync.DeleteLink(run.Account.Id, id);
                break;
        }
    }

    // ---- a link whose resource is gone from the server ---------------------------------------------

    private async Task HandleMissing(Run run, SyncLink link, CancellationToken ct)
    {
        var local = _events.Get(link.EventId);
        var state = new SyncState(
            LocalExists: local is not null, LocalDeleted: local?.Deleted == true, LocalStamp: local?.UpdatedAt ?? 0,
            Linked: true, LinkEtag: link.Etag, LinkStamp: link.LocalStamp, Protected: link.Protected,
            RemoteExists: false, RemoteEtag: null, TwoWay: run.Account.TwoWay);

        switch (SyncPlanner.Decide(state))
        {
            case SyncAction.DeleteLocal:
                _events.SoftDelete(link.EventId);
                _reminders.DeleteByOwner(ReminderOwner.Event, link.EventId);
                _sync.DeleteLink(run.Account.Id, link.EventId);
                run.Report.DeletedLocal++;
                break;
            case SyncAction.PushNew:
                await Push(run, local!, link.Uid, null, existing: null, isNew: true, ct);
                break;
            case SyncAction.DropLink:
                _sync.DeleteLink(run.Account.Id, link.EventId);
                break;
        }
    }

    // ---- carrying out -----------------------------------------------------------------------------

    private async Task Push(Run run, EventEntry local, string uid, string? ifMatch, string? existing, bool isNew, CancellationToken ct)
    {
        var ex = _exchange.CollectEvent(local.Id);
        if (ex is null || !EventTime.TryParse(ex.Start, out _, out _)) return;
        string ics = IcsExchange.Write(new ExchangeBundle([], [ex], [], []), DateTimeOffset.FromUnixTimeMilliseconds(clock.NowMs).UtcDateTime,
            isDayOff, uids: new Dictionary<string, string> { [local.Id] = uid });
        string href = existing ?? run.Provider.HrefFor(uid);
        string? etag = await run.Provider.PutAsync(href, ics, isNew ? null : ifMatch, ct);
        _sync.UpsertLink(new SyncLink(run.Account.Id, local.Id, href, uid, etag, local.UpdatedAt, Protected: false));
        if (isNew) run.Report.PushedNew++; else run.Report.PushedUpdated++;
    }

    /// <summary>Overwrite the event with the server's version. A deleted event comes back.</summary>
    private void ApplyRemote(EventEntry current, ExEvent ex)
    {
        _events.Update(current with
        {
            Title = ex.Title, Body = ex.Body, Start = ex.Start, End = ex.End, AllDay = ex.AllDay,
            RRule = ex.RRule, ExtRule = ex.ExtRule, Color = ex.Color ?? current.Color,
        });
        _events.ReplaceExceptions(current.Id, ex.Exceptions.Select(x => CountdownCalc.TryParseDate(x, out var d) ? d : (DateOnly?)null).Where(d => d is not null).Select(d => d!.Value));
        _reminders.ReplaceForOwner(ReminderOwner.Event, current.Id, ex.Reminders.Select(r => (r.OffsetMinutes, r.CustomTime, (string?)null)));
        if (current.Deleted) _events.Undelete(current.Id);
    }

    /// <summary>Both sides changed: the server wins, and what was typed here is kept as a separate event so nothing is lost.</summary>
    private void KeepConflictCopy(EventEntry local, SyncReport report)
    {
        var copy = _events.Add("[충돌] " + local.Title, local.Start, local.End, local.AllDay, local.Tz, local.RRule, local.ExtRule, local.Body, local.Color);
        report.Note($"'{Short(local.Title)}': 여기와 서버에서 모두 바뀌어 서버 내용을 적용하고, 이곳의 내용은 '{Short(copy.Title)}' 로 남겼습니다.");
    }

    private void StampLink(Run run, string id, RemoteResource res, string uid, bool isProtected)
    {
        long stamp = _events.Get(id)?.UpdatedAt ?? 0;
        _sync.UpsertLink(new SyncLink(run.Account.Id, id, res.Href, uid, res.Etag, stamp, isProtected));
    }

    private static ExEvent? ReadMaster(string data, IcsExchange.ResourceInfo info, SyncReport report)
    {
        ExchangeReadResult read;
        try { read = IcsExchange.Read(data); }
        catch (ExchangeFormatException ex) { report.Error(ex.Message); return null; }
        string id = IcsExchange.IdFromUid(info.Uid!);
        var master = read.Bundle.Events.FirstOrDefault(e => e.Id == id);
        foreach (var w in read.Warnings.Take(3)) report.Note(w);
        if (master is null) { report.Note($"읽을 수 없는 일정을 건너뜁니다 ({info.Uid})."); return null; }
        if (info.EventCount > 1) report.Note($"'{Short(master.Title)}': 반복 일정의 개별 수정본 {info.EventCount - 1}건은 가져오지 않습니다.");
        return master;
    }

    /// <summary>The same resource can come back with its address written differently (percent-encoding, trailing slash).</summary>
    internal static string KeyOf(string href)
    {
        try
        {
            var u = new Uri(href, UriKind.RelativeOrAbsolute);
            return Uri.UnescapeDataString(u.IsAbsoluteUri ? u.AbsolutePath : href).TrimEnd('/');
        }
        catch (UriFormatException) { return href; }
    }

    private static string Short(string s)
    {
        s = s.Replace('\n', ' ');
        return s.Ellipsize(40);
    }
}
