using DeskCal.Core.Calendars;

namespace DeskCal.Tests;

public class SolarTermTests
{
    // Published UTC instants of the equinoxes and solstices (month, day, hour, minute).
    private static readonly (int Year, int Lon, int Mo, int D, int H, int Mi)[] Known =
    [
        (2000,0,3,20,7,35),(2000,90,6,21,1,48),(2000,180,9,22,17,28),(2000,270,12,21,13,37),
        (2010,0,3,20,17,32),(2010,90,6,21,11,28),(2010,180,9,23,3,9),(2010,270,12,21,23,38),
        (2020,0,3,20,3,50),(2020,90,6,20,21,44),(2020,180,9,22,13,31),(2020,270,12,21,10,2),
        (2023,0,3,20,21,24),(2023,90,6,21,14,58),(2023,180,9,23,6,50),(2023,270,12,22,3,27),
        (2024,0,3,20,3,6),(2024,90,6,20,20,51),(2024,180,9,22,12,44),(2024,270,12,21,9,20),
        (2025,0,3,20,9,1),(2025,90,6,21,2,42),(2025,180,9,22,18,19),(2025,270,12,21,15,3),
        (2026,0,3,20,14,46),(2026,90,6,21,8,24),(2026,180,9,23,0,5),(2026,270,12,21,20,50),
    ];

    [Fact]
    public void Equinoxes_and_solstices_match_published_instants_within_two_minutes()
    {
        foreach (var (year, lon, mo, d, h, mi) in Known)
        {
            var expected = new DateTime(year, mo, d, h, mi, 0, DateTimeKind.Utc).AddHours(9);   // as KST
            var term = SolarTerms.ForYear(year).Single(t => t.Longitude == lon);
            double errMinutes = (term.Kst - expected).TotalMinutes;
            Assert.True(Math.Abs(errMinutes) <= 2, $"{year} lon {lon}: computed {term.Kst:u} vs expected {expected:u} ({errMinutes:+0.0;-0.0} min)");
        }
    }

    [Theory]
    [InlineData(2024, 2, 4, 17, 27)]   // 입춘 2024-02-04 17:27 KST
    [InlineData(2025, 2, 3, 23, 10)]   // 입춘 2025: 50 minutes before midnight - a date-boundary case
    [InlineData(2026, 2, 4, 5, 2)]
    public void Ipchun_date_and_time(int year, int month, int day, int hour, int minute)
    {
        var t = SolarTerms.ForYear(year).Single(x => x.Name == "입춘");
        Assert.Equal(new DateOnly(year, month, day), t.Date);
        Assert.True(Math.Abs((t.Kst - new DateTime(year, month, day, hour, minute, 0)).TotalMinutes) <= 2, t.Kst.ToString("u"));
    }

    [Fact]
    public void Jonghwa_2026_dates_around_the_equinox_use_korea_time()
    {
        // 2026 춘분 is 23:46 KST on March 20 (14:46 UTC) - still March 20 in Korea.
        Assert.Equal(new DateOnly(2026, 3, 20), SolarTerms.ForYear(2026).Single(t => t.Name == "춘분").Date);
        // 2026 동지 is 05:50 KST on December 22 although it is December 21 in UTC.
        Assert.Equal(new DateOnly(2026, 12, 22), SolarTerms.ForYear(2026).Single(t => t.Name == "동지").Date);
    }

    [Fact]
    public void Every_year_has_24_ordered_terms_with_plausible_spacing()
    {
        string[] names = ["소한","대한","입춘","우수","경칩","춘분","청명","곡우","입하","소만","망종","하지","소서","대서","입추","처서","백로","추분","한로","상강","입동","소설","대설","동지"];
        for (int year = SolarTerms.MinYear; year <= SolarTerms.MaxYear; year++)
        {
            var terms = SolarTerms.ForYear(year);
            Assert.Equal(names, terms.Select(t => t.Name));
            Assert.All(terms, t => Assert.Equal(year, t.Kst.Year));
            for (int i = 1; i < terms.Count; i++)
            {
                double gap = (terms[i].Kst - terms[i - 1].Kst).TotalDays;
                Assert.InRange(gap, 14.3, 16.5);              // the Sun covers 15 degrees in 14.7-15.7 days
                Assert.NotEqual(terms[i].Date, terms[i - 1].Date);
            }
        }
    }

    [Fact]
    public void OnDate_finds_the_term_and_is_null_otherwise()
    {
        Assert.Equal("춘분", SolarTerms.OnDate(new DateOnly(2026, 3, 20))?.Name);
        Assert.Null(SolarTerms.OnDate(new DateOnly(2026, 3, 21)));
        Assert.Null(SolarTerms.OnDate(new DateOnly(1800, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolarTerms.ForYear(1899));
    }
}

public class LunarTests
{
    [Theory]
    [InlineData(2026, 2, 17, 2026, 1, 1)]    // 설날 2026
    [InlineData(2025, 1, 29, 2025, 1, 1)]
    [InlineData(2024, 9, 17, 2024, 8, 15)]   // 추석 2024
    [InlineData(2026, 9, 25, 2026, 8, 15)]
    [InlineData(2023, 2, 20, 2023, 2, 1)]
    [InlineData(2026, 1, 5, 2025, 11, 17)]   // before 설날 the lunar year is still the previous one
    public void Known_conversions(int y, int m, int d, int ly, int lm, int ld)
    {
        Assert.True(KoreanLunar.TryFromSolar(new DateOnly(y, m, d), out var l));
        Assert.Equal(new LunarDate(ly, lm, ld, false), l);
    }

    [Fact]
    public void Leap_month_is_flagged_and_keeps_the_month_number()
    {
        // 2023 had a leap 2nd month (윤2월) that began on 2023-03-22.
        Assert.True(KoreanLunar.TryFromSolar(new DateOnly(2023, 3, 22), out var l));
        Assert.Equal(new LunarDate(2023, 2, 1, true), l);
        Assert.Equal("윤2.1", l.ToShortString());
        Assert.Equal(new DateOnly(2023, 3, 22), KoreanLunar.ToSolar(2023, 2, 1, leapMonth: true));
        Assert.Equal(new DateOnly(2023, 2, 20), KoreanLunar.ToSolar(2023, 2, 1));
        Assert.Equal(new DateOnly(2023, 4, 20), KoreanLunar.ToSolar(2023, 3, 1));   // 3월 comes after the leap month
        Assert.Null(KoreanLunar.ToSolar(2023, 3, 1, leapMonth: true));              // no leap 3월 that year
        Assert.Null(KoreanLunar.ToSolar(2024, 2, 1, leapMonth: true));              // no leap month at all in 2024
    }

    [Fact]
    public void Every_day_roundtrips_and_lunar_days_advance_consistently()
    {
        LunarDate? prev = null;
        for (var d = new DateOnly(1901, 1, 1); d <= new DateOnly(2049, 12, 31); d = d.AddDays(1))
        {
            Assert.True(KoreanLunar.TryFromSolar(d, out var l), d.ToString());
            Assert.Equal(d, KoreanLunar.ToSolar(l.Year, l.Month, l.Day, l.IsLeapMonth));
            Assert.InRange(l.Day, 1, 30);
            Assert.InRange(l.Month, 1, 12);
            if (prev is { } p)
            {
                if (l.Day == 1) Assert.True(p.Day is 29 or 30, $"month length before {d}: {p}");
                else { Assert.Equal(p.Day + 1, l.Day); Assert.Equal((p.Year, p.Month, p.IsLeapMonth), (l.Year, l.Month, l.IsLeapMonth)); }
            }
            prev = l;
        }
    }

    [Fact]
    public void Out_of_range_dates_are_rejected_not_thrown()
    {
        // the framework's table covers 918-02-14 .. 2051-02-10
        Assert.False(KoreanLunar.TryFromSolar(new DateOnly(900, 1, 1), out _));
        Assert.False(KoreanLunar.TryFromSolar(new DateOnly(2100, 1, 1), out _));
        Assert.True(KoreanLunar.TryFromSolar(new DateOnly(1800, 1, 1), out _));
        Assert.Null(KoreanLunar.ToSolar(500, 1, 1));
        Assert.Null(KoreanLunar.ToSolar(2100, 1, 1));
    }
}

public class HolidayTests
{
    private static readonly KoreanHolidays H = new();

    private static string[] DayOffs(int year, KoreanHolidays? h = null) =>
        (h ?? H).ForYear(year).Where(x => x.IsDayOff).Select(x => $"{x.Date.Month}/{x.Date.Day}").Distinct().ToArray();

    [Theory]
    [InlineData(2020, 1, 25)] [InlineData(2021, 2, 12)] [InlineData(2022, 2, 1)] [InlineData(2023, 1, 22)]
    [InlineData(2024, 2, 10)] [InlineData(2025, 1, 29)] [InlineData(2026, 2, 17)] [InlineData(2027, 2, 7)]   // 2027: new moon 00:56 KST on Feb 7 (Feb 6 in China)
    public void Seollal_dates(int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), H.ForYear(year).Single(h => h.Name == "설날").Date);

    [Theory]
    [InlineData(2020, 10, 1)] [InlineData(2021, 9, 21)] [InlineData(2022, 9, 10)] [InlineData(2023, 9, 29)]
    [InlineData(2024, 9, 17)] [InlineData(2025, 10, 6)] [InlineData(2026, 9, 25)] [InlineData(2027, 9, 15)]
    public void Chuseok_dates(int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), H.ForYear(year).Single(h => h.Name == "추석").Date);

    [Theory]
    [InlineData(2020, 4, 30)] [InlineData(2021, 5, 19)] [InlineData(2022, 5, 8)] [InlineData(2023, 5, 27)]
    [InlineData(2024, 5, 15)] [InlineData(2025, 5, 5)] [InlineData(2026, 5, 24)]
    public void Buddhas_birthday_dates(int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), H.ForYear(year).Single(h => h.Name == "부처님오신날").Date);

    [Fact]
    public void Full_day_off_list_2026()
    {
        // 설날 연휴 Mon-Wed; 3/1 and 8/15 and 10/3 fall on weekends -> substitutes; 부처님오신날 on a Sunday -> 5/25.
        Assert.Equal(["1/1", "2/16", "2/17", "2/18", "3/1", "3/2", "5/5", "5/24", "5/25", "6/3", "6/6",
                      "8/15", "8/17", "9/24", "9/25", "9/26", "10/3", "10/5", "10/9", "12/25"], DayOffs(2026));
    }

    [Fact]
    public void Full_day_off_list_2025_shares_one_substitute_for_overlapping_holidays()
    {
        // 어린이날 and 부처님오신날 are both 5/5 -> a single substitute on 5/6; 추석 starts on a Sunday -> 10/8.
        Assert.Equal(["1/1", "1/27", "1/28", "1/29", "1/30", "3/1", "3/3", "5/5", "5/6", "6/3", "6/6",
                      "8/15", "10/3", "10/5", "10/6", "10/7", "10/8", "10/9", "12/25"], DayOffs(2025));
        Assert.Equal(1, H.ForYear(2025).Count(h => h.Kind == HolidayKind.Substitute && h.Date.Month == 5));
    }

    [Fact]
    public void Full_day_off_list_2024()
    {
        Assert.Equal(["1/1", "2/9", "2/10", "2/11", "2/12", "3/1", "4/10", "5/5", "5/6", "5/15", "6/6",
                      "8/15", "9/16", "9/17", "9/18", "10/1", "10/3", "10/9", "12/25"], DayOffs(2024));
    }

    [Fact]
    public void Full_day_off_list_2021_covers_the_rules_that_took_effect_that_year()
    {
        // 광복절(Sun)->8/16 and 개천절(Sun)->10/4 and 한글날(Sat)->10/11 are new in 2021; 현충일 and 성탄절 get none.
        Assert.Equal(["1/1", "2/11", "2/12", "2/13", "3/1", "5/5", "5/19", "6/6", "8/15", "8/16",
                      "9/20", "9/21", "9/22", "10/3", "10/4", "10/9", "10/11", "12/25"], DayOffs(2021));
    }

    [Fact]
    public void Saturday_holidays_only_get_substitutes_after_the_law_changed()
    {
        Assert.DoesNotContain(H.ForYear(2020), h => h.Kind == HolidayKind.Substitute && h.Date.Month == 10);   // 개천절 on a Saturday in 2020
        Assert.DoesNotContain(H.ForYear(2018), h => h.Name.Contains("대체") && h.Date.Month == 5);              // 어린이날 on a Saturday in 2018
        Assert.Contains(H.ForYear(2019), h => h.Name == "어린이날 대체공휴일" && h.Date == new DateOnly(2019, 5, 6)); // on a Sunday in 2019
        Assert.Contains(H.ForYear(2020), h => h.Name == "설날 대체공휴일" && h.Date == new DateOnly(2020, 1, 27));
        Assert.Contains(H.ForYear(2022), h => h.Name == "추석 대체공휴일" && h.Date == new DateOnly(2022, 9, 12));
        Assert.Contains(H.ForYear(2017), h => h.Name == "추석 대체공휴일" && h.Date == new DateOnly(2017, 10, 6));   // 추석 overlapped 개천절
        Assert.Contains(H.ForYear(2023), h => h.Name == "부처님오신날 대체공휴일" && h.Date == new DateOnly(2023, 5, 29));
        Assert.Contains(H.ForYear(2027), h => h.Name == "성탄절 대체공휴일" && h.Date == new DateOnly(2027, 12, 27));
    }

    [Fact]
    public void Historic_rule_changes_제헌절_식목일_한글날()
    {
        Assert.True(H.ForYear(2007).Single(h => h.Name == "제헌절").IsDayOff);
        Assert.False(H.ForYear(2008).Single(h => h.Name == "제헌절").IsDayOff);
        Assert.True(H.ForYear(2005).Single(h => h.Name == "식목일").IsDayOff);
        Assert.False(H.ForYear(2006).Single(h => h.Name == "식목일").IsDayOff);
        Assert.DoesNotContain(H.ForYear(2012), h => h.Name == "한글날");
        Assert.True(H.ForYear(2013).Single(h => h.Name == "한글날").IsDayOff);
    }

    [Fact]
    public void Observances_are_listed_but_are_not_days_off()
    {
        var obs = H.ForYear(2026).Where(h => !h.IsDayOff).ToDictionary(h => h.Name, h => h.Date);
        Assert.Equal(new DateOnly(2026, 5, 8), obs["어버이날"]);
        Assert.Equal(new DateOnly(2026, 5, 18), obs["성년의 날"]);        // third Monday of May
        Assert.Equal(new DateOnly(2026, 3, 3), obs["정월대보름"]);        // lunar 1/15 = 2026-03-03
        Assert.False(H.IsDayOff(new DateOnly(2026, 5, 8)));
        Assert.True(H.IsDayOff(new DateOnly(2026, 5, 25)));
    }

    [Fact]
    public void Overrides_can_add_remove_and_annotate()
    {
        var h = new KoreanHolidays([
            new(new DateOnly(2026, 10, 10), "임시공휴일(사용자)", true),
            new(new DateOnly(2026, 6, 3), "", true, Remove: true),
            new(new DateOnly(2026, 4, 1), "우리 회사 창립일", false),
        ]);
        Assert.True(h.IsDayOff(new DateOnly(2026, 10, 10)));
        Assert.False(h.IsDayOff(new DateOnly(2026, 6, 3)));
        var company = h.On(new DateOnly(2026, 4, 1)).Single();
        Assert.False(company.IsDayOff);
        Assert.Equal("우리 회사 창립일", company.Name);
        // an added day-off pushes a substitute that would have landed on it
        Assert.DoesNotContain(DayOffs(2026, h), x => x == "10/10" && false);
    }

    [Fact]
    public void Added_day_off_blocks_a_substitute_from_landing_on_it()
    {
        // 2026-10-03 is a Saturday -> substitute on Monday 10/5. If 10/5 is already a (user-added) holiday it moves to 10/6.
        var h = new KoreanHolidays([new(new DateOnly(2026, 10, 5), "회사 휴무", true)]);
        var sub = h.ForYear(2026).Single(x => x.Name == "개천절 대체공휴일");
        Assert.Equal(new DateOnly(2026, 10, 6), sub.Date);
    }

    [Fact]
    public void Outside_the_supported_years_nothing_is_returned()
    {
        Assert.Empty(H.On(new DateOnly(1999, 1, 1)));
        Assert.Empty(H.ForYear(2051));
        Assert.Single(H.On(new DateOnly(2000, 1, 1)));
    }

    [Fact]
    public void Every_supported_year_builds_without_overlapping_day_off_duplicates()
    {
        for (int y = KoreanHolidays.MinYear; y <= KoreanHolidays.MaxYear; y++)
        {
            var offs = H.ForYear(y).Where(h => h.IsDayOff).ToList();
            Assert.NotEmpty(offs);
            Assert.All(offs, h => Assert.Equal(y, h.Date.Year));
            // a substitute never lands on a weekend or on another day off
            foreach (var s in offs.Where(h => h.Kind == HolidayKind.Substitute))
            {
                Assert.NotEqual(DayOfWeek.Saturday, s.Date.DayOfWeek);
                Assert.NotEqual(DayOfWeek.Sunday, s.Date.DayOfWeek);
                Assert.Single(offs, o => o.Date == s.Date);
            }
        }
    }
}

public class DayInfoTests
{
    private static readonly KoreanDays C = new();

    [Fact]
    public void Seollal_day_combines_lunar_and_holiday()
    {
        var d = C.Get(new DateOnly(2026, 2, 17));
        Assert.Equal("1.1", d.Lunar!.Value.ToShortString());
        Assert.Equal("설날", d.HolidayName);
        Assert.True(d.IsDayOff);
        Assert.Null(d.Term);
    }

    [Fact]
    public void Solar_term_day()
    {
        var d = C.Get(new DateOnly(2026, 3, 20));
        Assert.Equal("춘분", d.Term!.Name);
        Assert.False(d.IsDayOff);
    }

    [Fact]
    public void Festival_day_is_not_a_day_off()
    {
        var d = C.Get(new DateOnly(2026, 5, 8));
        Assert.Equal("어버이날", d.FestivalName);
        Assert.Null(d.HolidayName);
        Assert.False(d.IsDayOff);
    }

    [Fact]
    public void Dates_outside_every_range_still_return_a_day_info()
    {
        var d = C.Get(new DateOnly(2300, 1, 1));
        Assert.Null(d.Lunar); Assert.Null(d.Term); Assert.Empty(d.Holidays); Assert.False(d.IsDayOff);
    }
}
