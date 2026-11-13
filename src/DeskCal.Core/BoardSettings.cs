namespace DeskCal.Core;

/// <summary>How the calendar board arranges days. Definitions are ours (functional equivalents of the five modes).</summary>
public enum DisplayMode
{
    /// <summary>MaxRows weeks starting at the week of the anchor date; navigation scrolls by pages of weeks.</summary>
    FloatingMonth = 0,
    /// <summary>The anchor's month; days outside it are left blank. Row count follows the month (4-6).</summary>
    FixedMonth = 1,
    /// <summary>The anchor's month on a constant 6 rows, neighbouring months' days shown dimmed.</summary>
    FullMonth = 2,
    /// <summary>One row with every day of the anchor's month.</summary>
    SingleLine = 3,
    /// <summary>One row with LineDays days starting at the anchor date.</summary>
    SingleLineFloating = 4,
}

/// <summary>How a week's number is derived.</summary>
public enum WeekNumberRule
{
    /// <summary>ISO 8601 (week containing the Thursday; week 1 has the year's first Thursday).</summary>
    Iso = 0,
    /// <summary>The week containing January 1st is week 1 (common in Korea and the US).</summary>
    FirstDayOfYear = 1,
}

/// <summary>Marker drawn before each item line of a day.</summary>
public enum BulletStyle { None = 0, Dot = 1, Number = 2 }

/// <summary>How a background photo is fitted to the board.</summary>
public enum SkinFit
{
    /// <summary>Cover the board, cropping the overflow (keeps proportions).</summary>
    Fill = 0,
    /// <summary>Show the whole picture, leaving bars where it does not reach (keeps proportions).</summary>
    Fit = 1,
    /// <summary>Stretch to the board's shape (distorts).</summary>
    Stretch = 2,
    Tile = 3,
    /// <summary>Original size, centred.</summary>
    Center = 4,
}

public sealed record BoardSettings
{
    // ---- calendar annotations ----
    public bool ShowLunar { get; init; } = true;
    public bool ShowSolarTerms { get; init; } = true;
    public bool ShowHolidays { get; init; } = true;
    /// <summary>Memorial and traditional days that are not days off (어버이날, 정월대보름, ...).</summary>
    public bool ShowFestivals { get; init; } = true;
    public string HolidayColor { get; init; } = "#FF6B6B";
    public string HolidayBgColor { get; init; } = "";
    public string TermColor { get; init; } = "#8FD694";

    // ---- items ----
    public BulletStyle Bullet { get; init; } = BulletStyle.Dot;
    public bool ShowCompleted { get; init; } = true;
    /// <summary>Show finished items after the unfinished ones of the same day.</summary>
    public bool MoveDoneToBottom { get; init; }
    /// <summary>Draw the bell next to items that have a reminder.</summary>
    public bool ShowReminderMark { get; init; } = true;
    /// <summary>Extra space above each item line in a day cell, in pixels (0-8).</summary>
    public int LineGap { get; init; } = 1;
    public bool CompleteSound { get; init; } = true;
    public bool ReminderSound { get; init; } = true;

    // ---- layout ----
    public DisplayMode Mode { get; init; } = DisplayMode.FixedMonth;
    public DayOfWeek FirstDayOfWeek { get; init; } = DayOfWeek.Sunday;
    /// <summary>Weeks shown by <see cref="DisplayMode.FloatingMonth"/> (2-8).</summary>
    public int MaxRows { get; init; } = 5;
    /// <summary>Weeks before the anchor's week that <see cref="DisplayMode.FloatingMonth"/> also shows (0 to MaxRows-1).</summary>
    public int PastWeeks { get; init; }
    /// <summary>Days shown by <see cref="DisplayMode.SingleLineFloating"/> (7-31).</summary>
    public int LineDays { get; init; } = 14;
    public bool ShowWeekNumbers { get; init; }
    public WeekNumberRule WeekRule { get; init; } = WeekNumberRule.Iso;
    /// <summary>Bit i set = DayOfWeek i is a rest day (Sunday = bit 0).</summary>
    public int OffDaysMask { get; init; } = (1 << (int)DayOfWeek.Saturday) | (1 << (int)DayOfWeek.Sunday);
    public bool ShowOffDays { get; init; } = true;

    // ---- appearance ----
    public string FontFamily { get; init; } = "Segoe UI, Malgun Gothic";
    public double FontSize { get; init; } = 15;
    public string TextColor { get; init; } = "#FFFFFF";
    public string OffDayColor { get; init; } = "#FF7A7A";
    public string BackgroundColor { get; init; } = "#101820";
    /// <summary>Background opacity 0.0-1.0.</summary>
    public double BackgroundOpacity { get; init; } = 0.6;
    public bool Shadow { get; init; } = true;
    public int CellGap { get; init; } = 2;

    // ---- background photo ("skin") ----
    /// <summary>Folder of pictures to use behind the calendar; empty = none (plain colour).</summary>
    public string SkinFolder { get; init; } = "";
    public SkinFit SkinFit { get; init; } = SkinFit.Fill;
    /// <summary>Change picture every N minutes; 0 = always the first picture.</summary>
    public int SkinRotateMinutes { get; init; }

    public bool IsOffDay(DayOfWeek d) => (OffDaysMask & (1 << (int)d)) != 0;

    /// <summary>Clamp values that would break layout or rendering.</summary>
    public BoardSettings Sanitized() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : DisplayMode.FixedMonth,
        FirstDayOfWeek = Enum.IsDefined(FirstDayOfWeek) ? FirstDayOfWeek : DayOfWeek.Sunday,
        MaxRows = Math.Clamp(MaxRows, 2, 8),
        PastWeeks = Math.Clamp(PastWeeks, 0, Math.Clamp(MaxRows, 2, 8) - 1),
        LineGap = Math.Clamp(LineGap, 0, 8),
        LineDays = Math.Clamp(LineDays, 7, 31),
        WeekRule = Enum.IsDefined(WeekRule) ? WeekRule : WeekNumberRule.Iso,
        Bullet = Enum.IsDefined(Bullet) ? Bullet : BulletStyle.Dot,
        OffDaysMask = OffDaysMask & 0x7F,
        FontSize = Math.Clamp(FontSize, 8, 48),
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0.0, 1.0),
        CellGap = Math.Clamp(CellGap, 0, 12),
        SkinFolder = SkinFolder ?? "",
        SkinFit = Enum.IsDefined(SkinFit) ? SkinFit : SkinFit.Fill,
        SkinRotateMinutes = SkinRotateMinutes <= 0 ? 0 : Math.Clamp(SkinRotateMinutes, 5, 1440),
    };
}
