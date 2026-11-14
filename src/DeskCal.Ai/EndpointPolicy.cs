namespace DeskCal.Ai;

/// <summary>
/// Which addresses the app may send calendar text to. By default only this computer (127.0.0.1, ::1, localhost);
/// anything else needs the user's explicit switch and https. Same spirit as the CalDAV rules: no built-in passwords,
/// no query or fragment, and (in the HTTP layer) no following of redirects.
/// </summary>
public static class EndpointPolicy
{
    public static bool TryParse(string? text, bool allowRemote, out Uri? uri, out string? error)
    {
        uri = null; error = null;
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var u))
        {
            error = "주소 형식이 올바르지 않습니다. 예: http://127.0.0.1:11434";
            return false;
        }
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) { error = "주소는 http:// 또는 https:// 로 시작해야 합니다."; return false; }
        if (!string.IsNullOrEmpty(u.UserInfo)) { error = "주소에 사용자 이름·비밀번호를 넣을 수 없습니다."; return false; }
        if (!string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) { error = "주소에는 ? 나 # 부분을 넣을 수 없습니다."; return false; }

        if (!u.IsLoopback)
        {
            if (!allowRemote)
            {
                error = "이 컴퓨터가 아닌 주소입니다. 달력 내용이 그 서버로 전송되므로 기본으로는 막혀 있습니다 (127.0.0.1 또는 localhost 를 쓰세요).";
                return false;
            }
            if (u.Scheme != Uri.UriSchemeHttps) { error = "다른 컴퓨터로 보내려면 https:// 주소여야 합니다."; return false; }
        }
        uri = new UriBuilder(u) { Path = u.AbsolutePath.TrimEnd('/') + "/" }.Uri;
        return true;
    }

    /// <summary>True when the text would be sent off this computer.</summary>
    public static bool DataLeavesPc(Uri uri) => !uri.IsLoopback;
}

/// <summary>Whether an AI feature may run right now: it must be switched on and the calendar must not be locked.</summary>
public sealed class AiGate(Data.SettingsStore settings)
{
    private readonly AiSettingsStore _store = new(settings);
    private readonly Data.ContentLockStore _lock = new(settings);

    /// <summary>Null when allowed, otherwise the reason (for the user).</summary>
    public string? Refusal()
    {
        if (_lock.Hidden) return "달력이 잠겨 있어 AI 기능을 쓸 수 없습니다. 잠금을 먼저 푸세요.";
        return _store.Load().Enabled ? null : "AI 기능이 꺼져 있습니다. 설정 > AI 에서 켜세요.";
    }

    /// <summary>True when the content lock is off (the rule-based parser needs no AI switch, only an unlocked calendar).</summary>
    public bool Unlocked => !_lock.Hidden;
}
