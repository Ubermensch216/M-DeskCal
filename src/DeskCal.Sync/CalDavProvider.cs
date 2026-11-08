using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using DeskCal.Data;

namespace DeskCal.Sync;

/// <summary>One calendar object on the server: its address, its version tag and (when the server sent it) the iCalendar text.</summary>
public sealed record RemoteResource(string Href, string Etag, string? Data);

/// <summary>A problem talking to the calendar server. The message is meant for the user.</summary>
public class CalendarServerException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}

/// <summary>The server refused a change because the resource is no longer the version we knew (HTTP 412).</summary>
public sealed class PreconditionFailedException(string message) : CalendarServerException(message, 412);

/// <summary>What the sync engine needs from a calendar server, so another protocol can be added without touching the engine.</summary>
public interface ICalendarProvider
{
    /// <summary>Every event in the calendar, with its version tag.</summary>
    Task<IReadOnlyList<RemoteResource>> ListAsync(CancellationToken ct);

    Task<RemoteResource?> GetAsync(string href, CancellationToken ct);

    /// <summary>Create (<paramref name="ifMatch"/> null) or replace (only if the server still has that version). Returns the new version tag when the server says it.</summary>
    /// <exception cref="PreconditionFailedException">The server copy changed or already exists.</exception>
    Task<string?> PutAsync(string href, string ics, string? ifMatch, CancellationToken ct);

    /// <summary>Delete. A resource that is already gone is not an error.</summary>
    /// <exception cref="PreconditionFailedException">The server copy changed.</exception>
    Task DeleteAsync(string href, string? ifMatch, CancellationToken ct);

    /// <summary>The address for a new resource with this UID.</summary>
    string HrefFor(string uid);
}

/// <summary>
/// CalDAV (RFC 4791) over HTTPS with a user name and password: a single calendar collection whose address the user gives
/// (Nextcloud, Synology, Radicale, Baikal, iCloud with an app-specific password, ...). Credentials are sent only over HTTPS
/// (plain HTTP is accepted for this computer only), redirects are not followed, so the password never goes to another host.
/// </summary>
public sealed class CalDavProvider : ICalendarProvider
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private const long MaxResponseBytes = 64L * 1024 * 1024;
    private const int MaxResources = 50_000;

    private readonly HttpClient _http;
    private readonly Uri _collection;
    private readonly string _auth;

    public CalDavProvider(HttpClient http, Uri collection, string username, string password)
    {
        _http = http;
        string s = collection.AbsoluteUri;
        _collection = new Uri(s.EndsWith('/') ? s : s + "/");
        _auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
    }

    /// <summary>An HttpClient that never follows redirects and gives up after 30 seconds.</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(15) };
        var c = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("M-DeskCal-Sync/1.0");
        return c;
    }

    public static CalDavProvider Create(SyncAccount account, ISecretProtector protector, HttpMessageHandler? handler = null)
    {
        if (!TryParseUrl(account.Url, out var uri, out var error)) throw new CalendarServerException(error!);
        string password = protector.Unprotect(account.Secret)
            ?? throw new CalendarServerException("저장된 비밀번호를 읽을 수 없습니다 (다른 Windows 계정이거나 손상됨). 계정 설정에서 비밀번호를 다시 입력하세요.");
        return new CalDavProvider(CreateClient(handler), uri!, account.Username, password);
    }

    /// <summary>The address must be HTTPS (or http on this computer), without a built-in password.</summary>
    public static bool TryParseUrl(string? text, out Uri? uri, out string? error)
    {
        uri = null; error = null;
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var u)) { error = "주소 형식이 올바르지 않습니다. 예: https://example.com/dav/calendars/me/work/"; return false; }
        if (!string.IsNullOrEmpty(u.UserInfo)) { error = "주소에 사용자 이름·비밀번호를 넣지 마세요. 아래 칸에 따로 입력합니다."; return false; }
        bool loopback = u.IsLoopback;
        if (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && loopback)) { uri = u; return true; }
        error = "비밀번호를 안전하게 보내려면 주소가 https:// 로 시작해야 합니다.";
        return false;
    }

    public string HrefFor(string uid) => new Uri(_collection, Uri.EscapeDataString(uid) + ".ics").AbsoluteUri;

    // ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<RemoteResource>> ListAsync(CancellationToken ct)
    {
        const string body =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><d:getetag/><c:calendar-data/></d:prop>
              <c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT"/></c:comp-filter></c:filter>
            </c:calendar-query>
            """;
        using var req = Request(new HttpMethod("REPORT"), _collection);
        req.Headers.Add("Depth", "1");
        req.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        var text = await SendAsync(req, ct, 207, 200);
        return ParseMultistatus(text);
    }

    public async Task<RemoteResource?> GetAsync(string href, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, Resolve(href));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).WrapNetwork();
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        Check(resp, 200);
        string data = await ReadLimited(resp, ct);
        return new RemoteResource(Resolve(href).AbsoluteUri, resp.Headers.ETag?.ToString() ?? "", data);
    }

    public async Task<string?> PutAsync(string href, string ics, string? ifMatch, CancellationToken ct)
    {
        var target = Resolve(href);
        using var req = Request(HttpMethod.Put, target);
        if (ifMatch is null) req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        else req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        req.Content = new StringContent(ics, new UTF8Encoding(false), "text/calendar");
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/calendar") { CharSet = "utf-8" };
        using var resp = await _http.SendAsync(req, ct).WrapNetwork();
        if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new PreconditionFailedException("서버의 일정이 먼저 바뀌었습니다.");
        Check(resp, 200, 201, 204);
        if (resp.Headers.ETag is { } tag) return tag.ToString();
        var again = await GetAsync(href, ct);                       // some servers do not send the tag on PUT: ask for it
        return string.IsNullOrEmpty(again?.Etag) ? null : again!.Etag;
    }

    public async Task DeleteAsync(string href, string? ifMatch, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Delete, Resolve(href));
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var resp = await _http.SendAsync(req, ct).WrapNetwork();
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (resp.StatusCode == HttpStatusCode.PreconditionFailed) throw new PreconditionFailedException("서버의 일정이 먼저 바뀌었습니다.");
        Check(resp, 200, 202, 204);
    }

    // ---------------------------------------------------------------------------------------------

    private Uri Resolve(string href)
    {
        var u = new Uri(_collection, href);
        // never send the password to anything outside the calendar the user named
        if (!string.Equals(u.Authority, _collection.Authority, StringComparison.OrdinalIgnoreCase) || u.Scheme != _collection.Scheme)
            throw new CalendarServerException("서버가 다른 주소의 일정을 가리켜 무시했습니다.");
        return u;
    }

    private HttpRequestMessage Request(HttpMethod method, Uri uri)
    {
        var req = new HttpRequestMessage(method, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _auth);
        return req;
    }

    private async Task<string> SendAsync(HttpRequestMessage req, CancellationToken ct, params int[] ok)
    {
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).WrapNetwork();
        Check(resp, ok);
        return await ReadLimited(resp, ct);
    }

    private static async Task<string> ReadLimited(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength > MaxResponseBytes) throw new CalendarServerException("서버 응답이 너무 큽니다.");
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxResponseBytes) throw new CalendarServerException("서버 응답이 너무 큽니다.");
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Check(HttpResponseMessage resp, params int[] ok)
    {
        int code = (int)resp.StatusCode;
        if (ok.Contains(code)) return;
        throw code switch
        {
            401 => new CalendarServerException("사용자 이름 또는 비밀번호가 올바르지 않습니다. (iCloud·Google 등은 앱 전용 비밀번호가 필요합니다.)", code),
            403 => new CalendarServerException("이 캘린더에 접근할 권한이 없습니다.", code),
            404 => new CalendarServerException("캘린더 주소를 찾을 수 없습니다. 주소를 확인하세요.", code),
            >= 300 and < 400 => new CalendarServerException("서버가 다른 주소로 이동시켰습니다. 새 주소: " + (resp.Headers.Location?.ToString() ?? "(알 수 없음)"), code),
            507 => new CalendarServerException("서버의 저장 공간이 부족합니다.", code),
            _ => new CalendarServerException($"서버가 요청을 처리하지 못했습니다 (HTTP {code}).", code),
        };
    }

    private IReadOnlyList<RemoteResource> ParseMultistatus(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }                       // DTDs are prohibited by default: no entity-expansion tricks
        catch (System.Xml.XmlException) { throw new CalendarServerException("서버가 CalDAV 응답이 아닌 내용을 보냈습니다. 주소가 캘린더 주소인지 확인하세요."); }

        var list = new List<RemoteResource>();
        foreach (var r in doc.Descendants(D + "response"))
        {
            if (list.Count >= MaxResources) throw new CalendarServerException("일정이 너무 많습니다.");
            string? href = r.Element(D + "href")?.Value.Trim();
            if (string.IsNullOrEmpty(href)) continue;
            var ok = r.Elements(D + "propstat").FirstOrDefault(p => (p.Element(D + "status")?.Value ?? "").Contains(" 200"));
            if (ok is null) continue;
            var prop = ok.Element(D + "prop");
            string etag = prop?.Element(D + "getetag")?.Value.Trim() ?? "";
            string? data = prop?.Element(C + "calendar-data")?.Value;
            Uri abs;
            try { abs = Resolve(href); } catch (CalendarServerException) { continue; }
            if (abs.AbsolutePath.TrimEnd('/') == _collection.AbsolutePath.TrimEnd('/')) continue;   // the collection itself
            if (!abs.AbsolutePath.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(data)) continue;
            list.Add(new RemoteResource(abs.AbsoluteUri, etag, string.IsNullOrWhiteSpace(data) ? null : data));
        }
        return list;
    }
}

internal static class HttpExtensions
{
    /// <summary>Turns connection failures into a message the user can act on.</summary>
    public static async Task<HttpResponseMessage> WrapNetwork(this Task<HttpResponseMessage> task)
    {
        try { return await task; }
        catch (HttpRequestException ex) { throw new CalendarServerException("서버에 연결할 수 없습니다: " + (ex.InnerException?.Message ?? ex.Message)); }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException) { throw new CalendarServerException("서버가 응답하지 않습니다 (시간 초과)."); }
    }
}
