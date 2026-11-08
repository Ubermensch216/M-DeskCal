using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskCal.Core.Exchange;

/// <summary>
/// The full-fidelity format ("backup"): everything this program stores about items, events (with repeat rules, skipped
/// dates and reminders), countdowns and notes. Plain readable JSON; unknown fields are ignored, so newer files with
/// extra data still load.
/// </summary>
public static class JsonExchange
{
    public const string FormatName = "M-DeskCal";
    public const int Version = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep Korean text readable in the file
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Doc(
        string? Format, int Version, string? ExportedAt,
        List<ExItem>? Items, List<ExEvent>? Events, List<Countdown>? Countdowns, List<ExNote>? Notes);

    public static string Write(ExchangeBundle bundle, DateTimeOffset exportedAt) =>
        JsonSerializer.Serialize(new Doc(FormatName, Version, exportedAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            [.. bundle.Items], [.. bundle.Events], [.. bundle.Countdowns], [.. bundle.Notes]), Options);

    /// <exception cref="ExchangeFormatException">Not JSON, not this program's file, or from a newer version.</exception>
    public static ExchangeReadResult Read(string text)
    {
        Doc? doc;
        try { doc = JsonSerializer.Deserialize<Doc>(text, Options); }
        catch (JsonException ex) { throw new ExchangeFormatException("JSON 파일을 읽을 수 없습니다: " + ex.Message); }

        if (doc is null || !string.Equals(doc.Format, FormatName, StringComparison.Ordinal))
            throw new ExchangeFormatException("M-DeskCal 백업 파일이 아닙니다.");
        if (doc.Version > Version)
            throw new ExchangeFormatException($"더 새로운 버전(v{doc.Version})으로 만든 파일입니다. 프로그램을 업데이트한 뒤 다시 시도하세요.");

        return ExchangeCleaner.Clean(new ExchangeBundle(doc.Items ?? [], doc.Events ?? [], doc.Countdowns ?? [], doc.Notes ?? []));
    }
}
