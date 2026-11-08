using System.Text;
using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.Tests;

public class SearchEngineTests
{
    private static Item It(string day, string text, bool done = false, string? color = null, bool deleted = false, string? id = null) =>
        new(id ?? Guid.NewGuid().ToString("N"), day, text, null, 0, done, 0, 0, deleted, color);

    private static EventEntry Ev(string title, string start, string? rrule = null, string body = "", string? color = null) =>
        new(Guid.NewGuid().ToString("N"), title, body, start, null, true, null, rrule, null, 0, color, 0, 0);

    private static List<SearchHit> Run(IEnumerable<Item>? items, IEnumerable<EventEntry>? events, SearchQuery q) =>
        SearchEngine.Search(items ?? [], events ?? [], q);

    [Fact]
    public void Korean_substring_matches_inside_a_word()
    {
        var hits = Run([It("2026-11-02", "주간회의 준비"), It("2026-11-03", "장보기")], null, new("회의"));
        Assert.Single(hits);
        Assert.Equal("주간회의 준비", hits[0].Text);
    }

    [Fact]
    public void Every_word_must_match_in_any_order_and_case_is_ignored()
    {
        var items = new[] { It("2026-11-02", "Quarterly Report draft"), It("2026-11-03", "report only") };
        Assert.Single(Run(items, null, new("DRAFT report")));
        Assert.Equal(2, Run(items, null, new("  REPORT ")).Count);
    }

    [Fact]
    public void Precomposed_and_decomposed_hangul_are_the_same()
    {
        string nfc = "한글 메모";
        string nfd = nfc.Normalize(NormalizationForm.FormD);
        Assert.NotEqual(nfc, nfd);
        Assert.Single(Run([It("2026-11-02", nfd)], null, new(nfc)));
        Assert.Single(Run([It("2026-11-02", nfc)], null, new(nfd)));
    }

    [Fact]
    public void Blank_text_matches_everything_so_filters_work_alone()
    {
        var items = new[] { It("2026-11-02", "a"), It("2026-11-03", "b", done: true) };
        Assert.Equal(2, Run(items, null, new()).Count);
        Assert.Equal("b", Run(items, null, new(Done: DoneFilter.Done)).Single().Text);
        Assert.Equal("a", Run(items, null, new(Done: DoneFilter.NotDone)).Single().Text);
    }

    [Fact]
    public void Date_range_is_inclusive()
    {
        var items = new[] { It("2026-10-31", "x"), It("2026-11-01", "x"), It("2026-11-30", "x"), It("2026-12-01", "x") };
        var hits = Run(items, null, new("x", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));
        Assert.Equal([new DateOnly(2026, 11, 30), new DateOnly(2026, 11, 1)], hits.Select(h => h.Date));
    }

    [Fact]
    public void Colour_filter_distinguishes_default_from_a_given_colour()
    {
        var items = new[] { It("2026-11-02", "plain"), It("2026-11-03", "red", color: "#FF6B6B") };
        Assert.Equal("red", Run(items, null, new(TextColor: "#ff6b6b")).Single().Text);
        Assert.Equal("plain", Run(items, null, new(TextColor: "")).Single().Text);
        Assert.Equal(2, Run(items, null, new(TextColor: null)).Count);
    }

    [Fact]
    public void Deleted_items_only_appear_when_asked_for()
    {
        var items = new[] { It("2026-11-02", "gone", deleted: true), It("2026-11-03", "here") };
        Assert.Equal("here", Run(items, null, new()).Single().Text);
        var all = Run(items, null, new(IncludeDeleted: true));
        Assert.Equal(2, all.Count);
        Assert.True(all.Single(h => h.Text == "gone").Deleted);
    }

    [Fact]
    public void Kind_filter_and_done_filter_interact_with_events()
    {
        var items = new[] { It("2026-11-02", "meeting notes") };
        var events = new[] { Ev("meeting", "2026-11-05") };
        Assert.Equal(2, Run(items, events, new("meeting")).Count);
        Assert.Equal(SearchKind.Event, Run(items, events, new("meeting", Kind: SearchKind.Event)).Single().Kind);
        Assert.Equal(SearchKind.Item, Run(items, events, new("meeting", Kind: SearchKind.Item)).Single().Kind);
        // events have no done flag, so a done filter hides them
        Assert.Equal(SearchKind.Item, Run(items, events, new("meeting", Done: DoneFilter.NotDone)).Single().Kind);
    }

    [Fact]
    public void Event_body_is_searched_too()
    {
        var hits = Run(null, [Ev("Dentist", "2026-11-05", body: "bring insurance card")], new("insurance"));
        Assert.Equal("Dentist", hits.Single().Text);
    }

    [Fact]
    public void Repeating_event_reports_its_first_occurrence_inside_the_range()
    {
        var weekly = Ev("Standup", "2026-01-05", "FREQ=WEEKLY");   // Mondays
        var inRange = Run(null, [weekly], new("standup", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));
        Assert.Equal(new DateOnly(2026, 11, 2), inRange.Single().Date);
        Assert.True(inRange.Single().Repeating);

        var limited = Ev("Course", "2026-01-05", "FREQ=WEEKLY;COUNT=2");
        Assert.Empty(Run(null, [limited], new("course", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30))));
        Assert.Single(Run(null, [limited], new("course")));   // no range: the series start date
    }

    [Fact]
    public void Open_ended_range_never_expands_a_series_without_bound()
    {
        var daily = Ev("Pills", "2026-01-01", "FREQ=DAILY");
        var hits = Run(null, [daily], new("pills", From: new DateOnly(2026, 6, 1)));
        Assert.Equal(new DateOnly(2026, 6, 1), hits.Single().Date);
    }

    [Fact]
    public void Results_are_newest_first_and_capped()
    {
        var items = Enumerable.Range(1, 30).Select(d => It($"2026-11-{d:00}", "x")).ToList();
        var hits = SearchEngine.Search(items, [], new("x"), limit: 5);
        Assert.Equal(5, hits.Count);
        Assert.Equal(new DateOnly(2026, 11, 30), hits[0].Date);
        Assert.Equal(new DateOnly(2026, 11, 26), hits[4].Date);
    }

    [Fact]
    public void Items_with_unreadable_days_are_ignored() =>
        Assert.Empty(Run([It("not-a-date", "x")], null, new("x")));
}

public class SearchServiceTests : IDisposable
{
    private readonly DbFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact]
    public void Finds_items_events_and_restorable_deleted_items_in_the_database()
    {
        var keep = _f.Items.Add("2026-11-02", "프로젝트 회의");
        var gone = _f.Items.Add("2026-11-03", "회의록 정리");
        _f.Items.SoftDelete(gone.Id);
        _f.Events.Add("회의 일정", "2026-11-04", allDay: true);

        var svc = new SearchService(_f.Items, _f.Events);
        var plain = svc.Run(new("회의"));
        Assert.Equal(2, plain.Count);
        Assert.DoesNotContain(plain, h => h.Id == gone.Id);

        var withDeleted = svc.Run(new("회의", IncludeDeleted: true));
        Assert.Equal(3, withDeleted.Count);

        Assert.True(_f.Items.Undelete(gone.Id));
        Assert.Contains(svc.Run(new("회의")), h => h.Id == gone.Id);
        Assert.Contains(svc.Run(new("회의")), h => h.Id == keep.Id);
    }

    [Fact]
    public void Wildcard_characters_in_the_query_are_plain_text()
    {
        _f.Items.Add("2026-11-02", "100% 완료");
        _f.Items.Add("2026-11-03", "a_b");
        var svc = new SearchService(_f.Items, _f.Events);
        Assert.Single(svc.Run(new("100%")));
        Assert.Single(svc.Run(new("a_b")));
        Assert.DoesNotContain(svc.Run(new("%")), h => !h.Text.Contains('%'));
    }

    [Fact]
    public void Range_is_applied_in_the_database_query()
    {
        _f.Items.Add("2026-10-15", "old");
        _f.Items.Add("2026-11-15", "new");
        var svc = new SearchService(_f.Items, _f.Events);
        Assert.Equal("new", svc.Run(new("", From: new DateOnly(2026, 11, 1))).Single().Text);
        Assert.Equal("old", svc.Run(new("", To: new DateOnly(2026, 10, 31))).Single().Text);
    }
}

public class CountdownCalcTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);

    private static Countdown Cd(CountdownType type, string date, bool lunar = false, string title = "t") =>
        new("id", title, date, type, false, false, lunar);

    [Theory]
    [InlineData("2026-11-20", "D-12", 12)]
    [InlineData("2026-11-08", "D-Day", 0)]
    [InlineData("2026-11-05", "D+3", -3)]
    public void Custom_and_exam_count_down_to_one_date(string date, string label, int days)
    {
        foreach (var type in new[] { CountdownType.Custom, CountdownType.Exam })
        {
            var s = CountdownCalc.Compute(Cd(type, date), Today)!;
            Assert.Equal(label, s.Label);
            Assert.Equal(days, s.Days);
        }
    }

    [Fact]
    public void Passed_one_off_dates_are_marked_elapsed_and_upcoming_ones_are_not()
    {
        Assert.True(CountdownCalc.Compute(Cd(CountdownType.Custom, "2026-11-05"), Today)!.Elapsed);
        Assert.False(CountdownCalc.Compute(Cd(CountdownType.Custom, "2026-11-20"), Today)!.Elapsed);
    }

    [Fact]
    public void Memorial_counts_days_since_with_the_start_day_as_day_one()
    {
        Assert.Equal("1일째", CountdownCalc.Compute(Cd(CountdownType.Memorial, "2026-11-08"), Today)!.Label);
        Assert.Equal("100일째", CountdownCalc.Compute(Cd(CountdownType.Memorial, Today.AddDays(-99).ToString("yyyy-MM-dd")), Today)!.Label);
        Assert.Equal("D-3", CountdownCalc.Compute(Cd(CountdownType.Memorial, "2026-11-11"), Today)!.Label);   // not started yet
    }

    [Fact]
    public void Memorial_shows_the_next_anniversary()
    {
        var s = CountdownCalc.Compute(Cd(CountdownType.Memorial, "2020-12-01"), Today)!;
        Assert.Equal("6주년 D-23", s.Detail);
        // on the anniversary itself it says D-Day, and the first day is not its own anniversary
        Assert.Equal("6주년 D-Day", CountdownCalc.Compute(Cd(CountdownType.Memorial, "2020-11-08"), Today)!.Detail);
        Assert.Equal("1주년 D-365", CountdownCalc.Compute(Cd(CountdownType.Memorial, "2026-11-08"), Today)!.Detail);
    }

    [Fact]
    public void Birthday_repeats_yearly_and_reports_the_age()
    {
        var before = CountdownCalc.Compute(Cd(CountdownType.Birthday, "1990-11-20"), Today)!;
        Assert.Equal("D-12", before.Label);
        Assert.Equal("만 36세", before.Detail);

        Assert.Equal("D-Day", CountdownCalc.Compute(Cd(CountdownType.Birthday, "1990-11-08"), Today)!.Label);

        var after = CountdownCalc.Compute(Cd(CountdownType.Birthday, "1990-11-01"), Today)!;
        Assert.Equal(new DateOnly(2027, 11, 1), after.Target);
        Assert.Equal("만 37세", after.Detail);
    }

    [Fact]
    public void Feb_29_birthday_falls_on_feb_28_in_common_years()
    {
        var s = CountdownCalc.Compute(Cd(CountdownType.Birthday, "2000-02-29"), Today)!;
        Assert.Equal(new DateOnly(2027, 2, 28), s.Target);
        Assert.Equal(new DateOnly(2028, 2, 29), CountdownCalc.Compute(Cd(CountdownType.Birthday, "2000-02-29"), new DateOnly(2028, 1, 1))!.Target);
    }

    [Fact]
    public void A_birthday_date_in_the_future_is_just_a_countdown()
    {
        var s = CountdownCalc.Compute(Cd(CountdownType.Birthday, "2026-12-01"), Today)!;
        Assert.Equal("D-23", s.Label);
        Assert.Equal("", s.Detail);
    }

    [Fact]
    public void Festival_repeats_every_year_from_its_month_and_day()
    {
        var s = CountdownCalc.Compute(Cd(CountdownType.Festival, "2000-12-25"), Today)!;
        Assert.Equal(new DateOnly(2026, 12, 25), s.Target);
        Assert.Equal(47, s.Days);
    }

    [Fact]
    public void Lunar_festival_and_birthday_resolve_through_the_lunar_calendar()
    {
        // Seollal 2027 (lunar 1 January) is 7 February 2027.
        var seollal = CountdownCalc.Compute(Cd(CountdownType.Festival, "2000-01-01", lunar: true), Today)!;
        Assert.Equal(new DateOnly(2027, 2, 7), seollal.Target);

        var birthday = CountdownCalc.Compute(Cd(CountdownType.Birthday, "1990-01-01", lunar: true), Today)!;
        Assert.Equal(new DateOnly(2027, 2, 7), birthday.Target);
        Assert.Equal("만 37세", birthday.Detail);
    }

    [Fact]
    public void One_off_lunar_date_converts_to_solar()
    {
        var s = CountdownCalc.Compute(Cd(CountdownType.Custom, "2027-01-01", lunar: true), Today)!;
        Assert.Equal(new DateOnly(2027, 2, 7), s.Target);
    }

    [Fact]
    public void Unreadable_dates_give_no_status()
    {
        Assert.Null(CountdownCalc.Compute(Cd(CountdownType.Custom, "soon"), Today));
        Assert.Null(CountdownCalc.Compute(Cd(CountdownType.Custom, "2027-13-01"), Today));
    }

    [Fact]
    public void Validation_messages()
    {
        Assert.NotNull(CountdownCalc.Validate("", "2026-01-01", false));
        Assert.NotNull(CountdownCalc.Validate("x", "01/02/2026", false));
        Assert.NotNull(CountdownCalc.Validate("x", "2026-02-30", false));
        Assert.NotNull(CountdownCalc.Validate("x", "2026-01-31", true));   // lunar months have at most 30 days
        Assert.Null(CountdownCalc.Validate("x", "2026-01-15", true));
        Assert.Null(CountdownCalc.Validate("시험", "2027-03-15", false));
    }

    [Fact]
    public void Ordering_puts_upcoming_first_nearest_first_then_elapsed_counters()
    {
        var list = new[]
        {
            Cd(CountdownType.Memorial, "2026-01-01", title: "memorial"),
            Cd(CountdownType.Custom, "2026-12-25", title: "far"),
            Cd(CountdownType.Custom, "2026-11-10", title: "near"),
            Cd(CountdownType.Custom, "bad", title: "broken"),
        };
        Assert.Equal(["near", "far", "memorial"], CountdownCalc.Ordered(list, Today).Select(x => x.Countdown.Title));
    }

    [Fact]
    public void Summary_line()
    {
        var b = Cd(CountdownType.Birthday, "1990-11-20", title: "민수");
        Assert.Equal("D-12 민수 (만 36세)", CountdownCalc.Summary(b, CountdownCalc.Compute(b, Today)!));
        var c = Cd(CountdownType.Custom, "2026-11-20", title: "시험");
        Assert.Equal("D-12 시험", CountdownCalc.Summary(c, CountdownCalc.Compute(c, Today)!));
    }
}
