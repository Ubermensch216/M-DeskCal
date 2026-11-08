using System.Globalization;
using System.Text.Json;
using DeskCal.Core.Calendars;

namespace DeskCal.Data;

/// <summary>
/// The user-editable holiday corrections file (holidays.json). Temporary holidays and election days are announced
/// shortly before they happen and cannot be computed, so users can add them - or remove a computed day - here.
/// Entries: { "date": "2026-10-10", "name": "임시공휴일", "dayOff": true } or { "date": "2026-06-03", "remove": true }.
/// </summary>
public static class HolidayOverrideFile
{
    public sealed record Result(IReadOnlyList<HolidayOverride> Overrides, IReadOnlyList<string> Errors);

    private const string Template = """
        // M-DeskCal holiday corrections. Edit, save, then choose "휴일 데이터 다시 읽기" in the tray menu.
        // Add a day:     { "date": "2026-10-10", "name": "임시공휴일", "dayOff": true }
        // Not a day off: { "date": "2026-04-01", "name": "회사 창립일", "dayOff": false }
        // Remove a day:  { "date": "2026-06-03", "remove": true }
        [
        ]
        """;

    /// <summary>Create the file with usage notes if it does not exist yet.</summary>
    public static void EnsureTemplate(string path)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Template + Environment.NewLine);
    }

    /// <summary>Read the file. A missing file is fine; bad entries are skipped and reported, never thrown.</summary>
    public static Result Load(string path)
    {
        if (!File.Exists(path)) return new([], []);
        var list = new List<HolidayOverride>();
        var errors = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new([], ["최상위 값은 배열([ ... ])이어야 합니다."]);

            int n = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                n++;
                if (e.ValueKind != JsonValueKind.Object) { errors.Add($"{n}번째 항목: 객체가 아닙니다."); continue; }
                if (!e.TryGetProperty("date", out var dateProp) || dateProp.ValueKind != JsonValueKind.String ||
                    !DateOnly.TryParseExact(dateProp.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                { errors.Add($"{n}번째 항목: date 는 \"yyyy-MM-dd\" 형식이어야 합니다."); continue; }

                bool remove = e.TryGetProperty("remove", out var r) && r.ValueKind == JsonValueKind.True;
                if (remove) { list.Add(new HolidayOverride(date, "", true, Remove: true)); continue; }

                string? name = e.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) { errors.Add($"{n}번째 항목({date:yyyy-MM-dd}): name 이 필요합니다."); continue; }
                bool dayOff = !(e.TryGetProperty("dayOff", out var d) && d.ValueKind == JsonValueKind.False);
                list.Add(new HolidayOverride(date, name.Trim(), dayOff));
            }
        }
        catch (JsonException ex) { return new([], [$"JSON 형식 오류: {ex.Message}"]); }
        catch (IOException ex) { return new([], [$"파일을 읽을 수 없습니다: {ex.Message}"]); }
        return new(list, errors);
    }
}
