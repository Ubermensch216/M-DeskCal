namespace DeskCal.Core;

/// <summary>What the sync engine should do with one event / server resource pair.</summary>
public enum SyncAction
{
    /// <summary>Nothing to do (already equal, or a change that is deliberately left alone).</summary>
    None,
    /// <summary>The server has it and this program does not: create the event here.</summary>
    PullNew,
    /// <summary>Only the server changed since the last sync: update the event here.</summary>
    PullUpdate,
    /// <summary>Only the server changed, but the event was deleted here: bring it back.</summary>
    Restore,
    /// <summary>Only this program has it: create the resource on the server.</summary>
    PushNew,
    /// <summary>Only this program changed: replace the server copy.</summary>
    PushUpdate,
    /// <summary>The event was deleted here: delete it on the server.</summary>
    DeleteRemote,
    /// <summary>The server deleted it and it is unchanged here: delete it here too.</summary>
    DeleteLocal,
    /// <summary>Both sides changed. The server version wins; the local version is kept as a separate copy.</summary>
    Conflict,
    /// <summary>The pair no longer exists on either side (or must be forgotten): remove the bookkeeping.</summary>
    DropLink,
    /// <summary>The same event exists on both sides but was never linked: link it and send this program's version.</summary>
    AdoptAndPush,
    /// <summary>The same event exists on both sides but was never linked (one-way account): link it and take the server's version.</summary>
    AdoptAndPull,
}

/// <param name="LocalExists">The event exists in the database (deleted or not).</param>
/// <param name="LocalDeleted">The event is marked deleted.</param>
/// <param name="LocalStamp">The event's <c>updated_at</c> now.</param>
/// <param name="Linked">A link (a previous successful sync) exists.</param>
/// <param name="LinkEtag">The server version recorded at the last sync.</param>
/// <param name="LinkStamp">The event's <c>updated_at</c> recorded at the last sync.</param>
/// <param name="Protected">The server copy was made by another program: this program never overwrites or deletes it.</param>
/// <param name="RemoteExists">The server lists the resource.</param>
/// <param name="RemoteEtag">The server version now.</param>
/// <param name="TwoWay">Local changes may be sent to the server.</param>
public readonly record struct SyncState(
    bool LocalExists, bool LocalDeleted, long LocalStamp,
    bool Linked, string? LinkEtag, long LinkStamp, bool Protected,
    bool RemoteExists, string? RemoteEtag, bool TwoWay);

/// <summary>
/// The decision table of the calendar sync, kept free of I/O so every combination can be tested. Changes are detected by
/// comparing against what was recorded at the last successful sync (the server's ETag and this program's <c>updated_at</c>),
/// never by comparing contents. Nothing is ever lost silently: a delete only wins over an unchanged copy, a clash keeps both
/// versions, and a one-way account or a server copy made by another program is never written to.
/// </summary>
public static class SyncPlanner
{
    public static SyncAction Decide(in SyncState s)
    {
        bool localLive = s.LocalExists && !s.LocalDeleted;

        if (!s.Linked)
        {
            if (s.RemoteExists)
            {
                if (!s.LocalExists) return SyncAction.PullNew;
                if (s.LocalDeleted) return SyncAction.Restore;                  // no baseline to say it was deleted on purpose: the server's copy wins
                return s.TwoWay && !s.Protected ? SyncAction.AdoptAndPush : SyncAction.AdoptAndPull;
            }
            return localLive && s.TwoWay ? SyncAction.PushNew : SyncAction.None;
        }

        bool localChanged = !s.LocalExists || s.LocalDeleted || s.LocalStamp != s.LinkStamp;

        if (!s.RemoteExists)
        {
            if (!localLive) return SyncAction.DropLink;                         // gone on both sides
            if (!localChanged) return SyncAction.DeleteLocal;                   // the server deleted it, nothing here to lose
            return s.TwoWay ? SyncAction.PushNew : SyncAction.DropLink;         // edited here: keep it (and put it back on the server if allowed)
        }

        bool remoteChanged = !string.Equals(s.RemoteEtag, s.LinkEtag, StringComparison.Ordinal);

        if (!s.LocalExists) return remoteChanged ? SyncAction.PullNew : SyncAction.DropLink;   // row vanished (should not happen): rebuild or forget

        if (s.LocalDeleted)
        {
            if (remoteChanged) return SyncAction.Restore;                       // somebody edited it elsewhere: do not throw that away
            return s.TwoWay && !s.Protected ? SyncAction.DeleteRemote : SyncAction.None;
        }

        if (!localChanged) return remoteChanged ? SyncAction.PullUpdate : SyncAction.None;
        if (!remoteChanged) return s.TwoWay && !s.Protected ? SyncAction.PushUpdate : SyncAction.None;
        return SyncAction.Conflict;
    }
}
