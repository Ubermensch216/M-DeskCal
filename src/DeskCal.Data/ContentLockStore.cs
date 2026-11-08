using DeskCal.Core;

namespace DeskCal.Data;

/// <summary>Where the content-lock password hash and the "currently hidden" flag live (settings table, "lock.*").</summary>
public sealed class ContentLockStore(SettingsStore store)
{
    private const string HashKey = "lock.hash", HiddenKey = "lock.hidden";

    public bool HasPassword => !string.IsNullOrEmpty(store.GetString(HashKey));

    /// <summary>Whether the content was hidden when the app last ran; restored at start so a restart is not a way around the lock.</summary>
    public bool Hidden
    {
        get => store.GetBool(HiddenKey, false);
        set => store.SetBool(HiddenKey, value);
    }

    /// <exception cref="ArgumentException">The password breaks <see cref="ContentLock.Validate"/>.</exception>
    public void SetPassword(string password)
    {
        if (ContentLock.Validate(password) is { } problem) throw new ArgumentException(problem, nameof(password));
        store.SetString(HashKey, ContentLock.Hash(password));
    }

    /// <summary>With no password set, nothing can be verified: the caller decides whether that means "open".</summary>
    public bool Verify(string? password) => ContentLock.Verify(password, store.GetString(HashKey));

    public void ClearPassword() => store.Remove(HashKey);
}
