using System.Windows.Threading;
using DeskCal.Data;
using Microsoft.Win32;

namespace DeskCal.App;

/// <summary>
/// Wakes up at the next reminder time. A single timer is re-armed after every tick, edit, clock change or resume
/// from sleep; it never waits longer than an hour so a missed notification cannot leave it asleep forever.
/// </summary>
internal sealed class ReminderService : IDisposable
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    private readonly ReminderProcessor _processor;
    private readonly Action<IReadOnlyList<ReminderMessage>> _show;
    private readonly TimeSpan _missedWindow;
    private readonly DispatcherTimer _timer = new();

    public ReminderService(ReminderProcessor processor, Action<IReadOnlyList<ReminderMessage>> show, TimeSpan missedWindow)
    {
        _processor = processor; _show = show; _missedWindow = missedWindow;
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        Tick();                                  // catches up on anything missed while the app was not running
    }

    /// <summary>Call after reminders were added, edited or removed.</summary>
    public void Reschedule() => Schedule();

    private void Tick()
    {
        _timer.Stop();
        try
        {
            var due = _processor.CollectDue(DateTime.Now, _missedWindow);
            if (due.Count > 0) _show(due);
        }
        catch (Exception ex) { Log("reminder tick failed: " + ex); }
        Schedule();
    }

    private void Schedule()
    {
        _timer.Stop();
        try
        {
            var now = DateTime.Now;
            var next = _processor.NextWake(now);
            var wait = next is { } t ? t - now + TimeSpan.FromMilliseconds(300) : MaxWait;
            _timer.Interval = wait < TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : wait > MaxWait ? MaxWait : wait;
        }
        catch (Exception ex) { Log("reminder schedule failed: " + ex); _timer.Interval = MaxWait; }
        _timer.Start();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _timer.Dispatcher.BeginInvoke(Tick);
    }

    private void OnTimeChanged(object? sender, EventArgs e) => _timer.Dispatcher.BeginInvoke(Tick);

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        _timer.Stop();
    }

    private static void Log(string text) => AppLog.Write(text);
}
