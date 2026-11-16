using System.IO;
using DeskCal.Core;
using DeskCal.Data;
using DeskCal.Sync;

namespace DeskCal.App;

/// <summary>The Git sync settings as the settings window shows them.</summary>
internal sealed record GitSyncConfig(string Url, string Folder, int IntervalMinutes);

/// <summary>What the settings window needs for the "동기화" tab.</summary>
internal sealed record GitSyncHost(
    Func<GitSyncConfig> Get,
    Action<GitSyncConfig> Set,
    Func<Task<string>> RunNow,
    Func<string> Status,
    Action OpenFolder,
    Func<Action, Action>? Subscribe = null);

/// <summary>
/// Runs the Git sync (<see cref="GitCalendarSync"/>) in the background, one at a time, with the settings kept in the database, and
/// tells the app on the UI thread when a run is done. The checkout and the record of the last agreed state live in
/// %AppData%\DeskCal\git-sync, which belongs to this program.
/// </summary>
internal sealed class GitSyncCoordinator(DeskCalDb db, IClock clock, SettingsStore settings, string dataDir, string holidaysPath)
{
    private const string P = "gitsync.";
    public const int DefaultInterval = 15;

    public bool Running { get; private set; }

    /// <summary>Raised on the UI thread after every run: the report, or the error that stopped it.</summary>
    public event Action<GitSyncReport?, string?>? Finished;

    /// <summary>Raised on the UI thread whenever Running or LastResult changes.</summary>
    public event Action? StateChanged;

    public GitSyncConfig Config
    {
        get => new(settings.GetString(P + "url") ?? "",
                   settings.GetString(P + "folder") is { Length: > 0 } f ? f : GitSyncOptions.DefaultFolder,
                   settings.GetInt(P + "interval_min", DefaultInterval));
        set
        {
            settings.SetString(P + "url", value.Url.Trim());
            settings.SetString(P + "folder", value.Folder.Trim());
            settings.SetInt(P + "interval_min", Math.Max(0, value.IntervalMinutes));
        }
    }

    public bool Configured => Config.Url.Length > 0;

    public string WorkDir => Path.Combine(dataDir, "git-sync", "repo");

    public string LastResult => settings.GetString(P + "last_result") ?? "아직 동기화하지 않았습니다.";

    public async Task<(GitSyncReport? Report, string? Error)> RunAsync()
    {
        if (Running) return (null, "이미 동기화 중입니다.");
        var cfg = Config;
        if (cfg.Url.Length == 0) return (null, "저장소 주소를 먼저 입력하세요.");
        Running = true;
        StateChanged?.Invoke();
        GitSyncReport? report = null; string? error = null;
        try
        {
            var options = new GitSyncOptions(cfg.Url, cfg.Folder, WorkDir, Path.Combine(dataDir, "git-sync", "base.json"), Path.Combine(dataDir, "backup"));
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var engine = new GitCalendarSync(db, new CalendarSnapshot(db, clock, holidaysPath), new GitCli());
            report = await Task.Run(() => engine.RunAsync(options, cts.Token));
        }
        catch (GitException ex) { error = ex.Message; AppLog.Write("git sync: " + ex.Message + (ex.Detail is { } d ? "\n" + d : "")); }
        catch (OperationCanceledException) { error = "동기화가 너무 오래 걸려 중단했습니다."; }
        catch (Exception ex) { error = "동기화 중 예상하지 못한 오류: " + ex.Message; AppLog.Write("git sync: " + ex); }
        finally { Running = false; }

        string stamp = DateTime.Now.ToString("M월 d일 HH:mm", System.Globalization.CultureInfo.GetCultureInfo("ko-KR"));
        string text = error is null ? $"{stamp} 성공 - {report!.Summary()}" + (report.Warnings.Count > 0 ? "\n" + string.Join("\n", report.Warnings.Take(3)) : "")
                                    : $"{stamp} 실패 - {error}";
        settings.SetString(P + "last_result", text);
        Finished?.Invoke(report, error);
        StateChanged?.Invoke();
        return (report, error);
    }
}
