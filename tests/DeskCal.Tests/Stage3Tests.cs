using DeskCal.Core;
using DeskCal.Ai.Intent;

namespace DeskCal.Tests;

public class OcrTextCleanerTests
{
    [Theory]
    [InlineData("11월 20 일(금) 오후 2 시", "11월 20일(금) 오후 2시")]                    // a space between a number and its unit
    [InlineData("회의는 14 : 30 에 시작", "회의는 14:30 에 시작")]                          // a space around the colon of a clock time
    [InlineData("2026. 11. 20 워크숍", "2026-11-20 워크숍")]                               // a dotted date becomes ISO
    [InlineData("1 1월 20일 회의", "11월 20일 회의")]                                      // a number cut in two
    [InlineData("11월 2 0일 회의", "11월 20일 회의")]
    [InlineData("1O월 l2일 회의", "10월 12일 회의")]                                       // letters read for digits next to a unit
    [InlineData("l1월 15일", "11월 15일")]
    [InlineData("１１월 ２０일", "11월 20일")]                                             // full-width digits
    [InlineData("시험 2 시간 공부", "시험 2시간 공부")]
    public void The_usual_slips_of_a_picture_are_repaired(string raw, string expected) => Assert.Equal(expected, OcrTextCleaner.Clean(raw));

    [Theory]
    [InlineData("3 일정 확인")]                                                            // "일정" is a word, not the unit 일
    [InlineData("Office 365 사용법")]                                                      // letters are only touched next to date units
    [InlineData("2026년 11월 20일")]
    [InlineData("11월 20일 오후 2시 / 11월 21일 오전 9시")]
    public void Text_that_is_fine_is_left_alone(string text) => Assert.Equal(text, OcrTextCleaner.Clean(text));

    [Fact]
    public void A_clock_time_that_lost_its_colon_gets_it_back_only_on_a_line_that_names_a_day()
    {
        Assert.Equal("11월 20일 14:30", OcrTextCleaner.Clean("11월 20일 1430"));
        Assert.Equal("내일 09:00 미팅", OcrTextCleaner.Clean("내일 0900 미팅"));
        Assert.Equal("1430 치과 예약", OcrTextCleaner.Clean("1430 치과 예약"));                                // no day on the line: could be anything
    }

    [Theory]
    [InlineData("11월 20일 2026 모임")]                                                    // a year, not a time
    [InlineData("11월 20일 1999 모임")]
    [InlineData("11월 20일 1437 모임")]                                                    // odd minutes: probably not a time
    [InlineData("11월 20일 회비 1500원")]                                                  // carries a unit
    [InlineData("11월 20일 1,430명")]
    public void Four_digit_numbers_that_may_not_be_times_are_not_touched(string text) => Assert.Equal(text, OcrTextCleaner.Clean(text));

    [Fact]
    public void Lines_blank_runs_and_invisible_characters_are_tidied()
    {
        Assert.Equal("가나다\n\n라마바", OcrTextCleaner.Clean("가​나다  \r\n\r\n\r\n\r\n 라마바 "));
        Assert.Equal("", OcrTextCleaner.Clean(null));
        Assert.Equal("", OcrTextCleaner.Clean("  \n "));
        Assert.True(OcrTextCleaner.Clean(new string('가', 50_000)).Length <= OcrTextCleaner.MaxChars);
    }
}

/// <summary>The round trip: what Windows OCR typically returns for a notice or a chat screenshot, cleaned, then read by the extractor.</summary>
public class OcrRoundTripTests
{
    private static readonly DateOnly Today = new(2026, 11, 14);

    // as read from a real picture on this PC: the colon of "14:30" dropped, a space inside a unit, the headline kept as its own line
    private const string NoticeRaw = "[공지] 11월 정기 회의 안내\n일시: 11월 20 일(금) 오후 2 시\n장소: 3층 회의실\n제출 서류는 11월 18일까지 제출해 주세요.";
    private const string ChatRaw = "홍길동 오후 2:13\n내일 1430 치과 예약\n다음 주 월요일까지 보고서 부탁드려요";
    private const string LabelSplitRaw = "11월 정기 회의 안내\n일시\n11월 20일(금) 오후 2시";

    [Fact]
    public void A_scanned_notice_gives_the_same_event_and_deadline_as_the_typed_text()
    {
        var rows = TextExtractor.Extract(OcrTextCleaner.Clean(NoticeRaw), Today, DayOfWeek.Monday);
        var ev = Assert.Single(rows, r => r.Kind == CandidateKind.Event);
        Assert.Equal((new DateOnly(2026, 11, 20), new TimeOnly(14, 0)), (ev.Parse.Event.Date!.Value, ev.Parse.Event.Start!.Value));
        Assert.Equal("11월 정기 회의 안내", ev.Title);
        var todo = Assert.Single(rows, r => r.Kind == CandidateKind.Todo);
        Assert.Equal(new DateOnly(2026, 11, 18), todo.Parse.Event.Date);
    }

    [Fact]
    public void Without_the_cleaning_the_same_picture_text_would_lose_its_time()
    {
        // "1430" (the colon was dropped) is not read as a time by the sentence reader; after cleaning it is
        const string line = "11월 20일(금) 1430 회의";
        var raw = TextExtractor.Extract(line, Today, DayOfWeek.Monday).Single();
        var cleaned = TextExtractor.Extract(OcrTextCleaner.Clean(line), Today, DayOfWeek.Monday).Single();
        Assert.Equal(new TimeOnly(14, 30), cleaned.Parse.Event.Start);
        Assert.NotEqual(new TimeOnly(14, 30), raw.Parse.Event.Start);
    }

    [Fact]
    public void A_chat_screenshot_with_a_dropped_colon_still_reads_the_time()
    {
        var rows = TextExtractor.Extract(OcrTextCleaner.Clean(ChatRaw), Today, DayOfWeek.Monday);
        var ev = Assert.Single(rows, r => r.Kind == CandidateKind.Event);
        Assert.Equal((new DateOnly(2026, 11, 15), new TimeOnly(14, 30)), (ev.Parse.Event.Date!.Value, ev.Parse.Event.Start!.Value));
        Assert.Contains("치과", ev.Title);
        Assert.Equal(new DateOnly(2026, 11, 16), Assert.Single(rows, r => r.Kind == CandidateKind.Todo).Parse.Event.Date);
    }

    [Fact]
    public void A_date_line_that_lost_its_label_is_named_after_the_headline_above_it()
    {
        var rows = TextExtractor.Extract(OcrTextCleaner.Clean(LabelSplitRaw), Today, DayOfWeek.Monday);
        var ev = Assert.Single(rows, r => r.Kind == CandidateKind.Event);
        Assert.Equal("11월 정기 회의 안내", ev.Title);
        Assert.Equal(new DateOnly(2026, 11, 20), ev.Parse.Event.Date);
        Assert.Contains(ev.Parse.Event.Notes, n => n.Contains("제목줄"));                    // and says where the title came from
    }

    [Fact]
    public void A_date_line_with_no_headline_stays_skipped_rather_than_getting_a_made_up_title()
    {
        var row = Assert.Single(TextExtractor.Extract("11월 20일(금) 오후 2시", Today, DayOfWeek.Monday));
        Assert.Equal(CandidateKind.Skipped, row.Kind);
        Assert.StartsWith("제목을 알 수 없습니다", row.Reason);
    }
}

public class ReviewPlannerTests
{
    private static readonly DateOnly Today = new(2026, 11, 14);                            // a Saturday

    [Fact]
    public void After_learning_the_reviews_grow_apart_from_the_learning_day()
    {
        var plan = ReviewPlanner.Plan(new DateOnly(2026, 11, 16), ReviewMode.AfterLearning, Today);
        Assert.Equal([1, 2, 4, 7, 15, 30], plan.Select(s => s.OffsetDays));
        Assert.Equal(
            [new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 16)],
            plan.Select(s => s.Date));
        Assert.All(plan, s => Assert.False(s.Shifted || s.Past));
        Assert.Equal([1, 2, 3, 4, 5, 6], plan.Select(s => s.Number));
    }

    [Fact]
    public void Before_an_exam_the_last_review_is_the_day_before_and_the_plan_is_chronological()
    {
        var plan = ReviewPlanner.Plan(new DateOnly(2026, 12, 20), ReviewMode.BeforeExam, Today);
        Assert.Equal([new DateOnly(2026, 11, 20), new DateOnly(2026, 12, 6), new DateOnly(2026, 12, 13), new DateOnly(2026, 12, 17), new DateOnly(2026, 12, 19)], plan.Select(s => s.Date));
        Assert.Equal([30, 14, 7, 3, 1], plan.Select(s => s.OffsetDays));
    }

    [Fact]
    public void Days_already_past_are_marked_so_that_the_window_can_leave_them_out()
    {
        var plan = ReviewPlanner.Plan(new DateOnly(2026, 12, 3), ReviewMode.BeforeExam, Today);          // exam in 19 days: 30 and 14... days before are past
        Assert.True(plan.First().Past);                                                                  // 11-03 is before today
        Assert.False(plan.Last().Past);
        var learned = ReviewPlanner.Plan(new DateOnly(2026, 11, 1), ReviewMode.AfterLearning, Today);
        Assert.Equal([true, true, true, true, false, false], learned.Select(s => s.Past));               // 11-02, 11-03, 11-05, 11-08 | 11-16, 12-01
    }

    [Fact]
    public void Days_off_move_the_reviews_to_a_working_day_when_asked()
    {
        Func<DateOnly, bool> off = d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var plan = ReviewPlanner.Plan(new DateOnly(2026, 11, 16), ReviewMode.AfterLearning, Today, off, avoidDaysOff: true);
        Assert.All(plan, s => Assert.False(off(s.Date)));
        Assert.True(plan.Zip(plan.Skip(1)).All(p => p.First.Date < p.Second.Date));
        // 11-21 would be Saturday -> not in this plan; 11-23 (Mon) is fine. Check a case that does move:
        var moved = ReviewPlanner.Plan(new DateOnly(2026, 11, 19), ReviewMode.AfterLearning, Today, off, avoidDaysOff: true);   // +2 = Saturday 11-21
        Assert.Contains(moved, s => s.OffsetDays == 2 && s.Shifted && s.Date == new DateOnly(2026, 11, 23));
        Assert.True(moved.Zip(moved.Skip(1)).All(p => p.First.Date < p.Second.Date));                     // never two on one day

        var untouched = ReviewPlanner.Plan(new DateOnly(2026, 11, 19), ReviewMode.AfterLearning, Today, off, avoidDaysOff: false);
        Assert.Contains(untouched, s => s.Date == new DateOnly(2026, 11, 21));
    }

    [Fact]
    public void Before_an_exam_a_day_off_moves_earlier_and_never_onto_the_exam_day()
    {
        Func<DateOnly, bool> off = d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var plan = ReviewPlanner.Plan(new DateOnly(2026, 12, 21), ReviewMode.BeforeExam, Today, off, avoidDaysOff: true);    // exam Monday: the day before is Sunday
        Assert.All(plan, s => Assert.True(s.Date < new DateOnly(2026, 12, 21)));
        Assert.All(plan, s => Assert.False(off(s.Date)));
        Assert.Equal(new DateOnly(2026, 12, 18), plan.Last().Date);
        Assert.True(plan.Zip(plan.Skip(1)).All(p => p.First.Date < p.Second.Date));
    }

    [Fact]
    public void If_every_day_is_off_the_plan_is_kept_as_it_was()
    {
        var plan = ReviewPlanner.Plan(new DateOnly(2026, 11, 16), ReviewMode.AfterLearning, Today, _ => true, avoidDaysOff: true);
        Assert.Equal(6, plan.Count);
        Assert.Equal(new DateOnly(2026, 11, 17), plan[0].Date);
    }

    [Fact]
    public void The_text_says_which_review_it_is_and_a_long_topic_is_cut()
    {
        var plan = ReviewPlanner.Plan(new DateOnly(2026, 11, 16), ReviewMode.AfterLearning, Today);
        Assert.Equal("복습 3/6 · 영어 단어 (학습 4일 뒤)", ReviewPlanner.Content("영어 단어", plan[2], 6, ReviewMode.AfterLearning));
        Assert.Equal("복습 1/5 · 한국사 (시험 30일 전)", ReviewPlanner.Content(" 한국사\n", new ReviewStep(1, 30, new DateOnly(2026, 11, 20), false, false), 5, ReviewMode.BeforeExam));
        Assert.True(ReviewPlanner.Content(new string('가', 500), plan[0], 6, ReviewMode.AfterLearning).Length < 120);
    }
}
