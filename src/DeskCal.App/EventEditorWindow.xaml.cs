using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskCal.Core;
using DeskCal.Data;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;

namespace DeskCal.App;

/// <summary>Create or edit one event: title, done state, colours, time span, repeat rule, reminder, D-day countdown and memo.</summary>
public partial class EventEditorWindow : Window
{
    private static readonly (string Label, string ShortLabel, RepeatKind Kind)[] Repeats =
    [
        ("반복 안 함", "반복", RepeatKind.None),
        ("매일", "매일", RepeatKind.Daily),
        ("매주", "매주", RepeatKind.Weekly),
        ("매월", "매월", RepeatKind.Monthly),
        ("매년", "매년", RepeatKind.Yearly),
        ("평일 (월~금)", "평일", RepeatKind.Weekday),
        ("매월 말일", "월말", RepeatKind.MonthLastDay),
        ("분기 말일 (3·6·9·12월)", "분기말", RepeatKind.SeasonLastDay),
        ("법정 근무일 (공휴일 제외)", "근무일", RepeatKind.LegalWorkDay),
        ("음력 매년", "음력", RepeatKind.LunarYearly),
        ("에빙하우스 망각곡선 (+1·2·4·7·15·30일)", "복습", RepeatKind.Ebbinghaus),
    ];

    private static readonly int[] QuickRepeatIndices = [0, 1, 2, 3, 4, 5];

    private static readonly (string Label, string ShortLabel, int? Offset)[] ReminderPresets =
    [
        ("알림 없음", "알림", null),
        ("시작 시각", "정각", 0),
        ("5분 전", "5분 전", 5),
        ("10분 전", "10분 전", 10),
        ("15분 전", "15분 전", 15),
        ("30분 전", "30분 전", 30),
        ("1시간 전", "1시간", 60),
        ("2시간 전", "2시간", 120),
        ("1일 전", "1일 전", 1440),
        ("2일 전", "2일 전", 2880),
        ("3일 전", "3일 전", 4320),
        ("5일 전", "5일 전", 7200),
        ("1주 전", "1주 전", 10080),
    ];

    private static readonly int[] QuickReminderIndices = [0, 1, 3, 5, 6, 8];

    private static readonly (CountdownType Type, string ShortLabel, string IconPath, string Tooltip)[] DdayTypes =
    [
        (CountdownType.Custom, "일반", "M4 22V4M4 4h12l-2 4 2 4H4", "일반 D-day (D-12 / D-Day / D+3)"),
        (CountdownType.Exam, "시험", "M4 19.5A2.5 2.5 0 0 1 6.5 17H20M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z", "시험 D-day (D-12 / D-Day / D+3)"),
        (CountdownType.Birthday, "생일", "M20 12v10H4V12M2 7h20v5H2zM12 22V7M12 7H7.5a2.5 2.5 0 1 1 0-5C11 2 12 7 12 7zM12 7h4.5a2.5 2.5 0 1 0 0-5C13 2 12 7 12 7z", "생일 (매년 반복, 나이 표시)"),
        (CountdownType.Festival, "기념일", "M12 3l1.5 4.5L18 9l-4.5 1.5L12 15l-1.5-4.5L6 9l4.5-1.5L12 3z", "명절·기념일 (매년 반복)"),
        (CountdownType.Memorial, "시작일", "M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z", "만난 날·시작일 (N일째, N주년)"),
    ];

    private readonly EventSeriesService _svc;
    private readonly CountdownRepository? _countdowns;
    private readonly SettingsStore? _store;
    private readonly Func<DateOnly, string?>? _getCellBg;
    private readonly Action<DateOnly, string?>? _setCellBg;
    private readonly string? _eventId;
    private readonly DateOnly _occurrence;          // the occurrence being edited, or the date of a new event
    private readonly DateOnly _seriesStart;
    private readonly bool _wasRepeating;
    private readonly List<(string Label, string ShortLabel, int? Offset)> _reminderItems = [..ReminderPresets];
    private readonly IReadOnlyList<ReminderSpec> _extraReminders = [];
    private readonly CheckBox[] _weekdayBoxes = new CheckBox[7];
    private readonly List<(Button Btn, int Index)> _repeatChips = [];
    private readonly List<(Button Btn, int Index)> _reminderChips = [];
    private readonly List<(Border Tile, Path Icon, TextBlock Label, CountdownType Type)> _ddayTypeTiles = [];

    private bool _loading = true;
    private bool _done;
    private string? _color;
    private string? _cellBg;
    private bool _cellBgDirty;
    private bool _repeatExpanded;
    private bool _reminderExpanded;
    private bool _ddayEnabled;
    private CountdownType _ddayType = CountdownType.Custom;
    private string? _existingCountdownId;
    private bool _existingCountdownLunar;
    private string? _existingCountdownDate;

    /// <summary>True when something was created, changed or deleted.</summary>
    public bool Changed { get; private set; }

    /// <summary>True when a linked D-day countdown was created, updated or removed.</summary>
    public bool CountdownChanged { get; private set; }

    public EventEditorWindow(EventSeriesService svc, string? eventId, DateOnly occurrence,
                             CountdownRepository? countdowns = null, SettingsStore? store = null,
                             Func<DateOnly, string?>? getCellBg = null, Action<DateOnly, string?>? setCellBg = null)
    {
        InitializeComponent();
        _svc = svc; _eventId = eventId; _occurrence = occurrence;
        _countdowns = countdowns; _store = store;
        _getCellBg = getCellBg; _setCellBg = setCellBg;

        foreach (var (label, _, _) in Repeats) RepeatBox.Items.Add(label);
        foreach (var label in new[] { "계속", "횟수", "종료일" }) EndKindBox.Items.Add(label);
        MonthlyModeBox.Items.Add(""); MonthlyModeBox.Items.Add("");

        BuildWeekdayCircles();
        BuildQuickRepeatChips();
        BuildQuickReminderChips();
        BuildDdayTypeTiles();

        EventDraft? draft = null;
        if (eventId is not null)
        {
            draft = svc.Load(eventId);
            if (draft is null) { Title = "일정"; _loading = false; Loaded += (_, _) => Close(); return; }
        }

        if (draft is null)
        {
            Title = "새 일정";
            HeaderTitle.Text = "새 일정";
            StartDate.SelectedDate = occurrence.ToDateTime(TimeOnly.MinValue);
            var h = DateTime.Now.Hour < 23 ? DateTime.Now.Hour + 1 : 23;
            StartTime.Text = $"{h:00}:00";
            EndTime.Text = $"{Math.Min(h + 1, 23):00}:00";
            RepeatBox.SelectedIndex = 0;
            EndKindBox.SelectedIndex = 0;
            ReminderBox_Fill(null);
            DeleteBtn.Visibility = Visibility.Collapsed;
            _cellBg = _getCellBg?.Invoke(occurrence);
        }
        else
        {
            Title = "일정 수정";
            HeaderTitle.Text = "일정 수정";
            HeaderIconPath.Data = Geometry.Parse("M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7M18.5 2.5a2.12 2.12 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z");
            _seriesStart = draft.StartDate;
            _wasRepeating = draft.Rule.IsRepeating;
            _color = draft.Color;
            _done = draft.Done;
            int shift = occurrence.DayNumber - draft.StartDate.DayNumber;      // show the occurrence, not the series' first day
            var shownStart = draft.StartDate.AddDays(shift);

            TitleBox.Text = draft.Title;
            BodyBox.Text = draft.Body;
            StartDate.SelectedDate = shownStart.ToDateTime(TimeOnly.MinValue);
            AllDayBox.IsChecked = draft.AllDay;
            StartTime.Text = draft.StartTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "09:00";
            EndTime.Text = draft.EndTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
            EndDate.SelectedDate = draft.EndDate?.AddDays(shift).ToDateTime(TimeOnly.MinValue);
            LoadRule(draft.Rule);
            _repeatExpanded = draft.Rule.IsRepeating;
            _extraReminders = draft.Reminders.Skip(1).ToList();
            ReminderBox_Fill(draft.Reminders.FirstOrDefault());
            _reminderExpanded = draft.Reminders.Count > 0;
            _cellBg = _getCellBg?.Invoke(shownStart);

            if (!string.IsNullOrEmpty(draft.CountdownId))
            {
                _existingCountdownId = draft.CountdownId;
                var cd = _countdowns?.List().FirstOrDefault(c => c.Id == draft.CountdownId);
                if (cd is not null)
                {
                    _ddayEnabled = true;
                    _ddayType = cd.Type;
                    _existingCountdownLunar = cd.Lunar;
                    _existingCountdownDate = cd.Date;
                    DdayShowCardBox.IsChecked = cd.ShowCard;
                    DdayShowBottomBox.IsChecked = cd.ShowBottom;
                }
            }
        }

        _loading = false;
        RefreshMonthlyLabels();
        UpdateUi();
        TitleBox.Focus();
    }

    // ---- dynamic control builders ----------------------------------------------

    private void BuildWeekdayCircles()
    {
        var style = (Style)FindResource("WeekdayCircle");
        string[] names = ["일", "월", "화", "수", "목", "금", "토"];
        for (int i = 0; i < 7; i++)
        {
            var box = new CheckBox { Content = names[i], Style = style, Tag = i };
            if (i == 0) box.Foreground = UiColors.Frozen(Color.FromRgb(0xDC, 0x26, 0x26));
            else if (i == 6) box.Foreground = UiColors.Frozen(Color.FromRgb(0x25, 0x63, 0xEB));
            _weekdayBoxes[i] = box;
            WeekdayRow.Children.Add(box);
        }
    }

    private void BuildQuickRepeatChips()
    {
        var style = (Style)FindResource("QuickChip");
        foreach (int idx in QuickRepeatIndices)
        {
            string label = idx == 0 ? "안 함" : Repeats[idx].ShortLabel;
            var btn = new Button { Content = label, Style = style };
            int captured = idx;
            btn.Click += (_, _) => { RepeatBox.SelectedIndex = captured; };
            _repeatChips.Add((btn, captured));
            RepeatChips.Children.Add(btn);
        }
    }

    private void BuildQuickReminderChips()
    {
        var style = (Style)FindResource("QuickChip");
        foreach (int idx in QuickReminderIndices)
        {
            string label = idx == 0 ? "없음" : ReminderPresets[idx].ShortLabel;
            var btn = new Button { Content = label, Style = style };
            int captured = idx;
            btn.Click += (_, _) => { ReminderBox.SelectedIndex = captured; };
            _reminderChips.Add((btn, captured));
            ReminderChips.Children.Add(btn);
        }
    }

    private void BuildDdayTypeTiles()
    {
        foreach (var (type, shortLabel, iconPath, tip) in DdayTypes)
        {
            var path = new Path
            {
                Data = Geometry.Parse(iconPath),
                Stroke = ModernUi.MutedBrush,
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
                Width = 24,
                Height = 24,
            };
            var lbl = new TextBlock
            {
                Text = shortLabel,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = ModernUi.MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new Viewbox { Width = 16, Height = 16, Margin = new Thickness(0, 0, 0, 3), Child = path });
            stack.Children.Add(lbl);

            var tile = new Border
            {
                Background = ModernUi.CardAltBrush,
                BorderBrush = ModernUi.LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(4, 6, 4, 6),
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Child = stack,
            };
            var captured = type;
            tile.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _ddayType = captured;
                UpdateDdayVisuals();
            };
            _ddayTypeTiles.Add((tile, path, lbl, type));
            DdayTypeGrid.Children.Add(tile);
        }
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
        {
            var desc = ReminderText.DescribeOffset(current.OffsetMinutes);
            _reminderItems.Add((desc, desc, current.OffsetMinutes));
        }
        ReminderBox.Items.Clear();
        foreach (var (label, _, _) in _reminderItems) ReminderBox.Items.Add(label);
        ReminderBox.SelectedIndex = current is null ? 0 : _reminderItems.FindIndex(i => i.Offset == current.OffsetMinutes);
        if (current?.CustomTime is { } t) ReminderClock.Text = t;
        ReminderBox.SelectionChanged += (_, _) =>
        {
            if (!_loading && ReminderBox.SelectedIndex > 0) _reminderExpanded = true;
            UpdateUi();
        };
    }

    // ---- UI state --------------------------------------------------------------

    private RepeatKind SelectedKind => RepeatBox.SelectedIndex >= 0 ? Repeats[RepeatBox.SelectedIndex].Kind : RepeatKind.None;

    private DateOnly CurrentStartDate => StartDate.SelectedDate is { } d ? DateOnly.FromDateTime(d) : _occurrence;

    private void UpdateUi()
    {
        if (_loading) return;
        var kind = SelectedKind;
        bool allDay = AllDayBox.IsChecked == true;
        bool hasRepeat = kind != RepeatKind.None;
        bool hasReminder = ReminderBox.SelectedIndex > 0;

        // Date/Time controls
        StartTime.IsEnabled = EndTime.IsEnabled = !allDay;
        ClearEndDateBtn.Visibility = EndDate.SelectedDate is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeaderAndDuration(allDay);

        // Placeholders
        TitlePlaceholder.Visibility = string.IsNullOrEmpty(TitleBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        BodyPlaceholder.Visibility = string.IsNullOrEmpty(BodyBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        // Done visuals
        UpdateDoneVisuals();

        // Color visuals
        UpdateColorVisuals();

        // Repeat panel & tile
        RepeatCard.Visibility = _repeatExpanded ? Visibility.Visible : Visibility.Collapsed;
        IntervalRow.Visibility = kind.UsesInterval() ? Visibility.Visible : Visibility.Collapsed;
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
        UntilDateWrap.Visibility = until ? Visibility.Visible : Visibility.Collapsed;

        StyleTile(OptRepeatBtn, OptRepeatIcon, OptRepeatLabel,
            active: hasRepeat || _repeatExpanded,
            highlight: hasRepeat,
            activeBg: Color.FromRgb(0xEF, 0xF6, 0xFF),
            activeBorder: Color.FromRgb(0x93, 0xC5, 0xFD),
            activeFg: Color.FromRgb(0x25, 0x63, 0xEB),
            text: hasRepeat ? Repeats[RepeatBox.SelectedIndex].ShortLabel : "반복");
        UpdateChipSelection(_repeatChips, RepeatBox.SelectedIndex, Color.FromRgb(0xEF, 0xF6, 0xFF), Color.FromRgb(0x93, 0xC5, 0xFD), Color.FromRgb(0x1D, 0x4E, 0xD8));

        // Reminder panel & tile
        ReminderCard.Visibility = _reminderExpanded ? Visibility.Visible : Visibility.Collapsed;
        ReminderClockRow.Visibility = allDay && hasReminder ? Visibility.Visible : Visibility.Collapsed;
        StyleTile(OptReminderBtn, OptReminderIcon, OptReminderLabel,
            active: hasReminder || _reminderExpanded,
            highlight: hasReminder,
            activeBg: Color.FromRgb(0xFF, 0xFB, 0xEB),
            activeBorder: Color.FromRgb(0xFC, 0xD3, 0x4D),
            activeFg: Color.FromRgb(0xD9, 0x77, 0x06),
            text: hasReminder ? _reminderItems[ReminderBox.SelectedIndex].ShortLabel : "알림");
        UpdateChipSelection(_reminderChips, ReminderBox.SelectedIndex, Color.FromRgb(0xFF, 0xFB, 0xEB), Color.FromRgb(0xFC, 0xD3, 0x4D), Color.FromRgb(0xB4, 0x53, 0x09));

        // D-Day panel & tile
        DdayCard.Visibility = _ddayEnabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateDdayVisuals();
    }

    private void UpdateHeaderAndDuration(bool allDay)
    {
        var sd = CurrentStartDate;
        string dow = ItemDialog.DayName(sd.DayOfWeek);
        HeaderDateSummary.Text = $"{sd:yyyy년 M월 d일} ({dow})";

        string? dur = null;
        DateOnly? ed = EndDate.SelectedDate is { } edt ? DateOnly.FromDateTime(edt) : null;
        if (allDay)
        {
            if (ed is { } e && e > sd) dur = $"{e.DayNumber - sd.DayNumber + 1}일간 · 종일";
            else dur = "하루 종일";
        }
        else if (ReminderPlanner.TryParseClock(StartTime.Text, out var st))
        {
            var endDay = ed ?? sd;
            if (ReminderPlanner.TryParseClock(EndTime.Text, out var et))
            {
                var span = endDay.ToDateTime(et) - sd.ToDateTime(st);
                if (span > TimeSpan.Zero)
                {
                    if (span.Days > 0)
                        dur = span.Hours > 0 ? $"{span.Days}일 {span.Hours}시간" : $"{span.Days}일간";
                    else if (span.Hours > 0 && span.Minutes > 0)
                        dur = $"{span.Hours}시간 {span.Minutes}분";
                    else if (span.Hours > 0)
                        dur = $"{span.Hours}시간";
                    else
                        dur = $"{span.Minutes}분";
                }
            }
            else if (ed is { } e && e > sd)
            {
                dur = $"{e.DayNumber - sd.DayNumber + 1}일간";
            }
            else
            {
                dur = $"{st:HH:mm}";
            }
        }

        if (string.IsNullOrEmpty(dur))
        {
            DurationBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            DurationText.Text = dur;
            DurationBadge.Visibility = Visibility.Visible;
        }
    }

    private void UpdateDoneVisuals()
    {
        HeaderDonePill.Visibility = _done ? Visibility.Visible : Visibility.Collapsed;
        TitleBox.TextDecorations = _done ? TextDecorations.Strikethrough : null;
        TitleBox.Foreground = _done ? ModernUi.MutedBrush : ModernUi.InkBrush;

        if (_done)
        {
            TitleDoneCircle.Background = ModernUi.SuccessBrush;
            TitleDoneCircle.BorderBrush = ModernUi.SuccessBrush;
            TitleDoneCheck.Visibility = Visibility.Visible;
        }
        else
        {
            TitleDoneCircle.Background = Brushes.White;
            TitleDoneCircle.BorderBrush = ModernUi.InputBorderBrush;
            TitleDoneCheck.Visibility = Visibility.Collapsed;
        }

        StyleTile(OptDoneBtn, OptDoneIcon, OptDoneLabel,
            active: _done,
            highlight: _done,
            activeBg: Color.FromRgb(0xF0, 0xFD, 0xF4),
            activeBorder: Color.FromRgb(0x86, 0xEF, 0xAC),
            activeFg: Color.FromRgb(0x16, 0xA3, 0x4A),
            text: _done ? "완료됨" : "완료");
    }

    private void UpdateColorVisuals()
    {
        var accent = UiColors.TryParse(_color) ?? ModernUi.AccentColor;
        var accentBrush = UiColors.Frozen(accent);
        var softBg = UiColors.Frozen(Color.FromArgb(0x20, accent.R, accent.G, accent.B));
        var softBorder = UiColors.Frozen(Color.FromArgb(0x66, accent.R, accent.G, accent.B));

        TopAccentStrip.Background = accentBrush;
        HeaderIconBadge.Background = softBg;
        HeaderIconBadge.BorderBrush = softBorder;
        HeaderIconPath.Stroke = accentBrush;
        TitleColorDot.Background = accentBrush;

        bool hasEventColor = !string.IsNullOrWhiteSpace(_color);
        bool hasCellBg = !string.IsNullOrWhiteSpace(_cellBg);
        if (hasCellBg && UiColors.TryParse(_cellBg) is { } cellColor)
        {
            OptCellBgDot.Background = UiColors.Frozen(cellColor);
            OptCellBgDot.Visibility = Visibility.Visible;
        }
        else
        {
            OptCellBgDot.Visibility = Visibility.Collapsed;
        }

        if (hasEventColor)
        {
            OptColorBtn.Background = softBg;
            OptColorBtn.BorderBrush = softBorder;
            OptColorIcon.Stroke = accentBrush;
            OptColorIcon.Fill = UiColors.Frozen(Color.FromArgb(0x55, accent.R, accent.G, accent.B));
            OptColorLabel.Foreground = accentBrush;
            OptColorLabel.Text = "색상";
        }
        else if (hasCellBg && UiColors.TryParse(_cellBg) is { } cb)
        {
            var cbBrush = UiColors.Frozen(cb);
            OptColorBtn.Background = UiColors.Frozen(Color.FromArgb(0x1E, cb.R, cb.G, cb.B));
            OptColorBtn.BorderBrush = UiColors.Frozen(Color.FromArgb(0x66, cb.R, cb.G, cb.B));
            OptColorIcon.Stroke = ModernUi.InkBrush;
            OptColorIcon.Fill = Brushes.Transparent;
            OptColorLabel.Foreground = ModernUi.InkBrush;
            OptColorLabel.Text = "셀 배경";
        }
        else
        {
            OptColorBtn.Background = ModernUi.CardAltBrush;
            OptColorBtn.BorderBrush = ModernUi.LineBrush;
            OptColorIcon.Stroke = ModernUi.MutedBrush;
            OptColorIcon.Fill = Brushes.Transparent;
            OptColorLabel.Foreground = ModernUi.MutedBrush;
            OptColorLabel.Text = "색상";
        }
    }

    private void UpdateDdayVisuals()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        string title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "일정" : TitleBox.Text.Trim();
        string dateText = _existingCountdownLunar && !string.IsNullOrWhiteSpace(_existingCountdownDate)
            ? _existingCountdownDate!
            : CountdownCalc.Format(CurrentStartDate);

        var previewCd = new Countdown("", title, dateText, _ddayType, DdayShowCardBox.IsChecked == true, DdayShowBottomBox.IsChecked == true, _existingCountdownLunar);
        var status = CountdownCalc.Compute(previewCd, today);
        string badgeLabel = status?.Label ?? "D-Day";

        DdayPreviewText.Text = status is null ? "D-Day" : CountdownCalc.Summary(previewCd, status);
        HeaderDdayPill.Visibility = _ddayEnabled ? Visibility.Visible : Visibility.Collapsed;
        HeaderDdayText.Text = badgeLabel;

        StyleTile(OptDdayBtn, OptDdayIcon, OptDdayLabel,
            active: _ddayEnabled,
            highlight: _ddayEnabled,
            activeBg: Color.FromRgb(0xFD, 0xF2, 0xF8),
            activeBorder: Color.FromRgb(0xF9, 0xA8, 0xD4),
            activeFg: Color.FromRgb(0xDB, 0x27, 0x77),
            text: _ddayEnabled ? badgeLabel : "D-Day");

        var pinkBg = UiColors.Frozen(Color.FromRgb(0xFD, 0xF2, 0xF8));
        var pinkBorder = UiColors.Frozen(Color.FromRgb(0xF4, 0x72, 0xB6));
        var pinkFg = UiColors.Frozen(Color.FromRgb(0xBE, 0x18, 0x5D));
        foreach (var (tile, icon, lbl, type) in _ddayTypeTiles)
        {
            bool sel = type == _ddayType;
            tile.Background = sel ? pinkBg : ModernUi.CardAltBrush;
            tile.BorderBrush = sel ? pinkBorder : ModernUi.LineBrush;
            icon.Stroke = sel ? pinkFg : ModernUi.MutedBrush;
            lbl.Foreground = sel ? pinkFg : ModernUi.MutedBrush;
        }
    }

    private static void StyleTile(Border tile, Path icon, TextBlock label, bool active, bool highlight,
                                  Color activeBg, Color activeBorder, Color activeFg, string text)
    {
        label.Text = text;
        if (highlight)
        {
            tile.Background = UiColors.Frozen(activeBg);
            tile.BorderBrush = UiColors.Frozen(activeBorder);
            var fg = UiColors.Frozen(activeFg);
            icon.Stroke = fg;
            label.Foreground = fg;
        }
        else if (active)
        {
            tile.Background = ModernUi.HoverBrush;
            tile.BorderBrush = ModernUi.InputBorderBrush;
            icon.Stroke = ModernUi.InkBrush;
            label.Foreground = ModernUi.InkBrush;
        }
        else
        {
            tile.Background = ModernUi.CardAltBrush;
            tile.BorderBrush = ModernUi.LineBrush;
            icon.Stroke = ModernUi.MutedBrush;
            label.Foreground = ModernUi.MutedBrush;
        }
    }

    private static void UpdateChipSelection(List<(Button Btn, int Index)> chips, int selectedIndex, Color bg, Color border, Color fg)
    {
        var bgBrush = UiColors.Frozen(bg);
        var borderBrush = UiColors.Frozen(border);
        var fgBrush = UiColors.Frozen(fg);
        foreach (var (btn, idx) in chips)
        {
            bool sel = idx == selectedIndex;
            btn.Background = sel ? bgBrush : ModernUi.CardAltBrush;
            btn.BorderBrush = sel ? borderBrush : ModernUi.LineBrush;
            btn.Foreground = sel ? fgBrush : ModernUi.MutedBrush;
        }
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

    // ---- event handlers --------------------------------------------------------

    private void OptDone_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _done = !_done;
        UpdateUi();
    }

    private void OptColor_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ColorPickerPopup.Show(OptColorBtn, _store, _color, _cellBg,
            hex =>
            {
                _color = hex;
                UpdateColorVisuals();
            },
            hex =>
            {
                _cellBg = hex;
                _cellBgDirty = true;
                UpdateColorVisuals();
            },
            () => { },
            textSectionTitle: "일정 색상");
    }

    private void OptRepeat_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _repeatExpanded = !_repeatExpanded;
        UpdateUi();
    }

    private void OptReminder_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _reminderExpanded = !_reminderExpanded;
        UpdateUi();
    }

    private void OptDday_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ddayEnabled = !_ddayEnabled;
        UpdateUi();
    }

    private void ClearEndDate_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        EndDate.SelectedDate = null;
        UpdateUi();
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) UpdateUi();
    }

    private void TitleBox_FocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        bool focused = TitleBox.IsKeyboardFocused;
        TitleFrame.BorderBrush = focused ? ModernUi.AccentBrush : ModernUi.InputBorderBrush;
        TitleFrame.BorderThickness = new Thickness(focused ? 1.5 : 1);
        TitleFrame.Background = focused ? Brushes.White : ModernUi.CardAltBrush;
    }

    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
            BodyPlaceholder.Visibility = string.IsNullOrEmpty(BodyBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Repeat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SelectedKind != RepeatKind.None) _repeatExpanded = true;
        UpdateUi();
    }

    private void EndKind_Changed(object sender, SelectionChangedEventArgs e) => UpdateUi();
    private void AllDay_Click(object sender, RoutedEventArgs e) => UpdateUi();
    private void AllDay_Changed(object sender, RoutedEventArgs e) => UpdateUi();
    private void Time_Changed(object sender, TextChangedEventArgs e) { if (!_loading) UpdateHeaderAndDuration(AllDayBox.IsChecked == true); }

    private void Date_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ReferenceEquals(sender, StartDate))
        {
            RefreshMonthlyLabels();
            if (!_cellBgDirty && _getCellBg is not null)
                _cellBg = _getCellBg(CurrentStartDate);
        }
        UpdateUi();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            Save();
        }
    }

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
        if (kind.UsesInterval() && (!int.TryParse(IntervalBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval < 1 || interval > 999))
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
            Kind = kind, Interval = kind.UsesInterval() ? interval : 1,
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

        string? countdownId = _ddayEnabled ? (_existingCountdownId ?? Guid.NewGuid().ToString("N")) : null;

        draft = new EventDraft(TitleBox.Text.Trim(), BodyBox.Text, sd, st, ed, et, rule, _color, reminders, _done, countdownId);
        var problems = draft.Validate();
        if (problems.Count > 0) { error = string.Join(" ", problems); return false; }
        return true;
    }

    internal CheckBox[] WeekdayBoxes => _weekdayBoxes;

    // ---- dev helpers for UI self-test / snapshots ------------------------------

    internal void DevToggleDone(bool done) { _done = done; UpdateUi(); }
    internal void DevSetColor(string? hex) { _color = hex; UpdateUi(); }
    internal void DevSetDday(bool enabled, CountdownType type = CountdownType.Custom, bool showCard = true, bool showBottom = false)
    {
        _ddayEnabled = enabled;
        _ddayType = type;
        DdayShowCardBox.IsChecked = showCard;
        DdayShowBottomBox.IsChecked = showBottom;
        UpdateUi();
    }

    // ---- buttons ---------------------------------------------------------------

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    /// <summary>
    /// Set by the app: what already on the calendar clashes with an event on this day (day, title, start, end, the event's own id).
    /// Null in scripted runs, so that a notice never waits for a click there.
    /// </summary>
    internal static Func<DateOnly, string, TimeOnly?, TimeOnly?, string?, IReadOnlyList<string>>? ClashCheck;

    internal bool Save()
    {
        if (!TryBuild(out var draft, out var error)) { ErrorText.Text = error; return false; }
        if (ClashCheck is { } check)
        {
            // an end on another day cannot be compared on this day alone: leave it out and let the usual one hour stand
            var clashes = check(draft.StartDate, draft.Title, draft.StartTime, draft.EndDate is null || draft.EndDate == draft.StartDate ? draft.EndTime : null, _eventId);
            if (clashes.Count > 0 && MessageBox.Show(this, "겹치는 일정이 있습니다:\n\n" + string.Join("\n", clashes.Take(5).Select(c => "· " + c)) + "\n\n그래도 저장할까요?",
                    "일정 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) != MessageBoxResult.Yes) return false;
        }
        try
        {
            // Persist or remove linked D-Day countdown before saving the event.
            bool persistCountdown = _countdowns is not null;
            if (!persistCountdown && draft.CountdownId is not null)
                draft = draft with { CountdownId = null };

            if (_eventId is null)
            {
                if (persistCountdown && draft.CountdownId is { } newCid)
                {
                    UpsertCountdown(newCid, draft.Title, draft.StartDate);
                    CountdownChanged = true;
                }
                _svc.Create(draft);
            }
            else
            {
                var scope = _wasRepeating ? ScopeDialog.Ask(this, "수정") : SeriesScope.All;
                if (scope == SeriesScope.Cancel) return false;
                switch (scope)
                {
                    case SeriesScope.All:
                        int delta = _seriesStart.DayNumber - _occurrence.DayNumber;
                        var seriesDraft = draft with
                        {
                            StartDate = draft.StartDate.AddDays(delta),
                            EndDate = draft.EndDate?.AddDays(delta),
                        };
                        if (persistCountdown) ApplyCountdownChange(seriesDraft.Title, seriesDraft.StartDate, seriesDraft.CountdownId);
                        _svc.UpdateAll(_eventId, seriesDraft);
                        break;
                    case SeriesScope.This:
                        var thisDraft = draft;
                        if (persistCountdown && _ddayEnabled)
                        {
                            string ownCid = Guid.NewGuid().ToString("N");
                            UpsertCountdown(ownCid, thisDraft.Title, thisDraft.StartDate);
                            thisDraft = thisDraft with { CountdownId = ownCid };
                            CountdownChanged = true;
                        }
                        else thisDraft = thisDraft with { CountdownId = null };
                        _svc.UpdateThis(_eventId, _occurrence, thisDraft);
                        break;
                    case SeriesScope.Future:
                        var futDraft = draft;
                        if (persistCountdown && _ddayEnabled)
                        {
                            string ownCid = _occurrence <= _seriesStart && _existingCountdownId is not null ? _existingCountdownId : Guid.NewGuid().ToString("N");
                            UpsertCountdown(ownCid, futDraft.Title, futDraft.StartDate);
                            futDraft = futDraft with { CountdownId = ownCid };
                            CountdownChanged = true;
                        }
                        else if (persistCountdown && !_ddayEnabled && _occurrence <= _seriesStart && _existingCountdownId is not null)
                        {
                            _countdowns!.Delete(_existingCountdownId);
                            CountdownChanged = true;
                        }
                        _svc.UpdateFuture(_eventId, _occurrence, futDraft);
                        break;
                }
            }

            if (_cellBgDirty && _setCellBg is not null)
                _setCellBg(draft.StartDate, _cellBg);

            Changed = true;
            Close();
            return true;
        }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; return false; }
    }

    private void UpsertCountdown(string id, string title, DateOnly day)
    {
        if (_countdowns is null) return;
        string date = _existingCountdownLunar && !string.IsNullOrWhiteSpace(_existingCountdownDate)
            ? _existingCountdownDate!
            : CountdownCalc.Format(day);
        _countdowns.Upsert(new Countdown(id, title, date, _ddayType,
            DdayShowCardBox.IsChecked == true, DdayShowBottomBox.IsChecked == true, _existingCountdownLunar));
    }

    private void ApplyCountdownChange(string title, DateOnly day, string? nextCountdownId)
    {
        if (_countdowns is null) return;
        if (_ddayEnabled && nextCountdownId is not null)
        {
            UpsertCountdown(nextCountdownId, title, day);
            CountdownChanged = true;
        }
        else if (!_ddayEnabled && _existingCountdownId is not null)
        {
            _countdowns.Delete(_existingCountdownId);
            CountdownChanged = true;
        }
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
            else
            {
                _svc.DeleteAll(_eventId);
                if (_existingCountdownId is not null) CountdownChanged = true;
            }
        }
        else
        {
            if (MessageBox.Show(this, "이 일정을 삭제할까요?", "일정 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _svc.DeleteAll(_eventId);
            if (_existingCountdownId is not null) CountdownChanged = true;
        }
        Changed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
