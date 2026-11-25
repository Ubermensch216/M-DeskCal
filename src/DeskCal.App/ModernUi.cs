using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;
using TextBox = System.Windows.Controls.TextBox;

namespace DeskCal.App;

/// <summary>
/// Shared modern light UI kit (colors, flat 24x24 vector icons, cards, headers, buttons, and form styles)
/// used across the quick menu and the redesigned tool windows.
/// </summary>
internal static class ModernUi
{
    public static readonly Color AccentColor = Color.FromRgb(0x2F, 0x8C, 0xFF);
    public static readonly SolidColorBrush BgBrush = Frozen(Color.FromRgb(0xF4, 0xF6, 0xFA));
    public static readonly SolidColorBrush CardBrush = Frozen(Colors.White);
    public static readonly SolidColorBrush CardAltBrush = Frozen(Color.FromRgb(0xF8, 0xFA, 0xFC));
    public static readonly SolidColorBrush AccentBrush = Frozen(AccentColor);
    public static readonly SolidColorBrush AccentHoverBrush = Frozen(Color.FromRgb(0x1D, 0x78, 0xEB));
    public static readonly SolidColorBrush AccentSoftBrush = Frozen(Color.FromRgb(0xEF, 0xF6, 0xFF));
    public static readonly SolidColorBrush AccentBorderBrush = Frozen(Color.FromRgb(0xBF, 0xDB, 0xFE));
    public static readonly SolidColorBrush InkBrush = Frozen(Color.FromRgb(0x1E, 0x29, 0x3B));
    public static readonly SolidColorBrush MutedBrush = Frozen(Color.FromRgb(0x64, 0x74, 0x8B));
    public static readonly SolidColorBrush LineBrush = Frozen(Color.FromRgb(0xE2, 0xE8, 0xF0));
    public static readonly SolidColorBrush InputBorderBrush = Frozen(Color.FromRgb(0xCB, 0xD5, 0xE1));
    public static readonly SolidColorBrush HoverBrush = Frozen(Color.FromRgb(0xF1, 0xF5, 0xF9));
    public static readonly SolidColorBrush DangerBrush = Frozen(Color.FromRgb(0xDC, 0x26, 0x26));
    public static readonly SolidColorBrush DangerSoftBrush = Frozen(Color.FromRgb(0xFE, 0xF2, 0xF2));
    public static readonly SolidColorBrush DangerBorderBrush = Frozen(Color.FromRgb(0xFE, 0xCA, 0xCA));
    public static readonly SolidColorBrush WarnBrush = Frozen(Color.FromRgb(0xD9, 0x77, 0x06));
    public static readonly SolidColorBrush WarnSoftBrush = Frozen(Color.FromRgb(0xFF, 0xFB, 0xEB));
    public static readonly SolidColorBrush SuccessBrush = Frozen(Color.FromRgb(0x16, 0xA3, 0x4A));
    public static readonly SolidColorBrush SuccessSoftBrush = Frozen(Color.FromRgb(0xF0, 0xFD, 0xF4));

    public static readonly FontFamily AppFont = new("Segoe UI, Malgun Gothic");

    public static SolidColorBrush Frozen(Color c) => UiColors.Frozen(c);

    // Flat 24x24 stroke-based vector icon paths (clean, uniform 1.8px stroke)
    public static class Icons
    {
        // Group icons
        public const string GroupTools = "M8 2v3M16 2v3M3 9h18M5 5h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2zM9 14l2 2 4-4";
        public const string GroupDisplay = "M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7zM12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z";
        public const string GroupSettings = "M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6";

        // Menu & window icons
        public const string CalendarPlus = "M8 2v3M16 2v3M3 9h18M19 4H5a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2zM12 12v6M9 15h6";
        public const string Bolt = "M13 2L4 14h7l-1 8 9-12h-7l1-8z";
        public const string FileText = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8zM14 2v6h6M8 13h8M8 17h5";
        public const string RepeatPlan = "M17 1l4 4-4 4M3 11V9a4 4 0 0 1 4-4h14M7 23l-4-4 4-4M21 13v2a4 4 0 0 1-4 4H3";
        public const string Rollover = "M9 11l3 3L22 4M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11";
        public const string Briefing = "M12 3v2M12 19v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M3 12h2M19 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z";
        public const string Search = "M11 3a8 8 0 1 0 0 16 8 8 0 0 0 0-16zM21 21l-4.35-4.35";

        public const string Layout = "M3 3h8v8H3zM13 3h8v8h-8zM3 13h8v8H3zM13 13h8v8h-8z";
        public const string Moon = "M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z";
        public const string Flag = "M4 22V4M4 4h12l-2 4 2 4H4";
        public const string Hash = "M4 9h16M4 15h16M10 3L8 21M16 3l-2 18";
        public const string CheckSquare = "M9 11l3 3L22 4M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11";
        public const string Droplet = "M12 2.69l5.66 5.66a8 8 0 1 1-11.31 0z";

        public const string MoveResize = "M5 9l-3 3 3 3M9 5l3-3 3 3M15 19l-3 3-3-3M19 9l3 3-3 3M2 12h20M12 2v20";
        public const string Lock = "M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2zM7 11V7a5 5 0 0 1 10 0v4";
        public const string Gear = "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z";
        public const string Power = "M18.36 6.64a9 9 0 1 1-12.73 0M12 2v10";
        public const string Check = "M20 6L9 17l-5-5";
        public const string ChevronRight = "M9 18l6-6-6-6";
        public const string Monitor = "M3 4h18a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM8 21h8M12 18v3";
        public const string Cloud = "M18 10h-1.26A8 8 0 1 0 9 20h9a5 5 0 0 0 0-10z";
        public const string ArrowsLeftRight = "M8 7l-4 4 4 4M4 11h16M16 17l4-4-4-4";
        public const string Link = "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71";
        public const string Terminal = "M4 17l6-6-6-6M12 19h8";
        public const string Shield = "M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z";
        public const string Sparkles = "M12 3l1.5 4.5L18 9l-4.5 1.5L12 15l-1.5-4.5L6 9l4.5-1.5L12 3zM5 17l.8 2.2L8 20l-2.2.8L5 23l-.8-2.2L2 20l2.2-.8L5 17zM19 14l.6 1.8L21.4 16.4l-1.8.6L19 18.8l-.6-1.8-1.8-.6 1.8-.6L19 14z";
        public const string Cpu = "M9 9h6v6H9zM4 9h2M4 15h2M18 9h2M18 15h2M9 4v2M15 4v2M9 18v2M15 18v2M6 6h12v12H6z";
        public const string ArrowRight = "M5 12h14M13 6l6 6-6 6";
        public const string UserCheck = "M16 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2M8.5 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8zM17 11l2 2 4-4";
        public const string Layers = "M12 2L2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5";
        public const string Info = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zM12 16v-4M12 8h.01";
        public const string Type = "M4 7V4h16v3M9 20h6M12 4v16";
        public const string Image = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM8.5 10a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3zM21 15l-5-5L5 21";
        public const string Keyboard = "M4 6h16a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2zM6 10h.01M10 10h.01M14 10h.01M18 10h.01M8 14h8";
        public const string Send = "M22 2L11 13M22 2l-7 20-4-9-9-4 20-7z";
        public const string ArrowUp = "M12 19V5M5 12l7-7 7 7";
        public const string Undo = "M3 7v6h6M21 17a9 9 0 0 0-9-9 9 9 0 0 0-6 2.3L3 13";
        public const string Trash = "M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6";
        public const string Clock = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zM12 6v6l4 2";
        public const string Bell = "M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9M13.73 21a2 2 0 0 1-3.46 0";
        public const string Calendar = "M8 2v3M16 2v3M3 9h18M5 4h14a2 2 0 0 1 2 2v13a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z";
        public const string Edit = "M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7M18.5 2.5a2.12 2.12 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z";
        public const string Plus = "M12 5v14M5 12h14";
        public const string Close = "M18 6L6 18M6 6l12 12";
        public const string AlertTriangle = "M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0zM12 9v4M12 17h.01";
        public const string History = "M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5M12 7v5l3 2";
        public const string ChevronDown = "M6 9l6 6 6-6";
        public const string ThumbUp = "M14 9V5a3 3 0 0 0-3-3l-4 9v11h11.28a2 2 0 0 0 2-1.7l1.38-9a2 2 0 0 0-2-2.3H14zM7 22H4a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2h3";
        public const string ThumbDown = "M10 15v4a3 3 0 0 0 3 3l4-9V2H5.72a2 2 0 0 0-2 1.7l-1.38 9a2 2 0 0 0 2 2.3H10zM17 2h2.67A2.31 2.31 0 0 1 22 4v7a2.31 2.31 0 0 1-2.33 2H17";
        public const string RotateCcw = "M1 4v6h6M3.51 15a9 9 0 1 0 2.13-9.36L1 10";
    }

    /// <summary>Crisp 24x24 flat vector icon rendered inside a Viewbox so it scales cleanly at any size.</summary>
    public static Viewbox FlatIcon(string pathData, Brush? stroke = null, double size = 16, double strokeThickness = 1.9)
    {
        var path = new Path
        {
            Data = Geometry.Parse(pathData),
            Stroke = stroke ?? MutedBrush,
            StrokeThickness = strokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            Width = 24,
            Height = 24,
        };
        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = path,
            SnapsToDevicePixels = true,
        };
    }

    public static void ConfigureWindow(Window win, string title, double width, double height, bool topmost = true, bool showInTaskbar = true)
    {
        win.Title = title;
        win.Width = width;
        win.Height = height;
        win.MinWidth = Math.Min(width, 420);
        win.MinHeight = Math.Min(height, 340);
        win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.Topmost = topmost;
        win.ShowInTaskbar = showInTaskbar;
        win.FontFamily = AppFont;
        win.FontSize = 13;
        win.Foreground = InkBrush;
        win.Background = BgBrush;
        win.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
        ApplyControlStyles(win.Resources);
    }

    /// <summary>Modern top header bar with icon badge, title, subtitle, and optional right slot.</summary>
    public static Border Header(string iconData, string title, string subtitle, UIElement? right = null)
    {
        var iconPill = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(10),
            Background = AccentSoftBrush, BorderBrush = AccentBorderBrush, BorderThickness = new Thickness(1),
            Child = FlatIcon(iconData, AccentBrush, 19, 2.0),
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = title, FontSize = 16.5, FontWeight = FontWeights.SemiBold, Foreground = InkBrush });
        if (!string.IsNullOrWhiteSpace(subtitle))
            titles.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = MutedBrush, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });

        var row = new DockPanel();
        DockPanel.SetDock(iconPill, Dock.Left);
        row.Children.Add(iconPill);
        if (right is not null) { DockPanel.SetDock(right, Dock.Right); row.Children.Add(right); }
        row.Children.Add(titles);

        return new Border
        {
            Background = CardBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(20, 14, 20, 14),
            Child = row,
        };
    }

    public static Border Card(UIElement child, Thickness? padding = null, Thickness? margin = null, Brush? background = null, Brush? border = null) => new()
    {
        Background = background ?? CardBrush,
        BorderBrush = border ?? LineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = padding ?? new Thickness(14, 12, 14, 12),
        Margin = margin ?? new Thickness(0, 0, 0, 10),
        Child = child,
    };

    public static Border Pill(string text, Brush fg, Brush bg, Brush? border = null) => new()
    {
        Background = bg,
        BorderBrush = border ?? Brushes.Transparent,
        BorderThickness = new Thickness(border is null ? 0 : 1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 2, 8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = fg },
    };

    public static TextBlock FieldLabel(string text, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = MutedBrush,
        Margin = margin ?? new Thickness(0, 0, 0, 5),
    };

    public static Button PrimaryBtn(string text, RoutedEventHandler click, bool isDefault = false) =>
        MakeBtn(text, click, AccentBrush, AccentHoverBrush, Brushes.White, Brushes.Transparent, isDefault: isDefault);

    public static Button SecondaryBtn(string text, RoutedEventHandler click, bool isCancel = false) =>
        MakeBtn(text, click, CardBrush, HoverBrush, InkBrush, InputBorderBrush, isCancel: isCancel);

    public static Button DangerBtn(string text, RoutedEventHandler click) =>
        MakeBtn(text, click, DangerSoftBrush, Frozen(Color.FromRgb(0xFE, 0xE2, 0xE2)), DangerBrush, DangerBorderBrush);

    public static Button ChipBtn(string text, RoutedEventHandler click)
    {
        var b = MakeBtn(text, click, CardBrush, HoverBrush, InkBrush, LineBrush);
        b.Padding = new Thickness(10, 4, 10, 4);
        b.FontSize = 12;
        return b;
    }

    private static Button MakeBtn(string text, RoutedEventHandler click, Brush bg, Brush hover, Brush fg, Brush border, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button
        {
            Content = text,
            Background = bg,
            Foreground = fg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            Cursor = System.Windows.Input.Cursors.Hand,
            IsDefault = isDefault,
            IsCancel = isCancel,
            Template = ButtonTemplate(bg, hover),
        };
        b.Click += click;
        return b;
    }

    private static ControlTemplate ButtonTemplate(Brush normalBg, Brush hoverBg)
    {
        var borderFactory = new FrameworkElementFactory(typeof(Border), "bd");
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(content);

        var tpl = new ControlTemplate(typeof(Button)) { VisualTree = borderFactory };
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "bd"));
        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "bd"));
        tpl.Triggers.Add(hoverTrigger);
        tpl.Triggers.Add(disabledTrigger);
        return tpl;
    }

    /// <summary>Applies modern rounded styles for TextBox, ListBox, TabControl, CheckBox, RadioButton, and Button inside a window.</summary>
    public static void ApplyControlStyles(ResourceDictionary res)
    {
        var tbStyle = new Style(typeof(TextBox));
        tbStyle.Setters.Add(new Setter(Control.BackgroundProperty, CardBrush));
        tbStyle.Setters.Add(new Setter(Control.ForegroundProperty, InkBrush));
        tbStyle.Setters.Add(new Setter(Control.BorderBrushProperty, InputBorderBrush));
        tbStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        tbStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 6, 9, 6)));
        tbStyle.Setters.Add(new Setter(Control.TemplateProperty, TextBoxTemplate()));
        res[typeof(TextBox)] = tbStyle;

        var lbStyle = new Style(typeof(ListBox));
        lbStyle.Setters.Add(new Setter(Control.BackgroundProperty, CardBrush));
        lbStyle.Setters.Add(new Setter(Control.BorderBrushProperty, LineBrush));
        lbStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        lbStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        res[typeof(ListBox)] = lbStyle;

        var lbiStyle = new Style(typeof(ListBoxItem));
        lbiStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        lbiStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 1, 0, 1)));
        res[typeof(ListBoxItem)] = lbiStyle;

        var tabStyle = new Style(typeof(TabItem));
        tabStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        tabStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        tabStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 8, 14, 8)));
        res[typeof(TabItem)] = tabStyle;
    }

    /// <summary>Applies a crisp flat rounded style for ComboBox and ComboBoxItem (trim-safe, no XAML string lookups).</summary>
    public static void ApplyFlatComboBoxStyle(ResourceDictionary res)
    {
        var cbStyle = new Style(typeof(ComboBox));
        cbStyle.Setters.Add(new Setter(Control.BackgroundProperty, CardBrush));
        cbStyle.Setters.Add(new Setter(Control.ForegroundProperty, InkBrush));
        cbStyle.Setters.Add(new Setter(Control.BorderBrushProperty, InputBorderBrush));
        cbStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        cbStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 32.0));
        cbStyle.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
        cbStyle.Setters.Add(new Setter(Control.TemplateProperty, FlatComboBoxTemplate()));
        res[typeof(ComboBox)] = cbStyle;

        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 6, 9, 6)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, FlatComboBoxItemTemplate()));
        res[typeof(ComboBoxItem)] = itemStyle;
    }

    private static ControlTemplate FlatComboBoxTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));

        // ToggleButton with rounded border + chevron
        var tbBd = new FrameworkElementFactory(typeof(Border), "bd");
        tbBd.SetValue(Border.BackgroundProperty, CardBrush);
        tbBd.SetValue(Border.BorderBrushProperty, InputBorderBrush);
        tbBd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        tbBd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        tbBd.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var tbGrid = new FrameworkElementFactory(typeof(Grid));
        var tc0 = new FrameworkElementFactory(typeof(ColumnDefinition));
        tc0.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var tc1 = new FrameworkElementFactory(typeof(ColumnDefinition));
        tc1.SetValue(ColumnDefinition.WidthProperty, new GridLength(24));
        tbGrid.AppendChild(tc0);
        tbGrid.AppendChild(tc1);

        var chev = new FrameworkElementFactory(typeof(Path));
        chev.SetValue(Grid.ColumnProperty, 1);
        chev.SetValue(FrameworkElement.WidthProperty, 11.0);
        chev.SetValue(FrameworkElement.HeightProperty, 11.0);
        chev.SetValue(Path.StretchProperty, Stretch.Uniform);
        chev.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        chev.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chev.SetValue(Path.DataProperty, Geometry.Parse(Icons.ChevronDown));
        chev.SetValue(Path.StrokeProperty, MutedBrush);
        chev.SetValue(Path.StrokeThicknessProperty, 2.0);
        chev.SetValue(Path.StrokeStartLineCapProperty, PenLineCap.Round);
        chev.SetValue(Path.StrokeEndLineCapProperty, PenLineCap.Round);
        chev.SetValue(Path.StrokeLineJoinProperty, PenLineJoin.Round);
        tbGrid.AppendChild(chev);
        tbBd.AppendChild(tbGrid);

        var tbTpl = new ControlTemplate(typeof(ToggleButton)) { VisualTree = tbBd };
        var tbHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        tbHover.Setters.Add(new Setter(Border.BorderBrushProperty, Frozen(Color.FromRgb(0x94, 0xA3, 0xB8)), "bd"));
        tbHover.Setters.Add(new Setter(Border.BackgroundProperty, CardAltBrush, "bd"));
        tbTpl.Triggers.Add(tbHover);
        var tbChecked = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        tbChecked.Setters.Add(new Setter(Border.BorderBrushProperty, AccentBrush, "bd"));
        tbChecked.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1.5), "bd"));
        tbTpl.Triggers.Add(tbChecked);

        var toggle = new FrameworkElementFactory(typeof(ToggleButton), "ToggleButton");
        toggle.SetValue(UIElement.FocusableProperty, false);
        toggle.SetValue(ButtonBase.ClickModeProperty, ClickMode.Press);
        toggle.SetValue(Control.TemplateProperty, tbTpl);
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("IsDropDownOpen")
        {
            Mode = System.Windows.Data.BindingMode.TwoWay,
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
        });
        grid.AppendChild(toggle);

        var contentSite = new FrameworkElementFactory(typeof(ContentPresenter), "ContentSite");
        contentSite.SetValue(UIElement.IsHitTestVisibleProperty, false);
        contentSite.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
        contentSite.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
        contentSite.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 24, 0));
        contentSite.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        contentSite.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        grid.AppendChild(contentSite);

        var popup = new FrameworkElementFactory(typeof(Popup), "Popup");
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.IsOpenProperty, new TemplateBindingExtension(ComboBox.IsDropDownOpenProperty));
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(UIElement.FocusableProperty, false);
        popup.SetValue(Popup.VerticalOffsetProperty, 3.0);

        var dropBd = new FrameworkElementFactory(typeof(Border));
        dropBd.SetValue(Border.BackgroundProperty, CardBrush);
        dropBd.SetValue(Border.BorderBrushProperty, InputBorderBrush);
        dropBd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        dropBd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        dropBd.SetValue(Border.PaddingProperty, new Thickness(4));
        dropBd.SetValue(FrameworkElement.MinWidthProperty, new TemplateBindingExtension(FrameworkElement.ActualWidthProperty));
        dropBd.SetValue(FrameworkElement.MaxHeightProperty, 240.0);
        dropBd.SetValue(UIElement.EffectProperty, new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.14, Color = Color.FromRgb(0x0F, 0x17, 0x2A) });

        var sv = new FrameworkElementFactory(typeof(ScrollViewer));
        sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        var sp = new FrameworkElementFactory(typeof(StackPanel));
        sp.SetValue(Panel.IsItemsHostProperty, true);
        sv.AppendChild(sp);
        dropBd.AppendChild(sv);
        popup.AppendChild(dropBd);
        grid.AppendChild(popup);

        return new ControlTemplate(typeof(ComboBox)) { VisualTree = grid };
    }

    private static ControlTemplate FlatComboBoxItemTemplate()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);

        var tpl = new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = bd };
        var hi = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        hi.Setters.Add(new Setter(Border.BackgroundProperty, AccentSoftBrush, "bd"));
        tpl.Triggers.Add(hi);
        var sel = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        sel.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromRgb(0xDB, 0xEA, 0xFE)), "bd"));
        sel.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        sel.Setters.Add(new Setter(Control.ForegroundProperty, Frozen(Color.FromRgb(0x1D, 0x4E, 0xD8))));
        tpl.Triggers.Add(sel);
        return tpl;
    }

    private static ControlTemplate TextBoxTemplate()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        bd.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        sv.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        sv.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(sv);

        var tpl = new ControlTemplate(typeof(TextBox)) { VisualTree = bd };
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, AccentBrush, "bd"));
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1.5), "bd"));
        tpl.Triggers.Add(focus);
        return tpl;
    }

    /// <summary>Builds a modern footer bar with subtle top border and right-aligned / left-aligned action buttons.</summary>
    public static Border Footer(UIElement left, UIElement? right = null)
    {
        var dock = new DockPanel();
        if (right is not null) { DockPanel.SetDock(right, Dock.Right); dock.Children.Add(right); }
        dock.Children.Add(left);
        return new Border
        {
            Background = CardBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 12, 20, 12),
            Child = dock,
        };
    }

    public static Border ActionFooter(IEnumerable<UIElement> leftButtons, params UIElement[] rightButtons)
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in leftButtons) left.Children.Add(b);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < rightButtons.Length; i++)
        {
            if (i == rightButtons.Length - 1 && rightButtons[i] is FrameworkElement fe)
                fe.Margin = new Thickness(0);
            right.Children.Add(rightButtons[i]);
        }
        return Footer(left, rightButtons.Length > 0 ? right : null);
    }

    public static DockPanel PageShell(UIElement header, UIElement footer, UIElement center)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(center);
        return dock;
    }

    public static ScrollViewer ScrollBody(params UIElement[] children)
    {
        var body = new StackPanel();
        foreach (var c in children) body.Children.Add(c);
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(18, 14, 18, 10),
            Content = body,
        };
    }

    public static TextBlock SummaryText() => new()
    {
        Margin = new Thickness(0, 4, 0, 8),
        FontWeight = FontWeights.SemiBold,
        Foreground = InkBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock StatusText() => new()
    {
        Margin = new Thickness(0, 8, 0, 0),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = MutedBrush,
    };

    public static Border CheckCardRow(CheckBox check, UIElement body, UIElement? badge = null, UIElement? right = null, bool mutedBg = false)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (badge is not null) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (right is not null) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int col = 0;
        Grid.SetColumn(check, col++);
        row.Children.Add(check);
        if (badge is not null) { Grid.SetColumn(badge, col++); row.Children.Add(badge); }
        Grid.SetColumn(body, col++);
        row.Children.Add(body);
        if (right is not null) { Grid.SetColumn(right, col); row.Children.Add(right); }

        return Card(row, new Thickness(12, 10, 12, 10), new Thickness(0, 0, 0, 6),
            background: mutedBg ? CardAltBrush : CardBrush);
    }

    // ---- Modern ContextMenu for the Gear Quick Menu ----

    public static ContextMenu CreateModernContextMenu()
    {
        var menu = new ContextMenu
        {
            FontFamily = AppFont,
            FontSize = 13,
            Foreground = InkBrush,
            HasDropShadow = true,
            Background = CardBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            MinWidth = 248,
        };
        menu.Template = ContextMenuTemplate();
        return menu;
    }

    private static ControlTemplate ContextMenuTemplate()
    {
        var shadow = new DropShadowEffect
        {
            BlurRadius = 18,
            ShadowDepth = 4,
            Direction = 270,
            Opacity = 0.14,
            Color = Color.FromRgb(0x0F, 0x17, 0x2A),
        };
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, CardBrush);
        bd.SetValue(Border.BorderBrushProperty, LineBrush);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        bd.SetValue(Border.PaddingProperty, new Thickness(6));
        bd.SetValue(Border.MarginProperty, new Thickness(6, 4, 10, 12));
        bd.SetValue(UIElement.EffectProperty, shadow);

        var grid = new FrameworkElementFactory(typeof(Grid));
        // Subtle left sidebar gutter behind items
        var sidebar = new FrameworkElementFactory(typeof(Border));
        sidebar.SetValue(Border.WidthProperty, 32.0);
        sidebar.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        sidebar.SetValue(Border.BackgroundProperty, CardAltBrush);
        sidebar.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        grid.AppendChild(sidebar);

        var items = new FrameworkElementFactory(typeof(StackPanel));
        items.SetValue(Panel.IsItemsHostProperty, true);
        grid.AppendChild(items);
        bd.AppendChild(grid);

        return new ControlTemplate(typeof(ContextMenu)) { VisualTree = bd };
    }

    /// <summary>
    /// Group header positioned at the far left above the sidebar gutter, with a flat icon and group title.
    /// </summary>
    public static MenuItem MenuGroupHeader(string iconData, string title, bool topDivider = true)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = FlatIcon(iconData, AccentBrush, 13, 2.0);
        icon.Margin = new Thickness(0, 0, 7, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(icon);
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var root = new StackPanel();
        if (topDivider)
            root.Children.Add(new Border { Height = 1, Background = LineBrush, Margin = new Thickness(2, 4, 2, 6) });
        var labelRow = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = header,
        };
        root.Children.Add(labelRow);

        var mi = new MenuItem { Header = root, IsHitTestVisible = false, Focusable = false };
        var tpl = new ControlTemplate(typeof(MenuItem));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        cp.SetValue(FrameworkElement.MarginProperty, new Thickness(0, topDivider ? 2 : 0, 0, 2));
        tpl.VisualTree = cp;
        mi.Template = tpl;
        return mi;
    }

    public static MenuItem MenuEntry(string header, string? iconData, Action? click, bool? isChecked = null, bool isDanger = false)
    {
        var mi = new MenuItem
        {
            Header = header,
            IsChecked = isChecked == true,
            Foreground = isDanger ? DangerBrush : InkBrush,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        if (click is not null) mi.Click += (_, _) => click();

        // Build custom template with 32px left sidebar icon/check slot + label + optional submenu chevron
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        bd.SetValue(Border.PaddingProperty, new Thickness(0, 6, 8, 6));
        bd.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1));

        var grid = new FrameworkElementFactory(typeof(Grid));
        var c0 = new FrameworkElementFactory(typeof(ColumnDefinition));
        c0.SetValue(ColumnDefinition.WidthProperty, new GridLength(32));
        var c1 = new FrameworkElementFactory(typeof(ColumnDefinition));
        c1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var c2 = new FrameworkElementFactory(typeof(ColumnDefinition));
        c2.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        grid.AppendChild(c0); grid.AppendChild(c1); grid.AppendChild(c2);

        // Left sidebar slot: check pill if checked, otherwise flat icon
        var leftSlot = new FrameworkElementFactory(typeof(Grid));
        leftSlot.SetValue(Grid.ColumnProperty, 0);
        if (isChecked == true)
        {
            var pill = new FrameworkElementFactory(typeof(Border));
            pill.SetValue(Border.WidthProperty, 18.0);
            pill.SetValue(Border.HeightProperty, 18.0);
            pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            pill.SetValue(Border.BackgroundProperty, AccentSoftBrush);
            pill.SetValue(Border.BorderBrushProperty, AccentBorderBrush);
            pill.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var chk = new FrameworkElementFactory(typeof(Path));
            chk.SetValue(Path.DataProperty, Geometry.Parse(Icons.Check));
            chk.SetValue(Path.StrokeProperty, AccentBrush);
            chk.SetValue(Path.StrokeThicknessProperty, 2.2);
            chk.SetValue(Path.StretchProperty, Stretch.Uniform);
            chk.SetValue(FrameworkElement.WidthProperty, 11.0);
            chk.SetValue(FrameworkElement.HeightProperty, 11.0);
            pill.AppendChild(chk);
            leftSlot.AppendChild(pill);
        }
        else if (iconData is not null)
        {
            var ic = new FrameworkElementFactory(typeof(Path));
            ic.SetValue(Path.DataProperty, Geometry.Parse(iconData));
            ic.SetValue(Path.StrokeProperty, isDanger ? DangerBrush : MutedBrush);
            ic.SetValue(Path.StrokeThicknessProperty, 1.8);
            ic.SetValue(Path.StrokeStartLineCapProperty, PenLineCap.Round);
            ic.SetValue(Path.StrokeEndLineCapProperty, PenLineCap.Round);
            ic.SetValue(Path.StrokeLineJoinProperty, PenLineJoin.Round);
            ic.SetValue(Path.StretchProperty, Stretch.Uniform);
            ic.SetValue(FrameworkElement.WidthProperty, 14.0);
            ic.SetValue(FrameworkElement.HeightProperty, 14.0);
            ic.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            ic.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            leftSlot.AppendChild(ic);
        }
        grid.AppendChild(leftSlot);

        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(Grid.ColumnProperty, 1);
        text.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 10, 0));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(text);

        var arrow = new FrameworkElementFactory(typeof(Path), "arrow");
        arrow.SetValue(Grid.ColumnProperty, 2);
        arrow.SetValue(Path.DataProperty, Geometry.Parse(Icons.ChevronRight));
        arrow.SetValue(Path.StrokeProperty, MutedBrush);
        arrow.SetValue(Path.StrokeThicknessProperty, 1.8);
        arrow.SetValue(Path.StretchProperty, Stretch.Uniform);
        arrow.SetValue(FrameworkElement.WidthProperty, 10.0);
        arrow.SetValue(FrameworkElement.HeightProperty, 10.0);
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        grid.AppendChild(arrow);

        // Submenu popup
        var popup = new FrameworkElementFactory(typeof(Popup), "PART_Popup");
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Right);
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.IsOpenProperty, new TemplateBindingExtension(MenuItem.IsSubmenuOpenProperty));
        var subBorder = new FrameworkElementFactory(typeof(Border));
        subBorder.SetValue(Border.BackgroundProperty, CardBrush);
        subBorder.SetValue(Border.BorderBrushProperty, LineBrush);
        subBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        subBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        subBorder.SetValue(Border.PaddingProperty, new Thickness(6));
        subBorder.SetValue(Border.MarginProperty, new Thickness(4, 0, 8, 8));
        var subItems = new FrameworkElementFactory(typeof(StackPanel));
        subItems.SetValue(Panel.IsItemsHostProperty, true);
        subBorder.AppendChild(subItems);
        popup.AppendChild(subBorder);
        grid.AppendChild(popup);

        bd.AppendChild(grid);

        var tpl = new ControlTemplate(typeof(MenuItem)) { VisualTree = bd };
        var hover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, isDanger ? DangerSoftBrush : AccentSoftBrush, "bd"));
        tpl.Triggers.Add(hover);
        var hasItems = new Trigger { Property = MenuItem.HasItemsProperty, Value = true };
        hasItems.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "arrow"));
        tpl.Triggers.Add(hasItems);

        mi.Template = tpl;
        return mi;
    }

    public static Separator MenuDivider()
    {
        var sep = new Separator { IsHitTestVisible = false, Focusable = false };
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(FrameworkElement.HeightProperty, 1.0);
        bd.SetValue(Border.BackgroundProperty, LineBrush);
        bd.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 5, 2, 5));
        sep.Template = new ControlTemplate(typeof(Separator)) { VisualTree = bd };
        return sep;
    }
}
