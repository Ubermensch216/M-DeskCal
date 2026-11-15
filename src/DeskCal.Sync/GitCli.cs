using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DeskCal.Sync;

/// <summary>A git command failed; <see cref="Exception.Message"/> is meant for the user, <see cref="Detail"/> is git's own output.</summary>
public sealed class GitException(string message, string? detail = null) : Exception(message)
{
    public string? Detail { get; } = detail;
}

public sealed record GitResult(int Exit, string Out, string Err)
{
    public bool Ok => Exit == 0;
    public string Both => (Out + "\n" + Err).Trim();
}

/// <summary>
/// Runs the git program that is installed on this computer, so it uses the user's own sign-in (Git Credential Manager, SSH keys);
/// this program never sees or stores a password. Line endings are passed through unchanged and paths are printed as they are.
/// </summary>
public sealed class GitCli
{
    private static readonly string[] Common = ["-c", "core.autocrlf=false", "-c", "core.safecrlf=false", "-c", "core.quotepath=false", "-c", "advice.detachedHead=false"];

    public string Executable { get; }

    public GitCli(string? executable = null) => Executable = executable ?? Find();

    /// <summary>"git" on the PATH, else the usual Git for Windows locations (a program started from Explorer may have a shorter PATH).</summary>
    private static string Find()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(dir.Trim('"'), "git.exe"))) return Path.Combine(dir.Trim('"'), "git.exe"); }
            catch (ArgumentException) { }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, "Git", "cmd", "git.exe");
            if (File.Exists(p)) return p;
        }
        return "git";
    }

    public async Task<GitResult> RunAsync(string workDir, IEnumerable<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in Common) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["LC_ALL"] = "C";            // messages in English, so a rejected push can be recognised

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Win32Exception) { throw new GitException("git 프로그램을 찾을 수 없습니다. Git for Windows(https://git-scm.com)를 설치한 뒤 다시 시도하세요."); }
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(3));
        try { await p.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new GitException("git 이 응답하지 않아 중단했습니다 (로그인 창이 떠 있다면 로그인한 뒤 다시 시도하세요).");
        }
        return new GitResult(p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <summary>Runs the command and returns its output; throws <see cref="GitException"/> with <paramref name="what"/> when it fails.</summary>
    public async Task<string> OkAsync(string workDir, string what, CancellationToken ct, params string[] args)
    {
        var r = await RunAsync(workDir, args, ct).ConfigureAwait(false);
        if (!r.Ok) throw new GitException($"{what}: {FirstLine(r.Err.Length > 0 ? r.Err : r.Out)}", r.Both);
        return r.Out;
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal))
        ?? text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "(출력 없음)";
}
