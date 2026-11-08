using DeskCal.Core.Calendars;
using DeskCal.Data;

namespace DeskCal.Tests;

public class HolidayFileTests
{
    private static string Write(DbFixture f, string json)
    {
        var path = f.TempFile("holidays.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Missing_file_is_not_an_error()
    {
        using var f = new DbFixture();
        var r = HolidayOverrideFile.Load(f.TempFile("nope.json"));
        Assert.Empty(r.Overrides); Assert.Empty(r.Errors);
    }

    [Fact]
    public void Template_is_valid_empty_and_never_overwrites_the_users_file()
    {
        using var f = new DbFixture();
        var path = f.TempFile("sub/holidays.json");
        HolidayOverrideFile.EnsureTemplate(path);
        Assert.True(File.Exists(path));
        var r = HolidayOverrideFile.Load(path);
        Assert.Empty(r.Overrides); Assert.Empty(r.Errors);          // comments in the template are tolerated

        File.WriteAllText(path, """[ { "date": "2026-10-10", "name": "x" } ]""");
        HolidayOverrideFile.EnsureTemplate(path);
        Assert.Single(HolidayOverrideFile.Load(path).Overrides);
    }

    [Fact]
    public void Valid_entries_are_read_with_sensible_defaults()
    {
        using var f = new DbFixture();
        var r = HolidayOverrideFile.Load(Write(f, """
            [
              { "date": "2026-10-10", "name": "임시공휴일" },
              { "date": "2026-04-01", "name": "회사 창립일", "dayOff": false },
              { "date": "2026-06-03", "remove": true },   // trailing comma + comment are fine
            ]
            """));
        Assert.Empty(r.Errors);
        Assert.Equal(
            [new HolidayOverride(new(2026, 10, 10), "임시공휴일", true),
             new HolidayOverride(new(2026, 4, 1), "회사 창립일", false),
             new HolidayOverride(new(2026, 6, 3), "", true, Remove: true)], r.Overrides);
    }

    [Fact]
    public void Bad_entries_are_skipped_and_reported_but_good_ones_still_load()
    {
        using var f = new DbFixture();
        var r = HolidayOverrideFile.Load(Write(f, """
            [
              { "date": "2026-13-40", "name": "bad date" },
              { "date": "2026-10-10" },
              "not an object",
              { "name": "no date" },
              { "date": "2026-10-11", "name": "  ok  " }
            ]
            """));
        Assert.Equal(4, r.Errors.Count);
        Assert.Equal([new HolidayOverride(new(2026, 10, 11), "ok", true)], r.Overrides);
    }

    [Theory]
    [InlineData("{ \"date\": \"2026-10-10\" }")]     // root is not an array
    [InlineData("[ { ")]                               // truncated
    [InlineData("")]                                   // empty file
    public void Malformed_files_report_an_error_and_never_throw(string content)
    {
        using var f = new DbFixture();
        var r = HolidayOverrideFile.Load(Write(f, content));
        Assert.Empty(r.Overrides);
        Assert.Single(r.Errors);
    }

    [Fact]
    public void Loaded_overrides_change_the_calendar()
    {
        using var f = new DbFixture();
        var r = HolidayOverrideFile.Load(Write(f, """[ {"date":"2026-10-10","name":"임시공휴일"}, {"date":"2026-06-03","remove":true} ]"""));
        var cal = new KoreanDays(r.Overrides);
        Assert.True(cal.Get(new DateOnly(2026, 10, 10)).IsDayOff);
        Assert.False(cal.Get(new DateOnly(2026, 6, 3)).IsDayOff);
    }
}
