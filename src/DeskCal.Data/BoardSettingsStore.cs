using System.Globalization;
using DeskCal.Core;

namespace DeskCal.Data;

/// <summary>Loads/saves <see cref="BoardSettings"/> as namespaced keys ("board.*") in the settings table.</summary>
public sealed class BoardSettingsStore(SettingsStore store)
{
    public const string Prefix = "board.";

    public BoardSettings Load()
    {
        var d = new BoardSettings();   // defaults
        var s = d with
        {
            ShowLunar = store.GetBool(Prefix + "show_lunar", d.ShowLunar),
            ShowSolarTerms = store.GetBool(Prefix + "show_terms", d.ShowSolarTerms),
            ShowHolidays = store.GetBool(Prefix + "show_holidays", d.ShowHolidays),
            ShowFestivals = store.GetBool(Prefix + "show_festivals", d.ShowFestivals),
            HolidayColor = store.GetString(Prefix + "holiday_color") ?? d.HolidayColor,
            HolidayBgColor = store.GetString(Prefix + "holiday_bg_color") ?? d.HolidayBgColor,
            TermColor = store.GetString(Prefix + "term_color") ?? d.TermColor,
            Bullet = Enum(store, "bullet", d.Bullet),
            ShowCompleted = store.GetBool(Prefix + "show_completed", d.ShowCompleted),
            MoveDoneToBottom = store.GetBool(Prefix + "done_to_bottom", d.MoveDoneToBottom),
            ShowReminderMark = store.GetBool(Prefix + "reminder_mark", d.ShowReminderMark),
            LineGap = store.GetInt(Prefix + "line_gap", d.LineGap),
            PastWeeks = store.GetInt(Prefix + "past_weeks", d.PastWeeks),
            CompleteSound = store.GetBool(Prefix + "complete_sound", d.CompleteSound),
            ReminderSound = store.GetBool(Prefix + "reminder_sound", d.ReminderSound),
            BugiAlert = store.GetBool(Prefix + "bugi_alert", d.BugiAlert),
            Mode = Enum(store, "mode", d.Mode),
            FirstDayOfWeek = Enum(store, "first_dow", d.FirstDayOfWeek),
            MaxRows = store.GetInt(Prefix + "max_rows", d.MaxRows),
            LineDays = store.GetInt(Prefix + "line_days", d.LineDays),
            ShowWeekNumbers = store.GetBool(Prefix + "week_numbers", d.ShowWeekNumbers),
            WeekRule = Enum(store, "week_rule", d.WeekRule),
            OffDaysMask = store.GetInt(Prefix + "off_days", d.OffDaysMask),
            ShowOffDays = store.GetBool(Prefix + "show_off_days", d.ShowOffDays),
            FontFamily = store.GetString(Prefix + "font") ?? d.FontFamily,
            FontSize = Double(store, "font_size", d.FontSize),
            TextColor = store.GetString(Prefix + "text_color") ?? d.TextColor,
            OffDayColor = store.GetString(Prefix + "off_color") ?? d.OffDayColor,
            BackgroundColor = store.GetString(Prefix + "bg_color") ?? d.BackgroundColor,
            BackgroundOpacity = Double(store, "bg_opacity", d.BackgroundOpacity),
            Shadow = store.GetBool(Prefix + "shadow", d.Shadow),
            CellGap = store.GetInt(Prefix + "cell_gap", d.CellGap),
            SkinFolder = store.GetString(Prefix + "skin_folder") ?? d.SkinFolder,
            SkinFit = Enum(store, "skin_fit", d.SkinFit),
            SkinRotateMinutes = store.GetInt(Prefix + "skin_rotate", d.SkinRotateMinutes),
        };
        return s.Sanitized();
    }

    public void Save(BoardSettings settings)
    {
        var s = settings.Sanitized();
        store.SetBool(Prefix + "show_lunar", s.ShowLunar);
        store.SetBool(Prefix + "show_terms", s.ShowSolarTerms);
        store.SetBool(Prefix + "show_holidays", s.ShowHolidays);
        store.SetBool(Prefix + "show_festivals", s.ShowFestivals);
        store.SetString(Prefix + "holiday_color", s.HolidayColor);
        store.SetString(Prefix + "holiday_bg_color", s.HolidayBgColor);
        store.SetString(Prefix + "term_color", s.TermColor);
        store.SetString(Prefix + "bullet", s.Bullet.ToString());
        store.SetBool(Prefix + "show_completed", s.ShowCompleted);
        store.SetBool(Prefix + "done_to_bottom", s.MoveDoneToBottom);
        store.SetBool(Prefix + "reminder_mark", s.ShowReminderMark);
        store.SetInt(Prefix + "line_gap", s.LineGap);
        store.SetInt(Prefix + "past_weeks", s.PastWeeks);
        store.SetBool(Prefix + "complete_sound", s.CompleteSound);
        store.SetBool(Prefix + "reminder_sound", s.ReminderSound);
        store.SetBool(Prefix + "bugi_alert", s.BugiAlert);
        store.SetString(Prefix + "mode", s.Mode.ToString());
        store.SetString(Prefix + "first_dow", s.FirstDayOfWeek.ToString());
        store.SetInt(Prefix + "max_rows", s.MaxRows);
        store.SetInt(Prefix + "line_days", s.LineDays);
        store.SetBool(Prefix + "week_numbers", s.ShowWeekNumbers);
        store.SetString(Prefix + "week_rule", s.WeekRule.ToString());
        store.SetInt(Prefix + "off_days", s.OffDaysMask);
        store.SetBool(Prefix + "show_off_days", s.ShowOffDays);
        store.SetString(Prefix + "font", s.FontFamily);
        store.SetString(Prefix + "font_size", s.FontSize.ToString("R", CultureInfo.InvariantCulture));
        store.SetString(Prefix + "text_color", s.TextColor);
        store.SetString(Prefix + "off_color", s.OffDayColor);
        store.SetString(Prefix + "bg_color", s.BackgroundColor);
        store.SetString(Prefix + "bg_opacity", s.BackgroundOpacity.ToString("R", CultureInfo.InvariantCulture));
        store.SetBool(Prefix + "shadow", s.Shadow);
        store.SetInt(Prefix + "cell_gap", s.CellGap);
        store.SetString(Prefix + "skin_folder", s.SkinFolder);
        store.SetString(Prefix + "skin_fit", s.SkinFit.ToString());
        store.SetInt(Prefix + "skin_rotate", s.SkinRotateMinutes);
    }

    /// <summary>"Restore defaults" for every appearance/layout key. Window geometry (board.x/y/w/h) is kept.</summary>
    public void ResetToDefaults()
    {
        string[] geometry = ["board.x", "board.y", "board.w", "board.h"];
        foreach (var key in store.GetAll(Prefix).Keys)
            if (!geometry.Contains(key)) store.Remove(key);
    }

    private static T Enum<T>(SettingsStore st, string key, T fallback) where T : struct, Enum =>
        System.Enum.TryParse<T>(st.GetString(Prefix + key), ignoreCase: true, out var v) && System.Enum.IsDefined(v) ? v : fallback;

    private static double Double(SettingsStore st, string key, double fallback) =>
        double.TryParse(st.GetString(Prefix + key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;
}
