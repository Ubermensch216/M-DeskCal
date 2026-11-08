using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DeskCal.Core;
using DeskCal.Data;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;

namespace DeskCal.App;

/// <summary>Create or edit one event: title, time span, repeat rule and reminder.</summary>
public partial class EventEditorWindow : Window
{
    private static readonly (string Label, RepeatKind Kind)[] Repeats =
    [
        ("반복 안 함", RepeatKind.None), ("매일", RepeatKind.Daily), ("매주", RepeatKind.Weekly), ("매월", RepeatKind.Monthly),
        ("매년", RepeatKind.Yearly), ("평일 (월~금)", RepeatKind.Weekday), ("매월 말일", RepeatKind.MonthLastDay),
        ("분기 말일 (3·6·9·12월)", RepeatKind.SeasonLastDay), ("법정 근무일 (공휴일 제외)", RepeatKind.LegalWorkDay),
        ("음력 매년", RepeatKind.LunarYearly), ("에빙하우스 망각곡선 (+1·2·4·7·15·30일)", RepeatKind.Ebbinghaus),
    ];

    private static readonly (string Label, int? Offset)[] ReminderPresets =
    [
        ("알림 없음", null), ("시작 시각", 0), ("5분 전", 5), ("10분 전", 10), ("15분 전", 15), ("30분 전", 30),
        ("1시간 전", 60), ("2시간 전", 120), ("1일 전", 1440), ("2일 전", 2880), ("3일 전", 4320), ("5일 전", 7200), ("1주 전", 10080),
    ];

    private readonly EventSeriesService _svc;
    private readonly string? _eventId;
    private readonly DateOnly _occurrence;          // the occurrence being edited, or the date of a new event
    private readonly DateOnly _seriesStart;
    private readonly bool _wasRepeating;
    private readonly string? _color;
    private readonly List<(string Label, int? Offset)> _reminderItems = [..ReminderPresets];
    private readonly IReadOnlyList<ReminderSpec> _extraReminders = [];
    private readonly CheckBox[] _weekdayBoxes = new CheckBox[7];
    private bool _loading = true;

    /// <summary>True when something was created, changed or deleted.</summary>
    public bool Changed { get; private set; }

    public EventEditorWindow(EventSeriesService svc, string? eventId, DateOnly occurrence)
    {
        InitializeComponent();
        _svc = svc; _eventId = eventId; _occurrence = occurrence;

        foreach (var (label, _) in Repeats) RepeatBox.Items.Add(label);
        foreach (var label in new[] { "계속", "횟수", "종료일" }) EndKindBox.Items.Add(label);
        MonthlyModeBox.Items.Add(""); MonthlyModeBox.Items.Add("");
        string[] names = ["일", "월", "화", "수", "목", "금", "토"];
        for (int i = 0; i < 7; i++)
        {
            var box = new CheckBox { Content = names[i], Margin = new Thickness(0, 0, 10, 0), Tag = i };
            _weekdayBoxes[i] = box;
            WeekdayRow.Children.Add(box);
        }

        EventDraft? draft = null;
        if (eventId is not null)
        {
            draft = svc.Load(eventId);
            if (draft is null) { Title = "일정"; _loading = false; Loaded += (_, _) => Close(); return; }
        }

        if (draft is null)
        {
            Title = "새 일정";
            StartDate.SelectedDate = occurrence.ToDateTime(TimeOnly.MinValue);
            var h = DateTime.Now.Hour < 23 ? DateTime.Now.Hour + 1 : 23;
            StartTime.Text = $"{h:00}:00";
            EndTime.Text = $"{Math.Min(h + 1, 23):00}:00";
            RepeatBox.SelectedIndex = 0;
            EndKindBox.SelectedIndex = 0;
            ReminderBox_Fill(null);
            DeleteBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            Title = "일정 수정";
            _seriesStart = draft.StartDate;
            _wasRepeating = draft.Rule.IsRepeating;
            _color = draft.Color;
            int shift = occurrence.DayNumber - draft.StartDate.DayNumber;      // show the occurrence, not the series' first day

            TitleBox.Text = draft.Title;
            BodyBox.Text = draft.Body;
            StartDate.SelectedDate = draft.StartDate.AddDays(shift).ToDateTime(TimeOnly.MinValue);
            AllDayBox.IsChecked = draft.AllDay;
            StartTime.Text = draft.StartTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "09:00";
            EndTime.Text = draft.EndTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
            EndDate.SelectedDate = draft.EndDate?.AddDays(shift).ToDateTime(TimeOnly.MinValue);
            LoadRule(draft.Rule);
            _extraReminders = draft.Reminders.Skip(1).ToList();
            ReminderBox_Fill(draft.Reminders.FirstOrDefault());
        }

        _loading = false;
        RefreshMonthlyLabels();
        UpdateUi();
        TitleBox.Focus();
    }

    // ---- loading ---------------------------------------------------------------

    private void LoadRule(RecurrenceRule rule)
    {
        RepeatBox.SelectedIndex = Array.FindIndex(Repeats, r => r.Kind == rule.Kind);
        IntervalBox.Text = rule.Interval.ToString(CultureInfo.InvariantCulture);
        int mask = rule.WeekdaysMask != 0 ? rule.WeekdaysMask : 1 << (int)(StartDate.SelectedDate?.DayOfWeek ?? DayOfWeek.Monday);
        for (int i = 0; i < 7; i++) _weekdayBoxes[i].IsChecked = (mask & (1 << i)) != 0;
        MonthlyModeBox.SelectedIndex = rule.Monthly == MonthlyMode.ByWeekday ? 1 : 0;
        EndKindBox.SelectedIndex = rule.Count is not null ? 1 : rule.Until is not null ? 2 : 0;
        CountBox.Text = (rule.Count ?? 10).ToString(CultureInfo.InvariantCulture);
        UntilDate.SelectedDate = rule.Until?.ToDateTime(TimeOnly.MinValue);
    }

    private void ReminderBox_Fill(ReminderSpec? current)
    {
        if (current is not null && !_reminderItems.Any(i => i.Offset == current.OffsetMinutes))
            _reminderItems.Add((ReminderText.DescribeOffset(current.OffsetMinutes), current.OffsetMinutes));
        ReminderBox.Items.Clear();
        foreach (var (label, _) in _reminderItems) ReminderBox.Items.Add(label);
        ReminderBox.SelectedIndex = current is null ? 0 : _reminderItems.FindIndex(i => i.Offset == current.OffsetMinutes);
        if (current?.CustomTime is { } t) ReminderClock.Text = t;
        ReminderBox.SelectionChanged += (_, _) => UpdateUi();
    }

    // ---- UI state --------------------------------------------------------------

    private RepeatKind SelectedKind => RepeatBox.SelectedIndex >= 0 ? Repeats[RepeatBox.SelectedIndex].Kind : RepeatKind.None;

    private static bool UsesInterval(RepeatKind k) =>
        k is RepeatKind.Daily or RepeatKind.Weekly or RepeatKind.Monthly or RepeatKind.Yearly or RepeatKind.MonthLastDay or RepeatKind.LunarYearly;

    private void UpdateUi()
    {
        if (_loading) return;
        var kind = SelectedKind;
        bool allDay = AllDayBox.IsChecked == true;

        StartTime.IsEnabled = EndTime.IsEnabled = !allDay;
        IntervalRow.Visibility = UsesInterval(kind) ? Visibility.Visible : Visibility.Collapsed;
        IntervalUnit.Text = kind switch
        {
            RepeatKind.Daily => "일마다", RepeatKind.Weekly => "주마다", RepeatKind.Monthly => "개월마다",
            RepeatKind.Yearly => "년마다", RepeatKind.MonthLastDay => "개월마다 (월말)", RepeatKind.LunarYearly => "년마다 (음력)", _ => "",
        };
        WeekdayRow.Visibility = kind == RepeatKind.Weekly ? Visibility.Visible : Visibility.Collapsed;
        MonthlyModeBox.Visibility = kind == RepeatKind.Monthly ? Visibility.Visible : Visibility.Collapsed;
        EndRow.Visibility = kind is RepeatKind.None or RepeatKind.Ebbinghaus ? Visibility.Collapsed : Visibility.Visible;
        bool count = EndKindBox.SelectedIndex == 1, until = EndKindBox.SelectedIndex == 2;
        CountBox.Visibility = CountUnit.Visibility = count ? Visibility.Visible : Visibility.Collapsed;
        UntilDate.Visibility = until ? Visibility.Visible : Visibility.Collapsed;
        ReminderClockRow.Visibility = allDay && ReminderBox.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshMonthlyLabels()
    {
        if (StartDate.SelectedDate is not { } d) return;
        var date = DateOnly.FromDateTime(d);
        int keep = Math.Max(0, MonthlyModeBox.SelectedIndex);
        int nth = (date.Day - 1) / 7 + 1;
        string[] dow = ["일", "월", "화", "수", "목", "금", "토"];
        string weekday = nth >= 5 ? $"마지막 {dow[(int)date.DayOfWeek]}요일" : $"{nth}번째 {dow[(int)date.DayOfWeek]}요일";
        MonthlyModeBox.Items[0] = $"매월 {date.Day}일 (31일은 말일로)";
        MonthlyModeBox.Items[1] = $"매월 {weekday}";
        MonthlyModeBox.SelectedIndex = keep;
    }

    private void Repeat_Changed(object sender, SelectionChangedEventArgs e) => UpdateUi();
    private void EndKind_Changed(object sender, SelectionChangedEventArgs e) => UpdateUi();
    private void AllDay_Click(object sender, RoutedEventArgs e) => UpdateUi();
    private void Date_Changed(object? sender, SelectionChangedEventArgs e) { if (!_loading) RefreshMonthlyLabels(); }

    // ---- building the draft ----------------------------------------------------

    private bool TryBuild(out EventDraft draft, out string error)
    {
        draft = null!; error = "";
        if (StartDate.SelectedDate is not { } sdt) { error = "시작 날짜를 선택하세요."; return false; }
        var sd = DateOnly.FromDateTime(sdt);
        bool allDay = AllDayBox.IsChecked == true;

        TimeOnly? st = null, et = null;
        if (!allDay)
        {
            if (!ReminderPlanner.TryParseClock(StartTime.Text, out var s)) { error = "시작 시간을 HH:mm 형식으로 입력하세요. (예: 09:30)"; return false; }
            st = s;
            if (!string.IsNullOrWhiteSpace(EndTime.Text))
            {
                if (!ReminderPlanner.TryParseClock(EndTime.Text, out var e)) { error = "종료 시간을 HH:mm 형식으로 입력하세요."; return false; }
                et = e;
            }
        }
        DateOnly? ed = EndDate.SelectedDate is { } edt ? DateOnly.FromDateTime(edt) : null;

        var kind = SelectedKind;
        int interval = 1;
        if (UsesInterval(kind) && (!int.TryParse(IntervalBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval < 1 || interval > 999))
        { error = "반복 간격은 1~999 사이의 숫자여야 합니다."; return false; }

        int mask = 0;
        for (int i = 0; i < 7; i++) if (_weekdayBoxes[i].IsChecked == true) mask |= 1 << i;
        if (kind == RepeatKind.Weekly && mask == 0) { error = "반복할 요일을 하나 이상 선택하세요."; return false; }

        int? count = null; DateOnly? until = null;
        if (kind is not (RepeatKind.None or RepeatKind.Ebbinghaus))
        {
            if (EndKindBox.SelectedIndex == 1)
            {
                if (!int.TryParse(CountBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int c) || c < 1) { error = "반복 횟수는 1 이상의 숫자여야 합니다."; return false; }
                count = c;
            }
            else if (EndKindBox.SelectedIndex == 2)
            {
                if (UntilDate.SelectedDate is not { } u) { error = "반복 종료일을 선택하세요."; return false; }
                until = DateOnly.FromDateTime(u);
            }
        }

        var rule = new RecurrenceRule
        {
            Kind = kind, Interval = UsesInterval(kind) ? interval : 1,
            WeekdaysMask = kind == RepeatKind.Weekly ? mask : 0,
            Monthly = kind == RepeatKind.Monthly && MonthlyModeBox.SelectedIndex == 1 ? MonthlyMode.ByWeekday : MonthlyMode.ByDate,
            Count = count, Until = until,
        };

        var reminders = new List<ReminderSpec>();
        if (ReminderBox.SelectedIndex > 0 && _reminderItems[ReminderBox.SelectedIndex].Offset is { } offset)
        {
            string? custom = null;
            if (allDay)
            {
                if (!ReminderPlanner.TryParseClock(ReminderClock.Text, out var rc)) { error = "알림 시각을 HH:mm 형식으로 입력하세요."; return false; }
                custom = rc.ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            reminders.Add(new ReminderSpec(offset, custom));
        }
        reminders.AddRange(_extraReminders);

        draft = new EventDraft(TitleBox.Text.Trim(), BodyBox.Text, sd, st, ed, et, rule, _color, reminders);
        var problems = draft.Validate();
        if (problems.Count > 0) { error = string.Join(" ", problems); return false; }
        return true;
    }

    internal CheckBox[] WeekdayBoxes => _weekdayBoxes;

    // ---- buttons ---------------------------------------------------------------

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    /// <summary>Validate and store. Returns false (with the reason in the error line) when nothing was saved.</summary>
    internal bool Save()
    {
        if (!TryBuild(out var draft, out var error)) { ErrorText.Text = error; return false; }
        try
        {
            if (_eventId is null) _svc.Create(draft);
            else
            {
                var scope = _wasRepeating ? ScopeDialog.Ask(this, "수정") : SeriesScope.All;
                if (scope == SeriesScope.Cancel) return false;
                switch (scope)
                {
                    case SeriesScope.All:
                        // The form shows the occurrence's date; the series keeps its own start unless the user moved it.
                        int delta = _seriesStart.DayNumber - _occurrence.DayNumber;
                        _svc.UpdateAll(_eventId, draft with
                        {
                            StartDate = draft.StartDate.AddDays(delta),
                            EndDate = draft.EndDate?.AddDays(delta),
                        });
                        break;
                    case SeriesScope.This: _svc.UpdateThis(_eventId, _occurrence, draft); break;
                    case SeriesScope.Future: _svc.UpdateFuture(_eventId, _occurrence, draft); break;
                }
            }
            Changed = true;
            Close();
            return true;
        }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; return false; }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_eventId is null) return;
        if (_wasRepeating)
        {
            var scope = ScopeDialog.Ask(this, "삭제");
            if (scope == SeriesScope.Cancel) return;
            if (scope == SeriesScope.This) _svc.DeleteThis(_eventId, _occurrence);
            else if (scope == SeriesScope.Future) _svc.DeleteFuture(_eventId, _occurrence);
            else _svc.DeleteAll(_eventId);
        }
        else
        {
            if (MessageBox.Show(this, "이 일정을 삭제할까요?", "일정 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _svc.DeleteAll(_eventId);
        }
        Changed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
