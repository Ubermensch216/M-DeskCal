using System.Text;
using System.Text.RegularExpressions;
using DeskCal.Core;

namespace DeskCal.Ai.Intent;

/// <summary>
/// Reads a Korean sentence such as "다음 주 화요일 오후 2시 A사 미팅 1시간, 15분 전 알림" into an event, with no language model: dates,
/// times, durations, repeats and reminders are recognised by rules, so it answers at once, works without any AI installed, and can
/// be tested exhaustively. Principles: ① a date that is not found is reported as missing, never replaced by today; ② whatever is
/// inferred (am/pm, the year) is said in <see cref="ParsedEvent.Notes"/>; ③ the order of the patterns is part of the rules (full
/// dates before month-days, "다음 주 화요일" before a bare "화요일"); ④ only the recognised words are removed - what is left is the title.
/// </summary>
public static class KoreanEventParser
{
    public const int MaxChars = 500;
    private const string Wd = "월화수목금토일";

    private static Regex R(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ---- reminders --------------------------------------------------------------------------------
    private const string ReminderItem = @"(?:\d+\s*(?:분|시간|일|주)|하루|이틀|한\s*시간|반\s*시간)\s*전";
    private static readonly Regex ReminderList = R($@"(?<list>{ReminderItem}(?:\s*(?:,|와|과|및|그리고)?\s*{ReminderItem})*)\s*(?:에\s*)?(?:알림|알려\s*줘|알려줘|알람|리마인더|리마인드)");
    private static readonly Regex ReminderOne = R(@"(?:(?<n>\d+)\s*(?<u>분|시간|일|주)|(?<w>하루|이틀|한\s*시간|반\s*시간))\s*전");

    // ---- repeats ----------------------------------------------------------------------------------
    private const string WeekdayItem = $@"(?:[{Wd}]{{2,7}}|[{Wd}]요일)";
    private const string WeekdayListBody = $@"(?<list>(?:[{Wd}](?:\s*[,/·]\s*[{Wd}])+|{WeekdayItem})(?:\s*(?:,|/|·|과|와|및|랑)\s*{WeekdayItem})*)";
    private static readonly Regex MonthlyNth = R($@"매\s*(?:월|달)\s*(?<nth>마지막|첫\s*번?째|둘째|셋째|넷째|[1-4])\s*(?:번째\s*)?주?\s*(?<d>[{Wd}])요일");
    private static readonly Regex MonthLastDay = R(@"매\s*(?:월|달)\s*(?:말일|말|마지막\s*날)|월말(?:마다)?");
    private static readonly Regex MonthlyDay = R(@"매\s*(?:월|달)\s*(?<day>\d{1,2})\s*일");
    private static readonly Regex MonthlyBare = R(@"매\s*(?:월|달)|한\s*달에\s*한\s*번");
    private static readonly Regex MonthlyEvery = R(@"(?<n>\d+)\s*(?:개월|달)\s*마다|격월");
    private static readonly Regex QuarterEnd = R(@"분기\s*말");
    private static readonly Regex LegalWork = R(@"법정\s*근무일|영업일");
    private static readonly Regex WeekdayRepeat = R(@"매\s*평일|평일\s*마다|평일");
    // "매주 월수금", "격주 수요일", "2주마다 화요일": the weekday list counts only right after the repeat word (so a title like "일일 보고" is left alone)
    private static readonly Regex WeeklyMain = R($@"(?:(?<bi>격주)(?:로|마다)?|(?<n>\d+)\s*주\s*(?:마다|에\s*한\s*번)|매\s*주\s*(?:마다)?)(?:\s*(?:에\s*)?{WeekdayListBody}(?:\s*요일)?)?");
    private static readonly Regex WeeklyDay = R($@"(?<d>[{Wd}])요일\s*마다");
    private static readonly Regex DailyEvery = R(@"(?<n>\d+)\s*일\s*(?:마다|간격)");
    private static readonly Regex Daily = R(@"매\s*일|날마다|매일매일");
    private static readonly Regex Yearly = R(@"매\s*년|해마다");
    private static readonly Regex Ebbinghaus = R(@"에빙하우스|망각\s*곡선");
    private static readonly Regex CountRx = R(@"(?<n>\d+)\s*(?:번|회|차례)(?:\s*반복)?");
    private static readonly Regex UntilRx = R(@"(?:(?<y>\d{4})\s*년\s*)?(?<m>\d{1,2})\s*월\s*(?<d>\d{1,2})\s*일\s*까지|(?<iy>\d{4})[-./](?<im>\d{1,2})[-./](?<id>\d{1,2})\s*까지");

    // ---- dates ------------------------------------------------------------------------------------
    private static readonly Regex Lunar = R(@"음력\s*(?:(?<y>\d{4})\s*년\s*)?(?<leap>윤)?\s*(?<m>\d{1,2})\s*월\s*(?<d>\d{1,2})\s*일");
    private static readonly Regex IsoDate = R(@"(?<!\d)(?<y>\d{4})[-./]\s?(?<m>\d{1,2})[-./]\s?(?<d>\d{1,2})(?!\d)");
    private static readonly Regex WrittenDate = R(@"(?:(?<ry>올해|내년|내후년)\s*)?(?:(?<y>\d{4})\s*년\s*)?(?<m>\d{1,2})\s*월\s*(?<d>\d{1,2})\s*일");
    private static readonly Regex SlashDate = R(@"(?<![\d/.:-])(?<m>\d{1,2})/(?<d>\d{1,2})(?![\d/])");
    private static readonly Regex DaysLater = R(@"(?<n>\d+)\s*일\s*(?:후|뒤)");
    private static readonly Regex WeeksLater = R(@"(?<n>\d+)\s*주(?:일)?\s*(?:후|뒤)|(?<one>일주일)\s*(?:후|뒤)");
    private static readonly Regex MonthsLater = R(@"(?<n>\d+)\s*(?:개월|달)\s*(?:후|뒤)|(?<one>한\s*달)\s*(?:후|뒤)");
    private static readonly Regex WordDaysLater = R(@"(?<w>이틀|사흘|보름)\s*(?:후|뒤)");
    private static readonly Regex WeekendRx = R(@"(?<w>이번|다음|담)\s*주말");
    private static readonly Regex WeekWord = R($@"(?<w>다다음|다음|이번|지난|저번|담)\s*주(?!말)(?:\s*(?<d>[{Wd}])요일)?");
    private static readonly Regex WeekdayParen = R($@"\s*[(（]\s*(?<d>[{Wd}])(?:요일)?\s*[)）]");                     // "11월 20일(금)": the weekday written after a date
    private static readonly string[] DowName = ["일요일", "월요일", "화요일", "수요일", "목요일", "금요일", "토요일"];
    private static readonly Regex WeekdayOfWeekWord =R($@"(?<w>이번|다음|담)\s+(?<d>[{Wd}])요일");                  // "이번 토요일": no "주" in between
    private static readonly Regex MonthNth = R($@"(?:(?<ry>올해|내년)\s*)?(?<m>\d{{1,2}})\s*월\s*(?<nth>첫째|둘째|셋째|넷째|마지막)\s*주\s*(?<d>[{Wd}])요일");   // "12월 첫째 주 금요일"
    private static readonly Regex RelativeDay = R(@"내일모레|모레|글피|내일|오늘|어제|그저께|그제");
    private static readonly Regex BareWeekday = R($@"(?<![가-힣])(?<d>[{Wd}])요일");

    // ---- times ------------------------------------------------------------------------------------
    private const string TimeCore = @"(?:(?<ap{0}>오전|오후|아침|점심|저녁|밤|새벽)\s*)?(?<h{0}>\d{{1,2}})\s*(?:시(?!간)\s*(?:(?<m{0}>\d{{1,2}})\s*분|(?<half{0}>반))?|:(?<c{0}>\d{{2}})(?!\d))";
    private static readonly Regex TimeRange = R(string.Format(TimeCore, "") + @"\s*(?:부터|~|-|에서)\s*" + string.Format(TimeCore, "2") + @"(?:\s*까지)?");
    private static readonly Regex TimeOne = R(string.Format(TimeCore, ""));
    private static readonly Regex Noon = R(@"정오");
    private static readonly Regex Duration = R(@"(?:(?<h>\d+|한|두|세|네)\s*시간(?:\s*(?<half>반))?(?:\s*(?<m>\d+)\s*분)?|(?<only>반)\s*시간|(?<m2>\d+)\s*분)(?:\s*(?:동안|간|짜리))?(?!\s*(?:후|뒤|전))");
    private static readonly Regex AllDayRx = R(@"하루\s*종일|종일");

    // ---- title cleanup ----------------------------------------------------------------------------
    // what must not be left in a title: a month or an ordinal week the parser failed to use (it would silently be the wrong date)
    private static readonly Regex LeftoverDate = R(@"\d{1,2}\s*월|(?:첫째|둘째|셋째|넷째|마지막)\s*주");
    // "치과 예약 오전에": a day period with no hour is not part of the name
    private static readonly Regex TrailingPeriod = R(@"\s*(?:오전|오후|아침|점심|저녁|밤|새벽)(?:에|쯤|에는)?$");
    private static readonly Regex AmHint = R(@"아침|새벽|오전");
    private static readonly Regex PmHint = R(@"점심|저녁|오후|밤");
    private static readonly Regex Command = R(@"(?:일정\s*)?(?:추가|등록|잡아\s*줘|잡아줘|잡아|넣어\s*줘|넣어줘|만들어\s*줘|만들어줘|해\s*줘|해줘|알려\s*줘|알려줘)\s*$");
    private static readonly Regex LeadParticle = R(@"^(?:(?:은|는|엔|에는|에|에서|부터|까지|마다|의|로|으로)(?:\s+|$))+");
    private static readonly Regex TrailParticle = R(@"(?:(?:^|\s+)(?:에|에서|부터|까지|마다|로|으로|에는|엔))+$");
    private static readonly Regex Edge = R(@"^[\s,.:;·~\-""'“”‘’()\[\]/]+|[\s,.:;·~\-""'“”‘’()\[\]/]+$");

    public static ParseResult Parse(string? input, DateOnly today, DayOfWeek weekStart = DayOfWeek.Monday)
    {
        if (string.IsNullOrWhiteSpace(input)) return ParseResult.Failed("내용을 입력하세요.");
        if (input.Length > MaxChars) return ParseResult.Failed($"문장이 너무 깁니다 (최대 {MaxChars}자).");
        string s = Clean(input);
        var notes = new List<string>();
        string? problem = null;
        void Problem(string text) => problem ??= text;

        // 1. reminders ("15분 전 알림"): first, so that their "15분" is not read as a duration
        var reminders = new List<int>();
        while (Take(ReminderList, ref s) is { } rl)
            foreach (Match one in ReminderOne.Matches(rl.Groups["list"].Value))
                if (ReminderMinutes(one) is { } min && !reminders.Contains(min) && reminders.Count < MaxReminders) reminders.Add(min);

        // 2. repeat
        var (kind, interval, mask, monthly, nth, repeatDay, repeatWeekday) = ReadRepeat(ref s);
        int? count = null;
        DateOnly? until = null;
        bool repeating = kind != RepeatKind.None;
        if (repeating)
        {
            if (Take(CountRx, ref s) is { } cm && int.TryParse(cm.Groups["n"].Value, out int c) && c >= 1) count = c;
            if (Take(UntilRx, ref s) is { } um)
            {
                int? uy = um.Groups["y"].Success ? int.Parse(um.Groups["y"].Value) : um.Groups["iy"].Success ? int.Parse(um.Groups["iy"].Value) : null;
                int umo = int.Parse(um.Groups["m"].Success ? um.Groups["m"].Value : um.Groups["im"].Value), ud = int.Parse(um.Groups["d"].Success ? um.Groups["d"].Value : um.Groups["id"].Value);
                if (DateResolver.TryMonthDay(today, uy, umo, ud, out var u, out _)) until = u; else Problem("반복 종료일이 올바른 날짜가 아닙니다.");
            }
        }

        // 3. the date
        DateOnly? date = null;
        bool lunar = false;
        if (Take(Lunar, ref s) is { } lm)
        {
            lunar = true;
            int? ly = lm.Groups["y"].Success ? int.Parse(lm.Groups["y"].Value) : null;
            date = DateResolver.LunarToSolar(today, ly, int.Parse(lm.Groups["m"].Value), int.Parse(lm.Groups["d"].Value), lm.Groups["leap"].Success);
            if (date is null) Problem("음력 날짜를 양력으로 바꿀 수 없습니다 (없는 날짜이거나 지원 범위 밖입니다).");
            else notes.Add($"음력 {lm.Groups["m"].Value}월 {lm.Groups["d"].Value}일은 양력 {date:yyyy-MM-dd} 입니다.");
        }
        else date = ReadDate(ref s, today, weekStart, notes, Problem);

        // a weekday written after the date is a check, not a title: "11월 20일(금)" - and if it is not that weekday, the text or the year is probably off
        if (date is { } known && Take(WeekdayParen, ref s) is { } paren && Dow(paren.Groups["d"].Value[0]) != known.DayOfWeek)
            notes.Add($"{known.Month}월 {known.Day}일은 {DowName[(int)known.DayOfWeek]}인데 문장에는 '{paren.Groups["d"].Value}요일'로 적혀 있습니다. 날짜를 확인하세요.");

        // 4. time of day
        TimeOnly? start = null, end = null;
        DateOnly? endDate = null;
        if (Take(TimeRange, ref s) is { } tr)
        {
            if (!TryStart(tr.Groups["ap"].Value, tr.Groups["h"], tr.Groups["m"], tr.Groups["half"], tr.Groups["c"], s, out var t1, notes))
                Problem("시각이 올바르지 않습니다.");
            else
            {
                start = t1;
                if (!TryEnd(t1, tr.Groups["ap2"].Value, tr.Groups["h2"], tr.Groups["m2"], tr.Groups["half2"], tr.Groups["c2"], out var t2)) Problem("종료 시각이 시작 시각보다 빠르거나 올바르지 않습니다.");
                else end = t2;
            }
        }
        else if (Take(TimeOne, ref s) is { } t)
        {
            if (!TryStart(t.Groups["ap"].Value, t.Groups["h"], t.Groups["m"], t.Groups["half"], t.Groups["c"], s, out var t1, notes)) Problem("시각이 올바르지 않습니다.");
            else start = t1;
        }
        else if (Take(Noon, ref s) is not null) start = new TimeOnly(12, 0);

        // 5. how long
        if (Take(Duration, ref s) is { } d)
        {
            int minutes = DurationMinutes(d);
            if (start is null) notes.Add("시작 시각이 없어 길이는 반영하지 않았습니다.");
            else if (end is null && minutes > 0)
            {
                var total = start.Value.Hour * 60 + start.Value.Minute + minutes;
                if (total >= 24 * 60) { endDate = (date ?? today).AddDays(total / (24 * 60)); end = new TimeOnly(total % (24 * 60) / 60, total % 60); }
                else end = new TimeOnly(total / 60, total % 60);
            }
        }
        if (Take(AllDayRx, ref s) is not null && start is not null) { notes.Add("'종일' 표시가 있어 시각은 무시했습니다."); start = null; end = null; endDate = null; }

        // 6. a repeat without a date starts at the first day that fits
        RecurrenceRule rule = RecurrenceRule.None;
        if (repeating)
        {
            if (kind == RepeatKind.Yearly && lunar) kind = RepeatKind.LunarYearly;
            if (kind == RepeatKind.LunarYearly && !lunar) Problem("음력 매년 반복에는 음력 날짜(예: 음력 8월 15일)가 필요합니다.");
            date = StartFor(kind, mask, monthly, nth, repeatDay, repeatWeekday, date, today, notes);
            if (kind == RepeatKind.Weekly && mask != 0 && date is { } wd0 && (mask & (1 << (int)wd0.DayOfWeek)) == 0)
            {
                date = FirstMaskDayOnOrAfter(wd0, mask);
                notes.Add("시작일이 반복 요일이 아니어서 가장 가까운 반복 요일로 옮겼습니다.");
            }
            rule = new RecurrenceRule { Kind = kind, Interval = interval, WeekdaysMask = kind == RepeatKind.Weekly ? mask : 0, Monthly = monthly, Nth = nth, Count = count, Until = until };
        }

        // 7. what is left is the title
        string title = Title(s);
        if (start is null && TrailingPeriod.Match(title) is { Success: true } tp && title.Length > tp.Length)
        {
            title = title[..tp.Index].TrimEnd();
            notes.Add($"'{tp.Value.Trim()}' 처럼 시각이 정해지지 않은 표현은 제목에서 뺐습니다.");
        }
        if (title.Length > MaxTitle) { title = title[..MaxTitle].TrimEnd(); notes.Add("제목이 길어 잘랐습니다."); }
        var ev = new ParsedEvent(title, date, start, endDate, end, rule, reminders, notes);

        if (problem is null && LeftoverDate.Match(title) is { Success: true } left)
            problem = $"'{left.Value.Trim()}' 은(는) 날짜 표현 같은데 해석하지 못했습니다. 날짜를 다시 써 주세요 (예: 12월 4일).";
        if (problem is null && title.Length == 0) problem = "제목을 알 수 없습니다. 날짜·시각 말고 일정 이름을 함께 적어 주세요.";
        if (problem is null && date is null) problem = "날짜를 알 수 없습니다. 예: 내일, 다음 주 화요일, 12월 3일";
        return new ParseResult(ev, ParseSource.Rules, problem);
    }

    private const int MaxReminders = 5, MaxTitle = 500;

    // ---- pieces -----------------------------------------------------------------------------------

    private static string Clean(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (char ch in input.Normalize(NormalizationForm.FormC))
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>Replace the first match with a space (so neighbouring words do not run together) and return it.</summary>
    private static Match? Take(Regex rx, ref string s)
    {
        var m = rx.Match(s);
        if (!m.Success) return null;
        s = s.Remove(m.Index, m.Length).Insert(m.Index, " ");
        return m;
    }

    private static int? ReminderMinutes(Match m)
    {
        if (m.Groups["w"].Success)
            return Regex.Replace(m.Groups["w"].Value, @"\s+", "") switch { "하루" => 1440, "이틀" => 2880, "한시간" => 60, "반시간" => 30, _ => null };
        if (!int.TryParse(m.Groups["n"].Value, out int n)) return null;
        long minutes = m.Groups["u"].Value switch { "분" => n, "시간" => n * 60L, "일" => n * 1440L, "주" => n * 10080L, _ => -1 };
        return minutes is >= 0 and <= 525_600 ? (int)minutes : null;
    }

    private static int DurationMinutes(Match m)
    {
        if (m.Groups["only"].Success) return 30;
        if (m.Groups["m2"].Success) return int.TryParse(m.Groups["m2"].Value, out int x) ? x : 0;
        int hours = m.Groups["h"].Value switch { "한" => 1, "두" => 2, "세" => 3, "네" => 4, var v => int.TryParse(v, out int n) ? n : 0 };
        int minutes = hours * 60 + (m.Groups["half"].Success ? 30 : 0) + (m.Groups["m"].Success && int.TryParse(m.Groups["m"].Value, out int mm) ? mm : 0);
        return minutes is > 0 and <= 24 * 60 ? minutes : 0;
    }

    private static DayOfWeek Dow(char c) => c switch
    {
        '월' => DayOfWeek.Monday, '화' => DayOfWeek.Tuesday, '수' => DayOfWeek.Wednesday, '목' => DayOfWeek.Thursday,
        '금' => DayOfWeek.Friday, '토' => DayOfWeek.Saturday, _ => DayOfWeek.Sunday,
    };

    private static int MaskOf(string text)
    {
        int mask = 0;
        foreach (char ch in text.Replace("요일", ""))
            if (Wd.Contains(ch)) mask |= 1 << (int)Dow(ch);
        return mask;
    }

    private static DateOnly FirstMaskDayOnOrAfter(DateOnly from, int mask)
    {
        for (int i = 0; i < 7; i++)
            if ((mask & (1 << (int)from.AddDays(i).DayOfWeek)) != 0) return from.AddDays(i);
        return from;
    }

    private static (RepeatKind Kind, int Interval, int Mask, MonthlyMode Monthly, int Nth, int Day, DayOfWeek? Weekday) ReadRepeat(ref string s)
    {
        int interval = 1;
        if (Take(Ebbinghaus, ref s) is not null) return (RepeatKind.Ebbinghaus, 1, 0, MonthlyMode.ByDate, 0, 0, null);
        if (Take(MonthlyNth, ref s) is { } mn)
        {
            string w = Regex.Replace(mn.Groups["nth"].Value, @"\s+", "");
            int nth = w switch { "마지막" => -1, "첫째" or "첫번째" => 1, "둘째" => 2, "셋째" => 3, "넷째" => 4, var v => int.Parse(v) };
            return (RepeatKind.Monthly, 1, 0, MonthlyMode.ByWeekday, nth, 0, Dow(mn.Groups["d"].Value[0]));
        }
        if (Take(MonthLastDay, ref s) is not null) return (RepeatKind.MonthLastDay, 1, 0, MonthlyMode.ByDate, 0, 0, null);
        if (Take(QuarterEnd, ref s) is not null) return (RepeatKind.SeasonLastDay, 1, 0, MonthlyMode.ByDate, 0, 0, null);
        if (Take(MonthlyDay, ref s) is { } md && int.TryParse(md.Groups["day"].Value, out int day) && day is >= 1 and <= 31)
            return (RepeatKind.Monthly, 1, 0, MonthlyMode.ByDate, 0, day, null);
        if (Take(MonthlyEvery, ref s) is { } me)
            return (RepeatKind.Monthly, me.Groups["n"].Success ? Math.Max(1, int.Parse(me.Groups["n"].Value)) : 2, 0, MonthlyMode.ByDate, 0, 0, null);
        if (Take(MonthlyBare, ref s) is not null) return (RepeatKind.Monthly, 1, 0, MonthlyMode.ByDate, 0, 0, null);
        if (Take(LegalWork, ref s) is not null) return (RepeatKind.LegalWorkDay, 1, 0, MonthlyMode.ByDate, 0, 0, null);
        if (Take(WeekdayRepeat, ref s) is not null) return (RepeatKind.Weekday, 1, 0, MonthlyMode.ByDate, 0, 0, null);

        string before = s;
        if (Take(WeeklyMain, ref s) is { } wm)
        {
            interval = wm.Groups["bi"].Success ? 2 : wm.Groups["n"].Success ? Math.Max(1, int.Parse(wm.Groups["n"].Value)) : 1;
            int mask = 0;
            if (wm.Groups["list"].Success)
            {
                var list = wm.Groups["list"];
                string letters = list.Value.Replace("요일", "");
                // "월수금" is a list, "일일" (as in "일일 보고") is not: the same weekday twice means ordinary words
                if (letters.Where(Wd.Contains).Distinct().Count() == letters.Count(Wd.Contains)) mask = MaskOf(list.Value);
                else s = before.Remove(wm.Index, list.Index - wm.Index).Insert(wm.Index, " ");
            }
            return (RepeatKind.Weekly, interval, mask, MonthlyMode.ByDate, 0, 0, null);
        }
        if (Take(WeeklyDay, ref s) is { } wdm) return (RepeatKind.Weekly, 1, 1 << (int)Dow(wdm.Groups["d"].Value[0]), MonthlyMode.ByDate, 0, 0, null);
        if (Take(DailyEvery, ref s) is { } de) return (RepeatKind.Daily, Math.Max(1, int.Parse(de.Groups["n"].Value)), 0, MonthlyMode.ByDate, 0, 0, null);
        if (Take(Daily, ref s) is not null) return (RepeatKind.Daily, 1, 0, MonthlyMode.ByDate, 0, 0, null);
        if (Take(Yearly, ref s) is not null) return (RepeatKind.Yearly, 1, 0, MonthlyMode.ByDate, 0, 0, null);
        return (RepeatKind.None, 1, 0, MonthlyMode.ByDate, 0, 0, null);
    }

    private static DateOnly? ReadDate(ref string s, DateOnly today, DayOfWeek weekStart, List<string> notes, Action<string> problem)
    {
        DateOnly? Absolute(int? year, int month, int day, string label)
        {
            if (!DateResolver.TryMonthDay(today, year, month, day, out var d, out bool moved)) { problem($"{label} 은(는) 없는 날짜입니다."); return null; }
            if (moved) notes.Add($"{month}월 {day}일은 올해 이미 지나서 내년({d.Year}년)으로 봤습니다.");
            return d;
        }

        if (Take(IsoDate, ref s) is { } iso)
            return Absolute(int.Parse(iso.Groups["y"].Value), int.Parse(iso.Groups["m"].Value), int.Parse(iso.Groups["d"].Value), iso.Value.Trim());
        if (Take(WrittenDate, ref s) is { } w)
        {
            int? year = w.Groups["y"].Success ? int.Parse(w.Groups["y"].Value)
                      : w.Groups["ry"].Success ? today.Year + (w.Groups["ry"].Value switch { "내년" => 1, "내후년" => 2, _ => 0 }) : null;
            return Absolute(year, int.Parse(w.Groups["m"].Value), int.Parse(w.Groups["d"].Value), w.Value.Trim());
        }
        if (Take(SlashDate, ref s) is { } sl) return Absolute(null, int.Parse(sl.Groups["m"].Value), int.Parse(sl.Groups["d"].Value), sl.Value.Trim());

        if (Take(DaysLater, ref s) is { } dl && int.TryParse(dl.Groups["n"].Value, out int dn) && dn <= 3650) return today.AddDays(dn);
        if (Take(WeeksLater, ref s) is { } wl)
        {
            int weeks = wl.Groups["one"].Success ? 1 : int.Parse(wl.Groups["n"].Value);
            if (weeks <= 520) return today.AddDays(7 * weeks);
        }
        if (Take(MonthsLater, ref s) is { } ml)
        {
            int months = ml.Groups["one"].Success ? 1 : int.Parse(ml.Groups["n"].Value);
            if (months <= 120) return today.AddMonths(months);
        }
        if (Take(WordDaysLater, ref s) is { } wd) return today.AddDays(wd.Groups["w"].Value switch { "이틀" => 2, "사흘" => 3, _ => 15 });

        if (Take(MonthNth, ref s) is { } mn)
        {
            int month = int.Parse(mn.Groups["m"].Value);
            int nth = mn.Groups["nth"].Value switch { "첫째" => 1, "둘째" => 2, "셋째" => 3, "넷째" => 4, _ => -1 };
            var weekday = Dow(mn.Groups["d"].Value[0]);
            if (month is < 1 or > 12) { problem($"{month}월은 없는 달입니다."); return null; }
            int year = today.Year + (mn.Groups["ry"].Value == "내년" ? 1 : 0);
            var found = DateResolver.NthWeekdayOfMonth(year, month, weekday, nth);
            if (!mn.Groups["ry"].Success && found < today)
            {
                found = DateResolver.NthWeekdayOfMonth(year + 1, month, weekday, nth);
                notes.Add($"올해 {month}월은 이미 지나서 내년({found.Year}년)으로 봤습니다.");
            }
            return found;
        }
        if (Take(WeekendRx, ref s) is { } we)
        {
            notes.Add("주말은 토요일로 봤습니다.");
            return DateResolver.InWeek(today, we.Groups["w"].Value == "이번" ? 0 : 1, DayOfWeek.Saturday, weekStart);
        }
        if (Take(WeekWord, ref s) is { } ww)
        {
            int offset = ww.Groups["w"].Value switch { "다다음" => 2, "다음" or "담" => 1, "이번" => 0, _ => -1 };
            if (!ww.Groups["d"].Success) { problem($"'{ww.Value.Trim()}' 의 몇 요일인지 알 수 없습니다."); return null; }
            return DateResolver.InWeek(today, offset, Dow(ww.Groups["d"].Value[0]), weekStart);
        }
        if (Take(WeekdayOfWeekWord, ref s) is { } wow)
            return DateResolver.InWeek(today, wow.Groups["w"].Value == "이번" ? 0 : 1, Dow(wow.Groups["d"].Value[0]), weekStart);
        if (Take(RelativeDay, ref s) is { } rd)
            return today.AddDays(rd.Value switch { "내일" => 1, "모레" or "내일모레" => 2, "글피" => 3, "어제" => -1, "그저께" or "그제" => -2, _ => 0 });
        if (Take(BareWeekday, ref s) is { } bd) return DateResolver.OnOrAfter(today, Dow(bd.Groups["d"].Value[0]));
        return null;
    }

    /// <summary>Where a repeating event begins when the sentence names no date.</summary>
    private static DateOnly? StartFor(RepeatKind kind, int mask, MonthlyMode monthly, int nth, int day, DayOfWeek? weekday, DateOnly? date, DateOnly today, List<string> notes)
    {
        if (date is not null && !(kind == RepeatKind.Monthly && (day != 0 || monthly == MonthlyMode.ByWeekday))) return date;
        var from = date ?? today;
        DateOnly? start = kind switch
        {
            RepeatKind.Daily or RepeatKind.Ebbinghaus => today,
            RepeatKind.Weekday or RepeatKind.LegalWorkDay => FirstMaskDayOnOrAfter(today, 0b0111110),         // Monday to Friday
            RepeatKind.Weekly when mask != 0 => FirstMaskDayOnOrAfter(today, mask),
            RepeatKind.MonthLastDay => DateResolver.EndOfMonthOnOrAfter(today),
            RepeatKind.SeasonLastDay => DateResolver.QuarterEndOnOrAfter(today),
            RepeatKind.Monthly when monthly == MonthlyMode.ByWeekday && weekday is { } wd => DateResolver.NthWeekdayOnOrAfter(from, wd, nth),
            RepeatKind.Monthly when day != 0 => DateResolver.DayOfMonthOnOrAfter(from, day),
            _ => null,
        };
        if (start is not null && date is null) notes.Add($"날짜가 없어 반복의 첫 날을 {start:yyyy-MM-dd} 로 정했습니다.");
        return start;
    }

    private static bool TryStart(string ap, Group h, Group m, Group half, Group colon, string rest, out TimeOnly time, List<string> notes)
    {
        time = default;
        int hour = int.Parse(h.Value);
        int minute = colon.Success ? int.Parse(colon.Value) : m.Success ? int.Parse(m.Value) : half.Success ? 30 : 0;
        if (minute > 59 || hour > 24 || (hour == 24 && minute != 0)) return false;
        if (ap.Length == 0)
        {
            // no am/pm next to the hour: a day-period word elsewhere in the sentence ("저녁 먹기 ... 7시") decides, else the usual reading
            bool am = AmHint.IsMatch(rest), pm = PmHint.IsMatch(rest);
            if (hour is >= 1 and <= 11 && am != pm)
            {
                string word = (am ? AmHint : PmHint).Match(rest).Value;
                if (pm) hour += 12;
                notes.Add($"'{word}' 이(가) 있어 {hour}시를 {(pm ? "오후" : "오전")}로 봤습니다.");
            }
            else if (hour is >= 1 and <= 6) { notes.Add($"오전/오후가 없어 {hour}시를 오후 {hour}시로 봤습니다."); hour += 12; }
            else if (hour is >= 7 and <= 11) notes.Add($"오전/오후가 없어 {hour}시를 오전 {hour}시로 봤습니다.");
            if (hour == 24) hour = 0;
        }
        else if (ap is "오전" or "아침" or "새벽")
        {
            if (hour > 12) return false;
            if (hour == 12) hour = 0;
        }
        else
        {
            if (hour > 12 && hour != 24) return false;
            if (ap == "밤" && hour == 12) hour = 0;               // 밤 12시 is midnight
            else if (hour < 12) hour += 12;
            if (hour == 24) hour = 0;
        }
        time = new TimeOnly(hour, minute);
        return true;
    }

    /// <summary>The end of a range: the earliest reading (am or pm) that comes after the start.</summary>
    private static bool TryEnd(TimeOnly start, string ap, Group h, Group m, Group half, Group colon, out TimeOnly time)
    {
        time = default;
        int hour = int.Parse(h.Value);
        int minute = colon.Success ? int.Parse(colon.Value) : m.Success ? int.Parse(m.Value) : half.Success ? 30 : 0;
        if (minute > 59 || hour > 24 || (hour == 24 && minute != 0)) return false;
        IEnumerable<int> candidates = ap switch
        {
            "오전" or "아침" or "새벽" => [hour == 12 ? 0 : hour],
            "" => hour is >= 13 or 0 ? [hour % 24] : hour == 12 ? [12, 0] : [hour, hour + 12],
            _ => [hour == 12 ? 12 : hour + 12],
        };
        foreach (int c in candidates)
        {
            if (c > 23) continue;
            var t = new TimeOnly(c, minute);
            if (t > start) { time = t; return true; }
        }
        return false;
    }

    private static string Title(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = Regex.Replace(s, @"(?<=^|\s)(?:까지|부터)(?=\s|$)", " ");                 // "11월 18일까지 제출": the particle of a removed date
        string before;
        do
        {
            before = s;
            s = Command.Replace(s, "");
            s = Edge.Replace(s, "");
            s = LeadParticle.Replace(s, "");
            s = TrailParticle.Replace(s, "");
        } while (s != before);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
}
