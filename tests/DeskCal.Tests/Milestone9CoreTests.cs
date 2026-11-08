using System.IO.Compression;
using System.Xml.Linq;
using DeskCal.Core;
using DeskCal.Core.Exchange;

namespace DeskCal.Tests;

public class TimetableTests
{
    [Fact]
    public void Default_day_has_a_lunch_break_after_period_four()
    {
        var s = TimetableSchedule.Slots(new TimetableConfig());
        Assert.Equal(7, s.Count);
        Assert.Equal((new TimeOnly(9, 0), new TimeOnly(9, 50)), (s[0].Start, s[0].End));
        Assert.Equal(new TimeOnly(10, 0), s[1].Start);                                             // ten-minute break after the 1st
        Assert.Equal((new TimeOnly(12, 0), new TimeOnly(12, 50)), (s[3].Start, s[3].End));        // 4th period
        Assert.Equal(new TimeOnly(13, 50), s[4].Start);                                            // after the 60-minute lunch
        Assert.Equal(new TimeOnly(16, 40), s[6].End);
    }

    [Fact]
    public void No_lunch_break_when_lunch_after_is_zero()
    {
        var s = TimetableSchedule.Slots(new TimetableConfig { Periods = 3, LunchAfter = 0, BreakMinutes = 5, ClassMinutes = 45, FirstStart = new TimeOnly(8, 30) });
        Assert.Equal([new TimeOnly(8, 30), new TimeOnly(9, 20), new TimeOnly(10, 10)], s.Select(x => x.Start));
        Assert.Equal(new TimeOnly(10, 55), s[2].End);
    }

    [Fact]
    public void Current_and_next_period()
    {
        var c = new TimetableConfig();
        Assert.Null(TimetableSchedule.Current(c, new TimeOnly(8, 0)));
        Assert.Equal(1, TimetableSchedule.Current(c, new TimeOnly(9, 0)));
        Assert.Equal(1, TimetableSchedule.Current(c, new TimeOnly(9, 49)));
        Assert.Null(TimetableSchedule.Current(c, new TimeOnly(9, 50)));          // end is exclusive: break
        Assert.Equal(2, TimetableSchedule.NextOrCurrent(c, new TimeOnly(9, 55)));
        Assert.Equal(4, TimetableSchedule.NextOrCurrent(c, new TimeOnly(12, 30)));   // still in the 4th
        Assert.Equal(5, TimetableSchedule.NextOrCurrent(c, new TimeOnly(13, 0)));    // lunch: the 5th is next
        Assert.Null(TimetableSchedule.NextOrCurrent(c, new TimeOnly(23, 0)));
    }

    [Fact]
    public void Settings_are_clamped_and_a_day_cannot_run_past_midnight()
    {
        var c = new TimetableConfig { Periods = 99, ClassMinutes = 1, BreakMinutes = -5, LunchAfter = 50 }.Sanitized();
        Assert.Equal((12, 10, 0, 11), (c.Periods, c.ClassMinutes, c.BreakMinutes, c.LunchAfter));
        var late = TimetableSchedule.Slots(new TimetableConfig { FirstStart = new TimeOnly(22, 30), ClassMinutes = 60, Periods = 5, BreakMinutes = 0 });
        Assert.Single(late);                                                         // 22:30-23:30; the next would pass midnight
    }

    [Fact]
    public void Weekend_columns_only_when_asked()
    {
        Assert.Equal(5, TimetableSchedule.Days(new TimetableConfig()).Length);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday],
            TimetableSchedule.Days(new TimetableConfig { ShowWeekend = true }));
    }
}

public class SkinAndLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskcal-skin-" + Guid.NewGuid().ToString("N"));
    public SkinAndLockTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Folder_listing_keeps_only_pictures_sorted_by_name()
    {
        foreach (var n in new[] { "b.JPG", "a.png", "c.txt", "d.webp", "e.bmp" }) File.WriteAllText(Path.Combine(_dir, n), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "sub.png"));                       // a folder named like a picture
        Assert.Equal(["a.png", "b.JPG", "e.bmp"], SkinPicker.List(_dir).Select(Path.GetFileName));
        Assert.Empty(SkinPicker.List(Path.Combine(_dir, "missing")));
        Assert.Empty(SkinPicker.List(""));
        Assert.Empty(SkinPicker.List(null));
        Assert.Single(SkinPicker.List(_dir, max: 1));
    }

    [Fact]
    public void Rotation_advances_with_wall_clock_time_and_wraps()
    {
        var t0 = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(-1, SkinPicker.Pick(0, t0, 5));
        Assert.Equal(0, SkinPicker.Pick(4, t0, 0));
        Assert.Equal(0, SkinPicker.Pick(4, t0.AddDays(100), 0));                       // no rotation: always the first
        int a = SkinPicker.Pick(4, t0, 30);
        Assert.Equal(a, SkinPicker.Pick(4, t0.AddMinutes(29), 30));                    // same slot
        Assert.Equal((a + 1) % 4, SkinPicker.Pick(4, t0.AddMinutes(30), 30));
        Assert.Equal(a, SkinPicker.Pick(4, t0.AddMinutes(30 * 4), 30));                // full cycle
    }

    [Fact]
    public void Skin_settings_are_sanitised()
    {
        var s = new BoardSettings { SkinRotateMinutes = 2, SkinFit = (SkinFit)99, SkinFolder = null! }.Sanitized();
        Assert.Equal((5, SkinFit.Fill, ""), (s.SkinRotateMinutes, s.SkinFit, s.SkinFolder));
        Assert.Equal(0, new BoardSettings { SkinRotateMinutes = -3 }.Sanitized().SkinRotateMinutes);
        Assert.Equal(1440, new BoardSettings { SkinRotateMinutes = 99999 }.Sanitized().SkinRotateMinutes);
    }

    [Fact]
    public void Password_hash_verifies_only_the_right_password()
    {
        var h = ContentLock.Hash("비밀번호1234");
        Assert.StartsWith("pbkdf2-sha256$200000$", h);
        Assert.True(ContentLock.Verify("비밀번호1234", h));
        Assert.False(ContentLock.Verify("비밀번호1235", h));
        Assert.False(ContentLock.Verify("", h));
        Assert.NotEqual(h, ContentLock.Hash("비밀번호1234"));                           // random salt
        Assert.DoesNotContain("비밀번호", h);
    }

    [Fact]
    public void Composed_and_decomposed_hangul_are_the_same_password()
    {
        string nfc = "한글암호";
        var h = ContentLock.Hash(nfc);
        Assert.True(ContentLock.Verify(nfc.Normalize(System.Text.NormalizationForm.FormD), h));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("pbkdf2-sha256$200000$@@@$@@@")]
    [InlineData("pbkdf2-sha256$5$AAAA$AAAA")]                 // far too few iterations
    [InlineData("md5$1000$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$200000$$")]
    public void Malformed_stored_values_never_verify(string? stored) => Assert.False(ContentLock.Verify("anything", stored));

    [Fact]
    public void Password_rules()
    {
        Assert.NotNull(ContentLock.Validate(null));
        Assert.NotNull(ContentLock.Validate("abc"));
        Assert.Null(ContentLock.Validate("abcd"));
        Assert.NotNull(ContentLock.Validate(new string('x', 129)));
    }
}

public class XlsxTests
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static Dictionary<string, XDocument> Unzip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); return XDocument.Load(s); });
    }

    private static byte[] Make(params XlsxSheet[] sheets)
    {
        var ms = new MemoryStream();
        XlsxWriter.Write(ms, sheets);
        return ms.ToArray();
    }

    [Fact]
    public void Writes_a_well_formed_package()
    {
        var s = new XlsxSheet("목록");
        s.AddRow(new XlsxCell("제목", XlsxStyle.Header), new XlsxCell("날짜", XlsxStyle.Header));
        s.AddRow(new XlsxCell("a < b & \"c\"\nsecond line", XlsxStyle.Wrap), new XlsxCell(new DateOnly(2026, 11, 9), XlsxStyle.Date));
        s.AddRow(new XlsxCell(42), new XlsxCell(1.5), null, new XlsxCell(null, XlsxStyle.CellMuted));
        var parts = Unzip(Make(s));

        Assert.Equal(new HashSet<string> { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml" },
                     parts.Keys.ToHashSet());
        var rows = parts["xl/worksheets/sheet1.xml"].Descendants(Ns + "row").ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("a < b & \"c\"\nsecond line", rows[1].Elements(Ns + "c").First().Descendants(Ns + "t").Single().Value);
        Assert.Equal("46335", rows[1].Elements(Ns + "c").Last().Element(Ns + "v")!.Value);          // 2026-11-09 as an Excel serial
        Assert.Equal("42", rows[2].Elements(Ns + "c").First().Element(Ns + "v")!.Value);
        Assert.Equal("1.5", rows[2].Elements(Ns + "c").ElementAt(1).Element(Ns + "v")!.Value);
        Assert.Equal("목록", parts["xl/workbook.xml"].Descendants(Ns + "sheet").Single().Attribute("name")!.Value);
    }

    [Fact]
    public void Serial_numbers_follow_excels_1900_system()
    {
        var s = new XlsxSheet("d");
        s.AddRow(new XlsxCell(new DateOnly(1900, 3, 1), XlsxStyle.Date), new XlsxCell(new DateOnly(2000, 1, 1), XlsxStyle.Date));
        var v = Unzip(Make(s))["xl/worksheets/sheet1.xml"].Descendants(Ns + "v").Select(x => x.Value).ToList();
        Assert.Equal(["61", "36526"], v);                                                           // Excel: 1900-03-01 = 61, 2000-01-01 = 36526
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(51, "AZ")]
    [InlineData(702, "AAA")]
    public void Column_letters(int index, string expected) => Assert.Equal(expected, XlsxWriter.Column(index));

    [Fact]
    public void Invalid_xml_characters_are_removed_but_emoji_survive()
    {
        Assert.Equal("ab😀c", XlsxWriter.CleanXml("a\u0001b😀\u0000c"));
        Assert.Equal("x", XlsxWriter.CleanXml("x\uD800"));                                         // lone surrogate
        var s = new XlsxSheet("t");
        s.AddRow(new XlsxCell("bad\u0008char"));
        Unzip(Make(s));                                                                             // would throw on malformed XML
    }

    [Fact]
    public void Sheet_names_are_made_legal_and_unique()
    {
        var names = XlsxWriter.UniqueNames(["a/b:c", "A/B:C", new string('x', 40), "", "[x]", "'q'"]);
        Assert.Equal("abc", names[0]);
        Assert.Equal("ABC (2)", names[1]);
        Assert.Equal(31, names[2].Length);
        Assert.Equal("Sheet", names[3]);
        Assert.Equal("x", names[4]);
        Assert.Equal("q", names[5]);
        Assert.Equal(names.Count, names.Select(n => n.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void A_workbook_needs_a_sheet() => Assert.Throws<ArgumentException>(() => XlsxWriter.Write(new MemoryStream(), []));

    private static ExchangeBundle Sample() => new(
        [new ExItem("i1", "2026-11-09", "보고서 제출", true, null, null, 0, [new ExReminder(1440, "09:00")]),
         new ExItem("i2", "2026-11-09", "장보기", false, null, null, 1, [])],
        [new ExEvent("e1", "팀 회의", "안건", "2026-11-09T10:00", "2026-11-09T11:00", false, "FREQ=WEEKLY;BYDAY=MO;COUNT=3", null, null, ["2026-11-16"], [new ExReminder(30, null)]),
         new ExEvent("e2", "출장", "", "2026-11-12", "2026-11-14", true, null, null, null, [], [])],
        [], []);

    [Fact]
    public void List_sheet_has_one_row_per_record_sorted_by_date()
    {
        var sheets = XlsxExport.Build(Sample(), null, null, DayOfWeek.Sunday);
        var list = sheets[0];
        Assert.Equal("목록", list.Name);
        var texts = list.Rows.Select(r => r.Select(c => c?.Value switch { string s => s, DateOnly d => d.ToString("yyyy-MM-dd"), var x => x?.ToString() ?? "" }).ToArray()).ToList();
        Assert.Equal(["유형", "날짜", "시작", "종료일", "종료", "제목", "메모", "완료", "반복", "알림"], texts[0]);
        Assert.Equal(5, texts.Count);
        // the event (order 0) comes before the to-dos of the same day
        Assert.Equal(["일정", "2026-11-09", "10:00", "2026-11-09", "11:00", "팀 회의", "안건", "", "매주 월, 3회", "30분 전"], texts[1]);
        Assert.Equal("보고서 제출", texts[2][5]);
        Assert.Equal("✓", texts[2][7]);
        Assert.Equal("1일 전 09:00", texts[2][9]);
        Assert.Equal(["일정", "2026-11-12", "종일", "2026-11-14"], texts[4][..4]);
    }

    [Fact]
    public void Calendar_sheet_lays_the_month_out_in_weeks_with_events_items_and_holidays()
    {
        var sheets = XlsxExport.Build(Sample(), null, null, DayOfWeek.Sunday, holidayName: d => d == new DateOnly(2026, 11, 11) ? "빼빼로데이" : null);
        Assert.Equal(["목록", "2026-11"], sheets.Select(s => s.Name));
        var nov = sheets[1];
        // title, weekday header, then the weeks of Nov 2026 (starts on Sunday the 1st, ends Monday the 30th -> 5 weeks)
        Assert.Equal(2 + 5, nov.Rows.Count);
        Assert.Equal("2026년 11월", nov.Rows[0][0]!.Value);
        Assert.Equal(["일", "월", "화", "수", "목", "금", "토"], nov.Rows[1].Select(c => (string)c!.Value!));
        Assert.Contains((0, 0, 0, 6), nov.Merges);

        string Cell(int week, int dow) => (string)(nov.Rows[2 + week][dow]!.Value ?? "");
        Assert.Equal("1", Cell(0, 0));                                       // Sunday 1 Nov
        Assert.Equal("9\n10:00 팀 회의\n✓ 보고서 제출\n• 장보기", Cell(1, 1));    // Monday 9 Nov
        Assert.Equal("11  빼빼로데이", Cell(1, 3));
        // multi-day trip on 12, 13 and 14 November; the time prefix appears only on the first day of a timed event
        Assert.Equal("12\n출장", Cell(1, 4)); Assert.Equal("13\n출장", Cell(1, 5)); Assert.Equal("14\n출장", Cell(1, 6));
        Assert.DoesNotContain("팀 회의", Cell(2, 1));                          // the 16th is a skipped occurrence
        Assert.Contains("팀 회의", Cell(3, 1));                                // 23rd is the 3rd (COUNT=3: 9th, 23rd - 16th skipped but counted)
        // style: Sundays/Saturdays and holidays use the red style, other days the plain one, outside-month cells the muted one
        Assert.Equal(XlsxStyle.CellOff, nov.Rows[2][0]!.Style);
        Assert.Equal(XlsxStyle.Cell, nov.Rows[3][2]!.Style);
        Assert.Equal(XlsxStyle.CellOff, nov.Rows[3][3]!.Style);              // the holiday
        Assert.Equal(XlsxStyle.CellMuted, nov.Rows[6][3]!.Style);            // 2 Dec lies outside November
    }

    [Fact]
    public void Month_sheets_cover_the_requested_period_and_are_capped()
    {
        var one = XlsxExport.MonthRange(Sample(), new DateOnly(2026, 10, 15), new DateOnly(2026, 12, 2));
        Assert.Equal([new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1)], one);
        Assert.Equal(XlsxExport.MaxMonths, XlsxExport.MonthRange(Sample(), new DateOnly(2020, 1, 1), new DateOnly(2030, 1, 1)).Count);
        Assert.Single(XlsxExport.MonthRange(ExchangeBundle.Empty, null, null));
    }

    [Fact]
    public void Week_start_changes_the_grid()
    {
        var mon = XlsxExport.Build(Sample(), null, null, DayOfWeek.Monday)[1];
        Assert.Equal(["월", "화", "수", "목", "금", "토", "일"], mon.Rows[1].Select(c => (string)c!.Value!));
        Assert.Equal(2 + 6, mon.Rows.Count);                                  // Monday start: 1 Nov is a Sunday, so six weeks
        Assert.Null(mon.Rows[2][0]!.Value);                                   // Mon 26 Oct lies outside November
        Assert.Equal("1", (string)mon.Rows[2][6]!.Value!);                    // Sun 1 Nov closes the first row
    }

    [Fact]
    public void Repeat_and_reminder_wording()
    {
        Assert.Equal("매주 월·수, 6회", RepeatText.Describe(new RecurrenceRule { Kind = RepeatKind.Weekly, WeekdaysMask = (1 << 1) | (1 << 3), Count = 6 }));
        Assert.Equal("2주마다", RepeatText.Describe(new RecurrenceRule { Kind = RepeatKind.Weekly, Interval = 2 }));
        Assert.Equal("매월 2번째 주, 2027-12-31까지", RepeatText.Describe(new RecurrenceRule { Kind = RepeatKind.Monthly, Monthly = MonthlyMode.ByWeekday, Nth = 2, Until = new DateOnly(2027, 12, 31) }));
        Assert.Equal("", RepeatText.Describe(RecurrenceRule.None));
        Assert.Equal("에빙하우스 복습", RepeatText.Describe(new RecurrenceRule { Kind = RepeatKind.Ebbinghaus }));
        Assert.Equal("정시", RepeatText.Offset(new ExReminder(0, null)));
        Assert.Equal("2시간 전", RepeatText.Offset(new ExReminder(120, null)));
        Assert.Equal("45분 전", RepeatText.Offset(new ExReminder(45, null)));
        Assert.Equal("3일 전 08:30", RepeatText.Offset(new ExReminder(4320, "08:30")));
    }
}
