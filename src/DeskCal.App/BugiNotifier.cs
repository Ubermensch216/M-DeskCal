using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.App;

/// <summary>
/// Forwards fired calendar reminders to a running <c>deskPet_Bugi</c> instance via its local HTTP API.
/// Failures (e.g. Bugi is not running) are logged and ignored so calendar reminders are never blocked.
/// </summary>
internal static class BugiNotifier
{
    private const int DefaultPort = 19480;
    private const int MaxLines = 4;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1500) };

    internal static string DefaultDiscoveryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "부기", "부기_api.json");

    /// <summary>Reads the active local API base URL from <c>부기_api.json</c>, falling back to <c>http://127.0.0.1:19480</c>.</summary>
    internal static string ResolveBaseUrl(string? discoveryPath = null)
    {
        string path = discoveryPath ?? DefaultDiscoveryPath;
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                var root = doc.RootElement;
                if (root.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                {
                    string? url = urlProp.GetString()?.Trim().TrimEnd('/');
                    if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        return url;
                }
                if (root.TryGetProperty("port", out var portProp) && portProp.TryGetInt32(out int port) &&
                    port is >= 1024 and <= 65535)
                {
                    return $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("bugi discovery read failed: " + ex.Message);
        }
        return $"http://127.0.0.1:{DefaultPort.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Formats reminder messages into a compact multiline text for Bugi's alert bubble.</summary>
    internal static string BuildMessage(IReadOnlyList<ReminderMessage> messages)
    {
        if (messages.Count == 0) return "";
        if (messages.Count == 1)
        {
            var m = messages[0];
            return $"📅 {FormatWhen(m)} {m.Title}\n{FormatTiming(m)}";
        }

        var sb = new StringBuilder();
        int shown = Math.Min(MaxLines, messages.Count);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append('\n');
            var m = messages[i];
            sb.Append($"• {FormatWhen(m)} {m.Title} ({FormatTiming(m)})");
        }
        if (messages.Count > shown)
            sb.Append($"\n… 외 {messages.Count - shown}건");
        return sb.ToString();
    }

    /// <summary>Fire-and-forget dispatch on a background thread so UI responsiveness is unaffected.</summary>
    internal static void NotifyInBackground(IReadOnlyList<ReminderMessage> messages)
    {
        if (messages.Count == 0) return;
        var snapshot = messages.ToArray();
        _ = Task.Run(() => SendAsync(snapshot));
    }

    internal static async Task<bool> SendAsync(IReadOnlyList<ReminderMessage> messages, HttpMessageInvoker? client = null, string? discoveryPath = null)
    {
        string text = BuildMessage(messages);
        if (string.IsNullOrWhiteSpace(text)) return false;

        string endpoint = ResolveBaseUrl(discoveryPath) + "/api/alert";
        string json = JsonSerializer.Serialize(new { message = text, style = "big" });
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        try
        {
            using var resp = await (client ?? Http).SendAsync(req, CancellationToken.None).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            AppLog.Write("bugi alert send failed: " + ex.Message);
            return false;
        }
    }

    private static string FormatWhen(ReminderMessage m) =>
        m.Occurrence.ToString("M/d", CultureInfo.InvariantCulture) + (m.StartTime is { } t ? $" {t:HH:mm}" : " 종일");

    private static string FormatTiming(ReminderMessage m)
    {
        string timing = m.OffsetMinutes <= 0 ? "지금 시작" : ReminderText.DescribeOffset(m.OffsetMinutes);
        if (m.OwnerType == ReminderOwner.Item) timing = "할 일 · " + timing;
        if (m.Late) timing += " · 놓친 알림";
        return timing;
    }
}
