using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.Sync;

/// <summary>
/// Where the calendar is shared: a git repository (<see cref="RepoUrl"/>), its branch (empty = the repository's default branch) and the
/// folder inside it that belongs to the calendar. <see cref="WorkDir"/> is this program's own checkout of that folder,
/// <see cref="BaseFile"/> remembers what both sides agreed on at the last sync, <see cref="BackupDir"/> receives a database copy
/// before a sync changes anything here.
/// </summary>
public sealed record GitSyncOptions(string RepoUrl, string Branch, string Folder, string WorkDir, string BaseFile, string BackupDir)
{
    public const string DefaultFolder = "my_calendar_sync";
}

public sealed record GitSyncReport(
    int Received, int RemovedHere, int Sent, int RemovedThere, int Conflicts, int Skipped, bool HolidaysChanged, bool Pushed,
    IReadOnlyList<string> Warnings)
{
    public bool ChangedLocal => Received > 0 || RemovedHere > 0 || HolidaysChanged;

    public string Summary()
    {
        if (Received + RemovedHere + Sent + RemovedThere == 0 && Conflicts == 0) return "바뀐 것이 없습니다.";
        var parts = new List<string>();
        if (Received > 0) parts.Add($"받음 {Received}");
        if (RemovedHere > 0) parts.Add($"여기서 지움 {RemovedHere}");
        if (Sent > 0) parts.Add($"올림 {Sent}");
        if (RemovedThere > 0) parts.Add($"저장소에서 지움 {RemovedThere}");
        if (Conflicts > 0) parts.Add($"양쪽에서 고친 것 {Conflicts} (나중 것을 남김)");
        if (Skipped > 0) parts.Add($"다음에 다시 {Skipped}");
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Keeps the calendar in a folder of a git repository so several computers share it. One sync: fetch, put the checkout exactly on
/// the remote branch, read the folder, merge it with this computer's calendar record by record (<see cref="GitMerge"/>), write what
/// came from elsewhere into the database and what changed here into the folder, commit, push. When somebody else pushed in between,
/// the whole round is repeated on top of their commit. Only the calendar folder is checked out and committed; the rest of the
/// repository is never touched.
/// </summary>
public sealed class GitCalendarSync(DeskCalDb db, CalendarSnapshot snapshot, GitCli git)
{
    private const int FormatVersion = 1;
    private const int Attempts = 3;
    private static readonly Regex SafeId = new("^[A-Za-z0-9_-]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex SafeFolder = new(@"^[^\\/:*?""<>|.\s][^\\/:*?""<>|]{0,79}$", RegexOptions.Compiled);

    public static bool IsValidFolder(string folder) => SafeFolder.IsMatch(folder) && !folder.EndsWith('.') && !folder.EndsWith(' ');

    public async Task<GitSyncReport> RunAsync(GitSyncOptions o, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.RepoUrl)) throw new GitException("저장소 주소를 입력하세요.");
        if (!IsValidFolder(o.Folder)) throw new GitException("폴더 이름에는 / \\ : * ? \" < > | 를 쓸 수 없고 . 으로 시작할 수 없습니다.");

        await EnsureCloneAsync(o, ct).ConfigureAwait(false);
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            string branch = await PrepareAsync(o, ct).ConfigureAwait(false);
            var (report, push) = Merge(o, branch);
            if (push is null) return report;

            var r = await git.RunAsync(o.WorkDir, ["push", "--porcelain", "origin", "HEAD:refs/heads/" + branch], ct).ConfigureAwait(false);
            if (r.Ok) { push(); return report with { Pushed = true }; }
            bool overtaken = r.Both.Contains("rejected", StringComparison.OrdinalIgnoreCase) || r.Both.Contains("fetch first", StringComparison.OrdinalIgnoreCase)
                             || r.Both.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase);
            if (!overtaken || attempt >= Attempts)
                throw new GitException(overtaken ? "다른 컴퓨터가 계속 먼저 올려서 이번에는 올리지 못했습니다. 잠시 뒤 다시 시도하세요."
                                                 : "저장소에 올리지 못했습니다: " + Short(r.Err), r.Both);
            // somebody pushed in between: start over on top of their commit (what we already took from them is now unchanged here)
        }
    }

    // ---- the checkout ----------------------------------------------------------------------------

    private async Task EnsureCloneAsync(GitSyncOptions o, CancellationToken ct)
    {
        if (Directory.Exists(Path.Combine(o.WorkDir, ".git")))
        {
            var url = await git.RunAsync(o.WorkDir, ["remote", "get-url", "origin"], ct).ConfigureAwait(false);
            if (url.Ok && url.Out.Trim() == o.RepoUrl) return;
            if (url.Ok) { await git.OkAsync(o.WorkDir, "저장소 주소를 바꾸지 못했습니다", ct, "remote", "set-url", "origin", o.RepoUrl).ConfigureAwait(false); return; }
        }
        if (Directory.Exists(o.WorkDir)) DeleteTree(o.WorkDir);        // our own folder, in a broken state: start again
        string parent = Path.GetDirectoryName(Path.GetFullPath(o.WorkDir))!;
        Directory.CreateDirectory(parent);
        var r = await git.RunAsync(parent, ["clone", "--filter=blob:none", "--no-checkout", "--origin", "origin", "--", o.RepoUrl, o.WorkDir], ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        if (!r.Ok)
        {
            if (Directory.Exists(o.WorkDir)) DeleteTree(o.WorkDir);
            throw new GitException("저장소를 받아오지 못했습니다: " + Short(r.Err) + Hint(r.Err), r.Both);
        }
    }

    /// <summary>Fetch and put the checkout exactly on the remote branch (discarding anything a failed earlier round left behind). Returns the branch.</summary>
    private async Task<string> PrepareAsync(GitSyncOptions o, CancellationToken ct)
    {
        var fetch = await git.RunAsync(o.WorkDir, ["fetch", "--prune", "origin"], ct).ConfigureAwait(false);
        if (!fetch.Ok) throw new GitException("저장소에서 받아오지 못했습니다: " + Short(fetch.Err) + Hint(fetch.Err), fetch.Both);

        string branch = o.Branch.Trim();
        if (branch.Length == 0)
        {
            var head = await git.RunAsync(o.WorkDir, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], ct).ConfigureAwait(false);
            branch = head.Ok && head.Out.Trim().StartsWith("origin/", StringComparison.Ordinal) ? head.Out.Trim()["origin/".Length..] : "main";
        }

        await git.OkAsync(o.WorkDir, "체크아웃 범위를 정하지 못했습니다", ct, "sparse-checkout", "set", "--no-cone", "/" + o.Folder + "/").ConfigureAwait(false);
        bool remoteHas = (await git.RunAsync(o.WorkDir, ["rev-parse", "--verify", "--quiet", "refs/remotes/origin/" + branch], ct).ConfigureAwait(false)).Ok;
        bool anyHead = (await git.RunAsync(o.WorkDir, ["rev-parse", "--verify", "--quiet", "refs/remotes/origin/HEAD"], ct).ConfigureAwait(false)).Ok;
        if (remoteHas)
            await git.OkAsync(o.WorkDir, "저장소 내용을 꺼내지 못했습니다", ct, "checkout", "--force", "-B", branch, "refs/remotes/origin/" + branch).ConfigureAwait(false);
        else if (anyHead)       // a new branch: start it from the repository's default branch
            await git.OkAsync(o.WorkDir, "새 브랜치를 만들지 못했습니다", ct, "checkout", "--force", "-B", branch, "refs/remotes/origin/HEAD").ConfigureAwait(false);
        else                    // an empty repository: the first commit starts the branch
            await git.OkAsync(o.WorkDir, "빈 저장소를 준비하지 못했습니다", ct, "symbolic-ref", "HEAD", "refs/heads/" + branch).ConfigureAwait(false);
        await git.RunAsync(o.WorkDir, ["clean", "-fdx", "-q", "--", o.Folder], ct).ConfigureAwait(false);
        return branch;
    }

    // ---- one round of merging ----------------------------------------------------------------------

    /// <summary>
    /// Merges and commits. Returns the report and, when a push is needed, an action that records the agreed state once the push
    /// went through (null when there is nothing to push - the agreed state is then recorded at once).
    /// </summary>
    private (GitSyncReport Report, Action? AfterPush) Merge(GitSyncOptions o, string branch)
    {
        string root = Path.Combine(o.WorkDir, o.Folder);
        var warnings = new List<string>();
        CheckFormat(root);

        // the folder
        var remote = new Dictionary<string, SyncDoc>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);       // keys whose file is broken: left alone on both sides
        void Read(string kind, string file)
        {
            string fallbackKey = kind is CalendarSnapshot.Timetable or CalendarSnapshot.Holidays ? kind : kind + "/" + Path.GetFileNameWithoutExtension(file);
            try
            {
                var doc = CalendarSnapshot.ParseRemote(kind, File.ReadAllText(file, Encoding.UTF8));
                if (!remote.TryAdd(doc.Key, doc)) { warnings.Add($"같은 항목이 두 파일에 있어 하나만 읽었습니다: {Rel(root, file)}"); return; }
                paths[doc.Key] = file;
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                unreadable.Add(fallbackKey);
                warnings.Add($"읽을 수 없는 파일을 건너뛰었습니다: {Rel(root, file)} ({ex.Message})");
            }
        }
        foreach (var kind in CalendarSnapshot.Folders)
        {
            var dir = Path.Combine(root, kind);
            if (Directory.Exists(dir)) foreach (var f in Directory.EnumerateFiles(dir, "*.json")) Read(kind, f);
        }
        if (File.Exists(Path.Combine(root, "timetable.json"))) Read(CalendarSnapshot.Timetable, Path.Combine(root, "timetable.json"));
        if (File.Exists(Path.Combine(root, "holidays.json"))) Read(CalendarSnapshot.Holidays, Path.Combine(root, "holidays.json"));

        // this computer, and what both agreed on last time
        var local = snapshot.Collect();
        var baseState = BaseStore.Load(o.BaseFile, o.RepoUrl, branch, o.Folder);
        var localDocs = local.Docs.Where(kv => !unreadable.Contains(kv.Key)).ToDictionary();
        var baseHashes = baseState.Where(kv => !unreadable.Contains(kv.Key)).ToDictionary();

        var plan = GitMerge.Plan(localDocs, local.DeletedAt, remote, baseHashes);

        // into the database
        SnapshotApplyResult applied = new(new HashSet<string>(), new HashSet<string>(), false);
        if (plan.ChangesLocal)
        {
            Backup(o.BackupDir);
            applied = snapshot.Apply(plan.ApplyLocal, plan.DeleteLocal, localDocs.ToDictionary(kv => kv.Key, kv => kv.Value.Hash));
        }

        // into the folder
        foreach (var d in plan.WriteRemote)
            CalendarSnapshot.WriteFileAtomic(paths.TryGetValue(d.Key, out var existing) ? existing : PathFor(root, d.Key), d.Text);
        foreach (var key in plan.DeleteRemote)
            if (paths.TryGetValue(key, out var file) && File.Exists(file)) File.Delete(file);
        WriteHousekeeping(root);

        // the agreed state: a record skipped because it was edited meanwhile keeps its old base, so the next sync sees the edit
        var agreed = new Dictionary<string, string>(plan.Agreed, StringComparer.Ordinal);
        foreach (var key in applied.Skipped.Concat(unreadable))
        {
            if (baseState.TryGetValue(key, out var old)) agreed[key] = old; else agreed.Remove(key);
        }

        int received = plan.ApplyLocal.Count(d => applied.Done.Contains(d.Key)), removedHere = plan.DeleteLocal.Count(applied.Done.Contains);
        var report = new GitSyncReport(received, removedHere, plan.WriteRemote.Count, plan.DeleteRemote.Count, plan.Conflicts, applied.Skipped.Count,
            applied.HolidaysChanged, false, warnings);
        void Record() => BaseStore.Save(o.BaseFile, o.RepoUrl, branch, o.Folder, agreed);

        if (!CommitIfChanged(o, report)) { Record(); return (report, null); }
        return (report, Record);
    }

    private bool CommitIfChanged(GitSyncOptions o, GitSyncReport report)
    {
        // synchronous on purpose: these are quick local commands, and the caller already runs off the UI thread
        Run(o, "변경 내용을 담지 못했습니다", "add", "--all", "--", o.Folder);
        var diff = git.RunAsync(o.WorkDir, ["diff", "--cached", "--quiet", "--", o.Folder], CancellationToken.None).GetAwaiter().GetResult();
        if (diff.Ok) return false;

        var args = new List<string>();
        var email = git.RunAsync(o.WorkDir, ["config", "user.email"], CancellationToken.None).GetAwaiter().GetResult();
        if (!email.Ok || email.Out.Trim().Length == 0) args.AddRange(["-c", "user.name=M-DeskCal", "-c", "user.email=m-deskcal@localhost"]);
        string message = $"캘린더 동기화 ({Environment.MachineName}): 올림 {report.Sent}, 지움 {report.RemovedThere}";
        args.AddRange(["commit", "--quiet", "-m", message, "--", o.Folder]);
        Run(o, "커밋하지 못했습니다", [.. args]);
        return true;
    }

    private void Run(GitSyncOptions o, string what, params string[] args) =>
        git.OkAsync(o.WorkDir, what, CancellationToken.None, args).GetAwaiter().GetResult();

    // ---- the folder layout ----------------------------------------------------------------------------

    /// <summary>"items/&lt;id&gt;" → root/items/&lt;id&gt;.json (an id that is not safe as a file name gets a hashed name; the id itself is inside the file).</summary>
    internal static string PathFor(string root, string key)
    {
        if (key == CalendarSnapshot.Timetable) return Path.Combine(root, "timetable.json");
        if (key == CalendarSnapshot.Holidays) return Path.Combine(root, "holidays.json");
        string kind = CalendarSnapshot.KindOf(key), id = key[(kind.Length + 1)..];
        string name = SafeId.IsMatch(id) ? id : "x-" + GitMerge.HashOf(id)[..40];
        return Path.Combine(root, kind, name + ".json");
    }

    private static void CheckFormat(string root)
    {
        var path = Path.Combine(root, "format.json");
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("version", out var v) && v.TryGetInt32(out int version) && version > FormatVersion)
                throw new GitException("저장소의 캘린더 폴더는 이 프로그램보다 새로운 버전이 만든 형식입니다. 프로그램을 업데이트하세요.");
        }
        catch (JsonException) { /* rewritten below */ }
    }

    private static void WriteHousekeeping(string root)
    {
        Directory.CreateDirectory(root);
        WriteIfDifferent(Path.Combine(root, "format.json"), $"{{\n  \"format\": \"M-DeskCal-sync\",\n  \"version\": {FormatVersion}\n}}\n");
        WriteIfDifferent(Path.Combine(root, "README.md"), Readme);
    }

    private static void WriteIfDifferent(string path, string text)
    {
        try { if (File.Exists(path) && File.ReadAllText(path) == text) return; } catch (IOException) { }
        CalendarSnapshot.WriteFileAtomic(path, text);
    }

    private const string Readme = """
        # M-DeskCal 캘린더 동기화 폴더

        이 폴더는 M-DeskCal 이 관리합니다. 여러 컴퓨터의 M-DeskCal 이 이 폴더를 통해 같은 달력을 씁니다.
        각 컴퓨터는 동기화할 때마다 이 폴더를 받아 자기 달력과 합치고, 바뀐 것을 다시 올립니다.

        | 위치 | 내용 |
        |---|---|
        | `items/` | 날짜 칸의 할 일 (알림 포함) |
        | `events/` | 일정 (반복 규칙·건너뛴 날·알림 포함) |
        | `countdowns/` | 디데이 |
        | `notes/` | 메모 |
        | `series/` | 할 일의 반복 규칙 |
        | `timetable.json` | 시간표 |
        | `holidays.json` | 휴일 데이터 수정 파일 |

        - 파일 하나가 항목 하나입니다. 지운 항목은 파일이 사라집니다.
        - 직접 고쳐도 되지만, 형식이 틀린 파일은 건너뜁니다. 되도록 프로그램에서 고치세요.
        - 창 위치·표시 설정·AI 설정·잠금 비밀번호는 컴퓨터마다 따로이며 여기에 올라오지 않습니다.
        - 일정 내용이 그대로 들어 있으니 이 저장소는 **비공개**로 두세요.

        """;

    // ---- small helpers ------------------------------------------------------------------------------

    private void Backup(string dir)
    {
        Directory.CreateDirectory(dir);
        db.Backup(Path.Combine(dir, $"before-gitsync-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.db"));
        foreach (var old in new DirectoryInfo(dir).GetFiles("before-gitsync-*.db").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(10))
            try { old.Delete(); } catch (IOException) { }
    }

    private static string Rel(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string Short(string err)
    {
        var line = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal) || l.StartsWith("remote:", StringComparison.Ordinal))
            ?? err.Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    private static string Hint(string err) =>
        err.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) || err.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            ? "\n이 컴퓨터의 git 에 로그인되어 있지 않습니다. 명령 창에서 'git clone <주소>' 를 한 번 실행해 로그인하세요."
        : err.Contains("not found", StringComparison.OrdinalIgnoreCase) || err.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            ? "\n주소가 맞는지, 이 계정이 저장소에 접근할 수 있는지 확인하세요."
        : "";

    private static void DeleteTree(string dir)
    {
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal;   // git marks its objects read-only
        Directory.Delete(dir, recursive: true);
    }
}

/// <summary>What both sides held after the last successful sync, per record (hash), for one repository/branch/folder.</summary>
internal static class BaseStore
{
    public static Dictionary<string, string> Load(string path, string url, string branch, string folder)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var e = doc.RootElement;
            if (S(e, "remote") != url || S(e, "branch") != branch || S(e, "folder") != folder) return new(StringComparer.Ordinal);   // another place: a first sync
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (e.TryGetProperty("hashes", out var h) && h.ValueKind == JsonValueKind.Object)
                foreach (var p in h.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString()!;
            return map;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(StringComparer.Ordinal); }

        static string? S(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    public static void Save(string path, string url, string branch, string folder, IReadOnlyDictionary<string, string> hashes)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("remote", url); w.WriteString("branch", branch); w.WriteString("folder", folder);
            w.WriteStartObject("hashes");
            foreach (var (k, v) in hashes.OrderBy(kv => kv.Key, StringComparer.Ordinal)) w.WriteString(k, v);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        CalendarSnapshot.WriteFileAtomic(path, Encoding.UTF8.GetString(ms.ToArray()));
    }
}
