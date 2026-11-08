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
            Bullet = Enum(store, "bullet", d.Bullet),
            ShowCompleted = store.GetBool(Prefix + "show_completed", d.ShowCompleted),
            CompleteSound = store.GetBool(Prefix + "complete_sound", d.CompleteSound),
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
        };
        return s.Sanitized();
    }

    public void Save(BoardSettings settings)
    {
        var s = settings.Sanitized();
        store.SetString(Prefix + "bullet", s.Bullet.ToString());
        store.SetBool(Prefix + "show_completed", s.ShowCompleted);
        store.SetBool(Prefix + "complete_sound", s.CompleteSound);
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
