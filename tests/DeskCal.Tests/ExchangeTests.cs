using System.Text;
using DeskCal.Core;
using DeskCal.Core.Exchange;
using DeskCal.Data;

namespace DeskCal.Tests;

internal static class Ex
{
    public static ExItem Item(string day, string text, bool done = false, string? text_color = null, string? bg = null, string? id = null, params ExReminder[] rem) =>
        new(id ?? Guid.NewGuid().ToString("N"), day, text, done, bg, text_color, 0, rem);

    public static ExEvent Event(string title, string start, string? end = null, string? rrule = null, string? ext = null, string body = "",
        string? color = null, string? id = null, string[]? exceptions = null, params ExReminder[] rem) =>
        new(id ?? Guid.NewGuid().ToString("N"), title, body, start, end, !start.Contains('T'), rrule, ext, color, exceptions ?? [], rem);

    public static ExchangeBundle Bundle(IEnumerable<ExItem>? items = null, IEnumerable<ExEvent>? events = null) =>
        new([.. items ?? []], [.. events ?? []], [], []);
}

public class JsonExchangeTests
{
    [Fact]
    public void Round_trips_everything_including_countdowns_and_notes()
    {
        var bundle = new ExchangeBundle(
            [Ex.Item("2026-11-09", "보고서, \"최종\"\n아님", done: true, text_color: "#FF6B6B", bg: "#74C0FC", id: "i1", rem: new ExReminder(1440, "09:00"))],
            [Ex.Event("팀 회의", "2026-11-09T10:00", "2026-11-09T11:00", rrule: "FREQ=WEEKLY;BYDAY=MO;COUNT=6", body: "안건 ①", color: "#FFA94D", id: "e1",
                exceptions: ["2026-11-16"], rem: new ExReminder(30, null))],
            [new Countdown("c1", "시험", "2027-03-15", CountdownType.Exam, true, false, false)],
            [new ExNote("n1", "제목", "내용", "#FFE066")]);

        var json = JsonExchange.Write(bundle, DateTimeOffset.Now);
        Assert.Contains("보고서", json);                       // Korean stays readable, not \uXXXX
        var back = JsonExchange.Read(json);

        Assert.Empty(back.Warnings);
        var item = Assert.Single(back.Bundle.Items);
        Assert.Equal("보고서, \"최종\" 아님", item.Content);      // line breaks inside an item become spaces
        Assert.True(item.Done); Assert.Equal("#FF6B6B", item.TextColor); Assert.Equal("#74C0FC", item.BgColor);
        Assert.Equal(new ExReminder(1440, "09:00"), Assert.Single(item.Reminders));
        var ev = Assert.Single(back.Bundle.Events);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO;COUNT=6", ev.RRule);
        Assert.Equal(["2026-11-16"], ev.Exceptions);
        Assert.Equal("안건 ①", ev.Body);
        Assert.Equal(CountdownType.Exam, Assert.Single(back.Bundle.Countdowns).Type);
        Assert.Equal("내용", Assert.Single(back.Bundle.Notes).Content);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{\"format\":\"something-else\",\"version\":1}")]
    [InlineData("{\"version\":1}")]
    public void Foreign_or_broken_files_are_refused_with_a_message(string text) =>
        Assert.Throws<ExchangeFormatException>(() => JsonExchange.Read(text));

    [Fact]
    public void Newer_versions_are_refused_but_unknown_fields_are_ignored()
    {
        Assert.Throws<ExchangeFormatException>(() => JsonExchange.Read("{\"format\":\"M-DeskCal\",\"version\":99}"));
        var ok = JsonExchange.Read("{\"format\":\"M-DeskCal\",\"version\":1,\"future\":{\"x\":1},\"items\":[{\"id\":\"a\",\"day\":\"2026-11-09\",\"content\":\"x\",\"extra\":5}]}");
        Assert.Single(ok.Bundle.Items);
    }

    [Fact]
    public void Bad_records_are_dropped_with_a_warning_and_the_rest_survive()
    {
        var text = JsonExchange.Write(Ex.Bundle(
            [Ex.Item("2026-11-09", "ok", id: "ok"), Ex.Item("2026-13-40", "bad day", id: "b1"), Ex.Item("2026-11-09", "   ", id: "b2")],
            [Ex.Event("bad start", "soon", id: "b3"), Ex.Event("fine", "2026-11-09", id: "e")]), DateTimeOffset.Now);
        var r = JsonExchange.Read(text);
        Assert.Single(r.Bundle.Items);
        Assert.Single(r.Bundle.Events);
        Assert.NotEmpty(r.Warnings);
    }

    [Fact]
    public void Cleaner_normalises_colours_reminders_end_times_and_rules()
    {
        var raw = Ex.Bundle(
            [Ex.Item("2026-11-09", "a", text_color: "red", bg: "#abcdef", id: "1",
                rem: [new ExReminder(-5, null), new ExReminder(10, "25:99"), new ExReminder(10, null), new ExReminder(10, null)])],
            [Ex.Event("e", "2026-11-09T10:00", end: "2026-11-09T09:00", rrule: "FREQ=SOMETIMES", id: "2", exceptions: ["2026-11-10", "nope"])]);
        var r = ExchangeCleaner.Clean(raw);
        var item = r.Bundle.Items[0];
        Assert.Null(item.TextColor);
        Assert.Equal("#ABCDEF", item.BgColor);
        Assert.Equal([new ExReminder(10, null)], item.Reminders);          // negative dropped, bad clock dropped, duplicates merged
        var ev = r.Bundle.Events[0];
        Assert.Null(ev.End);                                               // ends before it starts
        Assert.Null(ev.RRule);                                             // unreadable rule: single event
        Assert.Empty(ev.Exceptions);                                       // exceptions only make sense for repeating events
        Assert.True(r.Warnings.Count >= 2);
    }

    [Fact]
    public void Duplicate_ids_inside_one_file_keep_the_first()
    {
        var r = ExchangeCleaner.Clean(Ex.Bundle([Ex.Item("2026-11-09", "first", id: "same"), Ex.Item("2026-11-10", "second", id: "same")]));
        Assert.Equal("first", Assert.Single(r.Bundle.Items).Content);
    }
}

public class CsvExchangeTests
{
    [Fact]
    public void Round_trips_awkward_text()
    {
        var bundle = Ex.Bundle(
            [Ex.Item("2026-11-09", "장보기, 우유 \"1L\"", done: true, text_color: "#69DB7C", id: "t1", rem: new ExReminder(0, "09:00"))],
            [Ex.Event("회의", "2026-11-09T14:00", "2026-11-09T15:30", rrule: "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=6", body: "첫 줄\r\n둘째 줄, 쉼표", color: "#FFA94D", id: "e1",
                exceptions: ["2026-11-11", "2026-11-23"], rem: [new ExReminder(30, null), new ExReminder(1440, "08:30")]),
             Ex.Event("연수", "2026-12-01", "2026-12-03", id: "e2")]);

        var back = CsvExchange.Read(CsvExchange.Write(bundle));

        Assert.Empty(back.Warnings);
        var item = Assert.Single(back.Bundle.Items);
        Assert.Equal("장보기, 우유 \"1L\"", item.Content);
        Assert.True(item.Done);
        Assert.Equal("t1", item.Id);
        Assert.Equal([new ExReminder(0, "09:00")], item.Reminders);
        Assert.Equal(2, back.Bundle.Events.Count);
        var e1 = back.Bundle.Events.Single(e => e.Id == "e1");
        Assert.Equal("2026-11-09T14:00", e1.Start); Assert.Equal("2026-11-09T15:30", e1.End);
        Assert.Equal("첫 줄\r\n둘째 줄, 쉼표", e1.Body);
        Assert.Equal(["2026-11-11", "2026-11-23"], e1.Exceptions);
        Assert.Equal([new ExReminder(30, null), new ExReminder(1440, "08:30")], e1.Reminders);
        var e2 = back.Bundle.Events.Single(e => e.Id == "e2");
        Assert.True(e2.AllDay); Assert.Equal("2026-12-03", e2.End);
    }

    [Fact]
    public void Written_file_starts_with_the_header_and_uses_crlf()
    {
        var csv = CsvExchange.Write(Ex.Bundle([Ex.Item("2026-11-09", "x")]));
        Assert.StartsWith("type,date,start_time,end_date,end_time,all_day,title,body,done,color,bg_color,rrule,ext_rule,exceptions,reminders,id\r\n", csv);
    }

    [Fact]
    public void Formula_like_text_is_guarded_on_write_and_restored_on_read()
    {
        var csv = CsvExchange.Write(Ex.Bundle([Ex.Item("2026-11-09", "=SUM(A1:A9)"), Ex.Item("2026-11-09", "- 장보기"), Ex.Item("2026-11-09", "@홍길동")]));
        Assert.Contains("'=SUM(A1:A9)", csv);
        Assert.Contains("'- 장보기", csv);
        var back = CsvExchange.Read(csv);
        Assert.Equal(["=SUM(A1:A9)", "- 장보기", "@홍길동"], back.Bundle.Items.Select(i => i.Content));
    }

    [Fact]
    public void Minimal_files_and_korean_headings_are_understood()
    {
        var r = CsvExchange.Read("날짜,제목\n2026-11-09,장보기\n2026-11-10,치과\n");
        Assert.Equal(["장보기", "치과"], r.Bundle.Items.Select(i => i.Content));

        var r2 = CsvExchange.Read("종류,날짜,시작시간,종료시간,제목,완료\n일정,2026.11.09,9:30,10:30,회의,\n할 일,2026/11/10,,,치과,완료\n");
        var ev = Assert.Single(r2.Bundle.Events);
        Assert.Equal("2026-11-09T09:30", ev.Start); Assert.Equal("2026-11-09T10:30", ev.End);
        Assert.True(Assert.Single(r2.Bundle.Items).Done);
    }

    [Theory]
    [InlineData("2026-11-09")]
    [InlineData("2026/11/9")]
    [InlineData("2026.11.9")]
    [InlineData("2026. 11. 9.")]
    [InlineData("20261109")]
    [InlineData("2026-1-9", "2026-01-09")]
    public void Date_styles(string text, string? expected = null)
    {
        Assert.True(CsvExchange.TryDate(text, out var d));
        Assert.Equal(expected ?? "2026-11-09", CountdownCalc.Format(d));
    }

    [Fact]
    public void Semicolon_delimited_files_work()
    {
        var r = CsvExchange.Read("date;title\n2026-11-09;a, b\n");
        Assert.Equal("a, b", Assert.Single(r.Bundle.Items).Content);
    }

    [Fact]
    public void Type_is_guessed_when_missing()
    {
        var r = CsvExchange.Read("date,start_time,title\n2026-11-09,10:00,timed\n2026-11-09,,plain\n");
        Assert.Equal("timed", Assert.Single(r.Bundle.Events).Title);
        Assert.Equal("plain", Assert.Single(r.Bundle.Items).Content);
    }

    [Fact]
    public void Bad_rows_are_reported_with_their_line_numbers_and_good_rows_still_import()
    {
        var r = CsvExchange.Read("type,date,title\ntodo,2026-11-09,ok\ntodo,not a date,x\ntodo,2026-11-10,\nwhat,2026-11-10,y\n\ntodo,2026-11-11,fine\n");
        Assert.Equal(["ok", "fine"], r.Bundle.Items.Select(i => i.Content));
        Assert.Contains(r.Warnings, w => w.StartsWith("3행"));
        Assert.Contains(r.Warnings, w => w.StartsWith("4행"));
        Assert.Contains(r.Warnings, w => w.StartsWith("5행"));
    }

    [Fact]
    public void Line_numbers_count_lines_inside_quoted_cells()
    {
        var r = CsvExchange.Read("type,date,title,body\nevent,2026-11-09,a,\"x\ny\nz\"\ntodo,bad,b,\n");
        Assert.Contains(r.Warnings, w => w.StartsWith("5행"));
    }

    [Fact]
    public void Unusable_files_are_refused()
    {
        Assert.Throws<ExchangeFormatException>(() => CsvExchange.Read(""));
        Assert.Throws<ExchangeFormatException>(() => CsvExchange.Read("foo,bar\n1,2\n"));
        Assert.Throws<ExchangeFormatException>(() => CsvExchange.Read("date,title\n2026-11-09,\"unterminated\n"));
    }

    [Fact]
    public void Rows_without_ids_get_stable_ids_so_reimporting_is_idempotent()
    {
        const string csv = "date,title\n2026-11-09,same\n2026-11-09,same\n";
        var a = CsvExchange.Read(csv).Bundle.Items;
        var b = CsvExchange.Read(csv).Bundle.Items;
        Assert.Equal(2, a.Count);                            // identical rows in one file are both kept
        Assert.NotEqual(a[0].Id, a[1].Id);
        Assert.Equal(a.Select(i => i.Id), b.Select(i => i.Id));
    }

    [Fact]
    public void Utf8_with_or_without_bom_and_korean_codepage_949_decode()
    {
        const string text = "date,title\n2026-11-09,한글 제목\n";
        Assert.Equal(text, ExchangeFiles.Decode(new UTF8Encoding(false).GetBytes(text)));
        Assert.Equal(text, ExchangeFiles.Decode(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray()));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal(text, ExchangeFiles.Decode(Encoding.GetEncoding(949).GetBytes(text)));
    }
}

public class IcsExchangeTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 3, 0, 0, DateTimeKind.Utc);

    private static ExchangeReadResult RoundTrip(ExchangeBundle b, Func<DateOnly, bool>? dayOff = null) =>
        IcsExchange.Read(IcsExchange.Write(b, Now, dayOff));

    [Fact]
    public void Events_and_todos_round_trip()
    {
        var b = Ex.Bundle(
            [Ex.Item("2026-11-09", "보고서; 제출, 마감", done: true, text_color: "#FF6B6B", bg: "#74C0FC", id: "i1", rem: new ExReminder(1440, "09:00"))],
            [Ex.Event("회의", "2026-11-09T10:00", "2026-11-09T11:30", body: "첫 줄\n둘째 줄", color: "#FFA94D", id: "e1", rem: new ExReminder(30, null)),
             Ex.Event("출장", "2026-12-01", "2026-12-03", id: "e2", rem: new ExReminder(1440, "08:30")),
             Ex.Event("하루", "2026-12-10", id: "e3")]);
        var ics = IcsExchange.Write(b, Now);
        var back = IcsExchange.Read(ics);

        Assert.Empty(back.Warnings);
        var item = Assert.Single(back.Bundle.Items);
        Assert.Equal(("i1", "보고서; 제출, 마감", true, "#FF6B6B", "#74C0FC"), (item.Id, item.Content, item.Done, item.TextColor, item.BgColor));
        Assert.Equal([new ExReminder(1440, null)], item.Reminders);   // an explicit 09:00 reads back as the 09:00 default: same moment

        var e1 = back.Bundle.Events.Single(e => e.Id == "e1");
        Assert.Equal(("2026-11-09T10:00", "2026-11-09T11:30", "첫 줄\n둘째 줄", "#FFA94D"), (e1.Start, e1.End, e1.Body, e1.Color));
        Assert.Equal([new ExReminder(30, null)], e1.Reminders);

        var e2 = back.Bundle.Events.Single(e => e.Id == "e2");
        Assert.True(e2.AllDay);
        Assert.Equal("2026-12-03", e2.End);                         // DTEND is exclusive in the file, inclusive here
        // 08:30 the day before is read back relative to the 09:00 default (1470 minutes) - the same moment
        var fires = (ExReminder r) => new DateTime(2026, 12, 1).Add(r.CustomTime is null ? TimeSpan.FromHours(9) : TimeSpan.Parse(r.CustomTime)).AddMinutes(-r.OffsetMinutes);
        Assert.Equal(new DateTime(2026, 11, 30, 8, 30, 0), fires(Assert.Single(e2.Reminders)));
        Assert.Null(back.Bundle.Events.Single(e => e.Id == "e3").End);
        Assert.Contains("DTEND;VALUE=DATE:20261204", ics);
        Assert.Contains("UID:e1@m-deskcal", ics);
    }

    [Fact]
    public void Standard_repeat_rules_and_skipped_days_round_trip()
    {
        var b = Ex.Bundle(events:
        [
            Ex.Event("주간", "2026-11-09T10:00", rrule: "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=6", id: "w", exceptions: ["2026-11-11"]),
            Ex.Event("월례", "2026-11-08", rrule: "FREQ=MONTHLY;BYDAY=2SU;UNTIL=20271231", id: "m"),
        ]);
        var ics = IcsExchange.Write(b, Now);
        Assert.Contains("RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=6", ics);
        Assert.Contains("EXDATE:20261111T100000", ics);

        var back = IcsExchange.Read(ics);
        var w = back.Bundle.Events.Single(e => e.Id == "w");
        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=6", w.RRule);
        Assert.Equal(["2026-11-11"], w.Exceptions);
        Assert.Equal("FREQ=MONTHLY;BYDAY=2SU;UNTIL=20271231", back.Bundle.Events.Single(e => e.Id == "m").RRule);
    }

    [Fact]
    public void App_specific_repeat_kinds_keep_their_rule_and_still_give_other_programs_the_dates()
    {
        var b = Ex.Bundle(events:
        [
            Ex.Event("복습", "2026-11-09", ext: "ebbinghaus", id: "eb"),
            Ex.Event("평일", "2026-11-09", ext: "weekday;COUNT=7", id: "wd"),
            Ex.Event("월말", "2026-11-30", ext: "monthlast;INTERVAL=2", id: "ml"),
        ]);
        var ics = IcsExchange.Write(b, Now);

        Assert.Contains("RDATE;VALUE=DATE:20261110,20261111,20261113,20261116,20261124,20261209", ics);   // +1,+2,+4,+7,+15,+30
        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR;COUNT=7", ics);
        Assert.Contains("RRULE:FREQ=MONTHLY;BYMONTHDAY=-1;INTERVAL=2", ics);

        var back = IcsExchange.Read(ics);
        Assert.Equal("ebbinghaus", back.Bundle.Events.Single(e => e.Id == "eb").ExtRule);
        Assert.Equal("weekday;COUNT=7", back.Bundle.Events.Single(e => e.Id == "wd").ExtRule);
        Assert.Equal("monthlast;INTERVAL=2", back.Bundle.Events.Single(e => e.Id == "ml").ExtRule);
        Assert.Null(back.Bundle.Events.Single(e => e.Id == "eb").RRule);
    }

    [Fact]
    public void Skipped_days_of_an_rdate_series_survive_the_trip()
    {
        var ics = IcsExchange.Write(Ex.Bundle(events: [Ex.Event("복습", "2026-11-09", ext: "ebbinghaus", id: "eb", exceptions: ["2026-11-11"])]), Now);
        Assert.DoesNotContain("20261111,", ics.Replace("\r\n ", ""));      // not in the RDATE list...
        var back = IcsExchange.Read(ics).Bundle.Events.Single();
        Assert.Equal(["2026-11-11"], back.Exceptions);                      // ...but remembered
    }

    [Fact]
    public void Long_lines_fold_at_75_octets_without_splitting_characters()
    {
        string title = string.Concat(Enumerable.Repeat("가나다라마바사", 20));
        var ics = IcsExchange.Write(Ex.Bundle(events: [Ex.Event(title, "2026-11-09", id: "long")]), Now);
        var physical = ics.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(physical, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75, l));
        Assert.Contains(physical, l => l.StartsWith(' '));
        Assert.Equal(title, IcsExchange.Read(ics).Bundle.Events.Single().Title);
    }

    [Fact]
    public void Folding_never_cuts_a_surrogate_pair()
    {
        string s = "SUMMARY:" + string.Concat(Enumerable.Repeat("😀", 40));
        var parts = IcsExchange.Fold(s).ToList();
        Assert.All(parts, p => Assert.True(Encoding.UTF8.GetByteCount(p) <= 75));
        Assert.Equal(s, parts[0] + string.Concat(parts.Skip(1).Select(p => p[1..])));
    }

    [Fact]
    public void Foreign_file_from_another_calendar_program()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Example//EN
            BEGIN:VTIMEZONE
            TZID:Asia/Seoul
            BEGIN:STANDARD
            DTSTART:19700101T000000
            TZOFFSETFROM:+0900
            TZOFFSETTO:+0900
            END:STANDARD
            END:VTIMEZONE
            BEGIN:VEVENT
            UID:abc-1@example.com
            DTSTART;TZID=Asia/Seoul:20261109T100000
            DTEND;TZID=Asia/Seoul:20261109T110000
            SUMMARY:Weekly sync
            LOCATION:회의실 A
            DESCRIPTION:Agenda
            RRULE:FREQ=WEEKLY;WKST=MO;BYDAY=MO
            EXDATE;TZID=Asia/Seoul:20261116T100000
            BEGIN:VALARM
            ACTION:DISPLAY
            TRIGGER:-PT15M
            END:VALARM
            END:VEVENT
            BEGIN:VEVENT
            UID:abc-2@example.com
            DTSTART;VALUE=DATE:20261224
            DTEND;VALUE=DATE:20261227
            SUMMARY:Holiday
            END:VEVENT
            BEGIN:VEVENT
            UID:abc-3@example.com
            DTSTART:20261110T093000
            DURATION:PT1H30M
            SUMMARY:Floating with duration
            RRULE:FREQ=MONTHLY;BYMONTHDAY=10
            END:VEVENT
            BEGIN:VEVENT
            UID:abc-4@example.com
            DTSTART;VALUE=DATE:20261111
            SUMMARY:Cancelled thing
            STATUS:CANCELLED
            END:VEVENT
            BEGIN:VEVENT
            UID:abc-5@example.com
            DTSTART;VALUE=DATE:20261112
            SUMMARY:Unsupported rule
            RRULE:FREQ=MONTHLY;BYSETPOS=-1;BYDAY=MO,TU,WE,TH,FR
            END:VEVENT
            END:VCALENDAR
            """;
        var r = IcsExchange.Read(ics.Replace("\n", "\r\n"));
        var byTitle = r.Bundle.Events.ToDictionary(e => e.Title);

        var sync = byTitle["Weekly sync"];
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", sync.RRule);                 // WKST dropped
        Assert.Equal("회의실 A".Length > 0 ? "장소: 회의실 A\nAgenda" : "", sync.Body);
        Assert.Equal([new ExReminder(15, null)], sync.Reminders);
        Assert.Equal("2026-11-09", sync.Start[..10]);
        Assert.Single(sync.Exceptions);

        var holiday = byTitle["Holiday"];
        Assert.True(holiday.AllDay);
        Assert.Equal("2026-12-26", holiday.End);

        var floating = byTitle["Floating with duration"];
        Assert.Equal("2026-11-10T09:30", floating.Start);
        Assert.Equal("2026-11-10T11:00", floating.End);
        Assert.Equal("FREQ=MONTHLY", floating.RRule);                      // BYMONTHDAY equal to the start day is implied

        Assert.DoesNotContain("Cancelled thing", byTitle.Keys);
        var unsupported = byTitle["Unsupported rule"];
        Assert.Null(unsupported.RRule);
        Assert.Contains(r.Warnings, w => w.Contains("지원하지 않는 반복 규칙"));
        Assert.Contains(r.Warnings, w => w.Contains("취소"));
    }

    [Fact]
    public void Utc_times_are_converted_to_local_wall_clock()
    {
        var r = IcsExchange.Read("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:u\r\nDTSTART:20261109T010000Z\r\nSUMMARY:utc\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        var expected = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2026, 11, 9, 1, 0, 0), TimeZoneInfo.Local);
        Assert.Equal(expected.ToString("yyyy-MM-ddTHH:mm"), r.Bundle.Events.Single().Start);
    }

    [Fact]
    public void Todos_use_due_or_start_and_completion_flags()
    {
        var r = IcsExchange.Read("BEGIN:VCALENDAR\r\nBEGIN:VTODO\r\nUID:t1\r\nDUE;VALUE=DATE:20261109\r\nSUMMARY:a\r\nSTATUS:COMPLETED\r\nEND:VTODO\r\n" +
                                 "BEGIN:VTODO\r\nUID:t2\r\nDTSTART;VALUE=DATE:20261110\r\nSUMMARY:b\r\nPERCENT-COMPLETE:100\r\nEND:VTODO\r\n" +
                                 "BEGIN:VTODO\r\nUID:t3\r\nSUMMARY:no date\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
        Assert.Equal([("a", true, "2026-11-09"), ("b", true, "2026-11-10")], r.Bundle.Items.Select(i => (i.Content, i.Done, i.Day)));
        Assert.Contains(r.Warnings, w => w.Contains("no date"));
    }

    [Theory]
    [InlineData("-PT15M", 15, null)]
    [InlineData("-PT1H", 60, null)]
    [InlineData("-P1D", 1440, null)]
    [InlineData("PT0S", 0, null)]
    public void Alarm_triggers_on_timed_events_become_minutes_before(string trigger, int offset, string? clock)
    {
        var r = IcsExchange.Read($"BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:u\r\nDTSTART:20261109T100000\r\nSUMMARY:x\r\nBEGIN:VALARM\r\nTRIGGER:{trigger}\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        Assert.Equal([new ExReminder(offset, clock)], r.Bundle.Events.Single().Reminders);
    }

    [Theory]
    [InlineData("-PT15H", "2026-11-08T09:00")]    // 9:00 the day before
    [InlineData("-PT9H", "2026-11-08T15:00")]
    [InlineData("-PT0M", "2026-11-09T00:00")]
    [InlineData("PT8H30M", "2026-11-09T08:30")]   // later than the 09:00 default clock would allow: stored with a custom time
    public void Alarm_triggers_on_all_day_entries_fire_at_the_same_moment_after_import(string trigger, string firesAt)
    {
        var r = IcsExchange.Read($"BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:u\r\nDTSTART;VALUE=DATE:20261109\r\nSUMMARY:x\r\nBEGIN:VALARM\r\nTRIGGER:{trigger}\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        var reminder = Assert.Single(r.Bundle.Events.Single().Reminders);
        var clock = reminder.CustomTime is null ? TimeSpan.FromHours(9) : TimeSpan.Parse(reminder.CustomTime);
        var actual = new DateTime(2026, 11, 9).Add(clock).AddMinutes(-reminder.OffsetMinutes);
        Assert.Equal(DateTime.Parse(firesAt), actual);
    }

    [Fact]
    public void All_day_reminders_written_by_this_program_read_back_unchanged()
    {
        foreach (var rem in new[] { new ExReminder(0, null), new ExReminder(1440, null), new ExReminder(1440, "08:30"), new ExReminder(60, "10:15"), new ExReminder(4320, "09:00") })
        {
            var back = RoundTrip(Ex.Bundle(events: [Ex.Event("x", "2026-11-09", id: "a", rem: rem)])).Bundle.Events.Single().Reminders.Single();
            var fire = (ExReminder r) => new DateTime(2026, 11, 9).Add(r.CustomTime is null ? TimeSpan.FromHours(9) : TimeSpan.Parse(r.CustomTime)).AddMinutes(-r.OffsetMinutes);
            Assert.Equal(fire(rem), fire(back));
        }
    }

    [Fact]
    public void Not_an_ics_file_is_refused()
    {
        Assert.Throws<ExchangeFormatException>(() => IcsExchange.Read("hello"));
        Assert.Throws<ExchangeFormatException>(() => IcsExchange.Read("{\"format\":\"M-DeskCal\"}"));
    }

    [Fact]
    public void Foreign_uids_become_stable_ids_and_own_uids_keep_their_id()
    {
        const string ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:something@google.com\r\nDTSTART;VALUE=DATE:20261109\r\nSUMMARY:x\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var a = IcsExchange.Read(ics).Bundle.Events.Single().Id;
        var b = IcsExchange.Read(ics).Bundle.Events.Single().Id;
        Assert.Equal(a, b);
        Assert.StartsWith("ics-", a);
        Assert.Equal("abc123", IcsExchange.Read(ics.Replace("something@google.com", "abc123@m-deskcal")).Bundle.Events.Single().Id);
    }
}

public class ExchangeServiceTests : IDisposable
{
    private readonly DbFixture _a = new(), _b = new();
    public void Dispose() { _a.Dispose(); _b.Dispose(); }

    private ExchangeService Svc(DbFixture f) => new(f.Db, f.Clock);

    private void Seed(DbFixture f)
    {
        f.Clock.NowMs = 1_800_000_000_000;
        var i = f.Items.Add("2026-11-09", "보고서");
        f.Items.SetDone(i.Id, true); f.Items.SetTextColor(i.Id, "#FF6B6B");
        f.Items.Add("2026-11-09", "장보기");
        f.Items.Add("2027-03-01", "먼 훗날");
        f.Reminders.Add(ReminderOwner.Item, i.Id, 1440, "09:00");
        var e = f.Events.Add("주간 회의", "2026-11-09T10:00", "2026-11-09T11:00", false, null, "FREQ=WEEKLY;BYDAY=MO;COUNT=4", null, "안건", "#FFA94D");
        f.Events.AddException(e.Id, new DateOnly(2026, 11, 16));
        f.Reminders.Add(ReminderOwner.Event, e.Id, 30);
        f.Events.Add("작년 일정", "2025-01-10", null, true);
        f.Countdowns.Add("시험", "2027-03-15", CountdownType.Exam, true, true);
    }

    [Fact]
    public void Full_export_into_an_empty_database_reproduces_everything_through_every_format()
    {
        Seed(_a);
        var bundle = Svc(_a).Collect();
        Assert.Equal(3, bundle.Items.Count);
        Assert.Equal(2, bundle.Events.Count);
        Assert.Single(bundle.Countdowns);

        // JSON carries everything
        var viaJson = JsonExchange.Read(JsonExchange.Write(bundle, DateTimeOffset.Now));
        var result = Svc(_b).Apply(viaJson.Bundle);
        Assert.Equal(new ImportResult(3, 0, 2, 0, 1, 0, 0, 0), result);

        var copy = Svc(_b).Collect();
        Assert.Equal(bundle.Items.Select(i => (i.Id, i.Day, i.Content, i.Done, i.TextColor)), copy.Items.Select(i => (i.Id, i.Day, i.Content, i.Done, i.TextColor)));
        var ev = copy.Events.Single(e => e.Title == "주간 회의");
        Assert.Equal(["2026-11-16"], ev.Exceptions);
        Assert.Equal([new ExReminder(30, null)], ev.Reminders);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO;COUNT=4", ev.RRule);
        Assert.Equal("안건", ev.Body);
        Assert.Equal([new ExReminder(1440, "09:00")], copy.Items.Single(i => i.Content == "보고서").Reminders);
        Assert.Equal("시험", Assert.Single(copy.Countdowns).Title);
    }

    [Theory]
    [InlineData(ExchangeFormat.Csv)]
    [InlineData(ExchangeFormat.Ics)]
    public void Items_and_events_survive_csv_and_ics(ExchangeFormat format)
    {
        Seed(_a);
        var bundle = Svc(_a).Collect();
        string path = _a.TempFile("x" + ExchangeFiles.Extension(format));
        ExchangeFiles.WriteFile(path, format, bundle, DateTimeOffset.Now);

        var read = ExchangeFiles.ReadFile(path);
        Assert.Empty(read.Warnings);
        Svc(_b).Apply(read.Bundle);
        var copy = Svc(_b).Collect();

        Assert.Equal(bundle.Items.Select(i => (i.Id, i.Day, i.Content, i.Done, i.TextColor)).OrderBy(x => x.Id),
                     copy.Items.Select(i => (i.Id, i.Day, i.Content, i.Done, i.TextColor)).OrderBy(x => x.Id));
        var ev = copy.Events.Single(e => e.Title == "주간 회의");
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO;COUNT=4", ev.RRule);
        Assert.Equal(["2026-11-16"], ev.Exceptions);
        Assert.Equal([new ExReminder(30, null)], ev.Reminders);
        Assert.Equal("2026-11-09T10:00", ev.Start); Assert.Equal("2026-11-09T11:00", ev.End);
        Assert.Empty(copy.Countdowns);                                          // countdowns only travel in JSON
    }

    [Fact]
    public void Importing_twice_adds_nothing_the_second_time_and_never_overwrites()
    {
        Seed(_a);
        var bundle = Svc(_a).Collect();
        Svc(_b).Apply(bundle);
        _b.Items.UpdateContent(_b.Items.GetByDay("2026-11-09")[0].Id, "내가 고친 내용");

        var again = Svc(_b).Apply(bundle);
        Assert.Equal(0, again.TotalAdded);
        Assert.Equal(bundle.Items.Count + bundle.Events.Count + bundle.Countdowns.Count, again.TotalSkipped);
        Assert.Equal("내가 고친 내용", _b.Items.GetByDay("2026-11-09")[0].Content);
    }

    [Fact]
    public void Imported_reminders_are_armed_from_now_so_old_alarms_do_not_burst()
    {
        Seed(_a);
        _b.Clock.NowMs = 1_900_000_000_000;
        Svc(_b).Apply(Svc(_a).Collect());
        Assert.All(_b.Reminders.ListAll(), r => Assert.Equal(1_900_000_000_000, r.FiredAt));
        Assert.Equal(2, _b.Reminders.ListAll().Count);
    }

    [Fact]
    public void Period_export_keeps_items_in_range_and_events_with_an_occurrence_there()
    {
        Seed(_a);
        var svc = Svc(_a);
        var nov = svc.Collect(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
        Assert.Equal(["보고서", "장보기"], nov.Items.Select(i => i.Content));
        Assert.Equal(["주간 회의"], nov.Events.Select(e => e.Title));          // the 2025 event is out of range
        Assert.Empty(nov.Countdowns);

        var march = svc.Collect(new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 31));
        Assert.Equal(["먼 훗날"], march.Items.Select(i => i.Content));
        Assert.Empty(march.Events);                                            // COUNT=4 weekly series is long over

        Assert.Equal(["작년 일정"], svc.Collect(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)).Events.Select(e => e.Title));
    }

    [Fact]
    public void Open_ended_repeating_events_are_exported_for_any_later_period()
    {
        _a.Events.Add("매일", "2026-01-01", null, true, null, "FREQ=DAILY");
        Assert.Single(Svc(_a).Collect(new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 31)).Events);
        Assert.Empty(Svc(_a).Collect(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)).Events);
    }

    [Fact]
    public void Imported_items_are_appended_after_the_existing_ones_of_the_same_day()
    {
        _b.Items.Add("2026-11-09", "원래 있던 것");
        Svc(_b).Apply(Ex.Bundle([Ex.Item("2026-11-09", "가져온 것 1"), Ex.Item("2026-11-09", "가져온 것 2")]));
        Assert.Equal(["원래 있던 것", "가져온 것 1", "가져온 것 2"], _b.Items.GetByDay("2026-11-09").Select(i => i.Content));
    }

    [Fact]
    public void Writing_replaces_an_existing_file_and_leaves_no_temp_files()
    {
        string path = _a.TempFile("out.csv");
        File.WriteAllText(path, "old");
        ExchangeFiles.WriteFile(path, ExchangeFormat.Csv, Ex.Bundle([Ex.Item("2026-11-09", "x")]), DateTimeOffset.Now);
        Assert.StartsWith("﻿type,", File.ReadAllText(path, Encoding.UTF8).Replace("﻿", "﻿"));
        Assert.Equal(0xEF, File.ReadAllBytes(path)[0]);                        // BOM for Excel
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));

        string json = _a.TempFile("out.json");
        ExchangeFiles.WriteFile(json, ExchangeFormat.Json, Ex.Bundle(), DateTimeOffset.Now);
        Assert.Equal((byte)'{', File.ReadAllBytes(json)[0]);                   // no BOM for JSON
    }

    [Fact]
    public void Unknown_extension_is_sniffed_from_content()
    {
        string p = _a.TempFile("data.txt");
        File.WriteAllText(p, JsonExchange.Write(Ex.Bundle([Ex.Item("2026-11-09", "x")]), DateTimeOffset.Now));
        Assert.Single(ExchangeFiles.ReadFile(p).Bundle.Items);
        File.WriteAllText(p, "BEGIN:VCALENDAR\r\nBEGIN:VTODO\r\nUID:a\r\nDUE;VALUE=DATE:20261109\r\nSUMMARY:y\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
        Assert.Single(ExchangeFiles.ReadFile(p).Bundle.Items);
        File.WriteAllText(p, "date,title\n2026-11-09,z\n");
        Assert.Single(ExchangeFiles.ReadFile(p).Bundle.Items);
    }

    [Fact]
    public void Missing_file_gives_a_readable_error() =>
        Assert.Throws<ExchangeFormatException>(() => ExchangeFiles.ReadFile(_a.TempFile("nope.json")));
}
