using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Foldy.Git;

/// <summary>GitCli 호출 옵션.</summary>
public sealed record GitRunOptions
{
    /// <summary>true 면 인증 프롬프트를 막는다 (GIT_TERMINAL_PROMPT=0). 백그라운드 호출 기본.</summary>
    public bool NonInteractive { get; init; } = true;
    /// <summary>true 면 --no-optional-locks 로 인덱스를 건드리지 않는다 (읽기 전용 백그라운드 호출).</summary>
    public bool NoOptionalLocks { get; init; }
    /// <summary>true 면 저장소별 쓰기 세마포어를 잡는다.</summary>
    public bool Write { get; init; }
    public string? StdIn { get; init; }
}

/// <summary>git.exe 프로세스 실행기 (설계서 5.7). 저장소당 쓰기 작업은 하나씩만 실행한다.</summary>
public sealed partial class GitCli
{
    private static readonly string[] BaseArgs = ["-c", "core.quotepath=false", "-c", "i18n.logOutputEncoding=UTF-8"];
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan PipeDrainGrace = TimeSpan.FromSeconds(2);

    private readonly Lazy<string?> _gitPath;

    public GitCli(string? gitExePath = null)
    {
        _gitPath = new Lazy<string?>(() => gitExePath ?? FindGitExe());
    }

    /// <summary>찾은 git.exe 전체 경로. 없으면 null.</summary>
    public string? GitPath => _gitPath.Value;
    public bool IsAvailable => GitPath is not null;

    /// <summary>PATH → Program Files\Git → 사용자 설치 순으로 git.exe 를 찾는다.</summary>
    public static string? FindGitExe()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "git.exe" } : new[] { "git" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var n in names)
            {
                try
                {
                    var p = Path.Combine(dir.Trim().Trim('"'), n);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
        }
        var candidates = new List<string>();
        foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            var pf = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "Git", "cmd", "git.exe"));
        }
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(local)) candidates.Add(Path.Combine(local, "Programs", "Git", "cmd", "git.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>git 을 실행한다. 비정상 종료는 Success=false 로, git 없음은 GitNotFound 로 돌려준다.</summary>
    public async Task<GitResult> RunAsync(
        string workDir, IReadOnlyList<string> args, GitRunOptions? options = null,
        IProgress<GitProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new GitRunOptions();
        if (GitPath is null) return GitResult.Fail(GitFailure.GitNotFound, "git.exe 를 찾을 수 없습니다.");

        if (!options.Write) return await RunCoreAsync(GitPath, workDir, args, options, progress, ct);

        var gate = WriteLocks.GetOrAdd(Path.GetFullPath(workDir).TrimEnd('\\', '/'), _ => new SemaphoreSlim(1, 1));
        try { await gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return GitResult.Fail(GitFailure.Cancelled, "취소됨"); }
        try { return await RunCoreAsync(GitPath, workDir, args, options, progress, ct); }
        finally { gate.Release(); }
    }

    private static async Task<GitResult> RunCoreAsync(
        string gitPath, string workDir, IReadOnlyList<string> args, GitRunOptions options,
        IProgress<GitProgress>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(gitPath)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            StandardInputEncoding = Utf8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (options.NoOptionalLocks) psi.ArgumentList.Add("--no-optional-locks");
        foreach (var a in BaseArgs) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["GIT_EDITOR"] = "true";   // 편집기가 뜨며 멈추는 것을 막는다
        if (options.NonInteractive)
        {
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "never";
        }

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return GitResult.Fail(GitFailure.GitNotFound, ex.Message);
        }

        using var reg = ct.Register(static p =>
        {
            try { ((Process)p!).Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        }, proc);

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();
        var stdoutTask = ReadAllAsync(proc.StandardOutput, stdoutBuf);
        var stderrTask = ReadStderrAsync(proc.StandardError, stderrBuf, progress);
        try
        {
            if (options.StdIn is not null) await proc.StandardInput.WriteAsync(options.StdIn);
            proc.StandardInput.Close();
        }
        catch (IOException) { /* 프로세스가 입력을 안 읽고 종료 */ }

        await proc.WaitForExitAsync(CancellationToken.None);
        // git 이 띄운 백그라운드 자식(gc --auto, fsmonitor 데몬 등)이 파이프 핸들을 물고 있으면
        // EOF 가 오지 않으므로, 종료 후에는 짧게만 기다리고 지금까지 읽은 내용으로 진행한다.
        await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(PipeDrainGrace, CancellationToken.None));
        string stdout, stderr;
        lock (stdoutBuf) stdout = stdoutBuf.ToString();
        lock (stderrBuf) stderr = stderrBuf.ToString();

        if (ct.IsCancellationRequested) return GitResult.Fail(GitFailure.Cancelled, "취소됨");
        return Classify(proc.ExitCode, stdout, stderr);
    }

    /// <summary>stderr 를 \r/\n 단위 줄로 읽어 진행률을 보고하고 전체 텍스트를 모은다.</summary>
    private static async Task ReadAllAsync(StreamReader reader, StringBuilder sink)
    {
        var buf = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buf)) > 0)
            lock (sink) sink.Append(buf, 0, n);
    }

    private static async Task ReadStderrAsync(StreamReader reader, StringBuilder all, IProgress<GitProgress>? progress)
    {
        var line = new StringBuilder();
        var buf = new char[1024];
        int n;
        while ((n = await reader.ReadAsync(buf)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                var c = buf[i];
                if (c is '\r' or '\n')
                {
                    if (line.Length > 0)
                    {
                        var s = line.ToString();
                        // 진행 줄(\r 로 끝나는 것)은 마지막 상태만 의미 있으므로 최종 텍스트에는 일반 줄만 보존
                        if (c == '\n' || !ProgressParser.TryParse(s, out _)) { lock (all) all.Append(s).Append('\n'); }
                        if (progress is not null && ProgressParser.TryParse(s, out var gp)) progress.Report(gp);
                        line.Clear();
                    }
                }
                else line.Append(c);
            }
        }
        if (line.Length > 0) { lock (all) all.Append(line).Append('\n'); }
    }

    internal static GitResult Classify(int exitCode, string stdout, string stderr)
    {
        if (exitCode == 0) return new GitResult(true, GitFailure.None, 0, stdout, stderr);

        var failure = GitFailure.Other;
        string? safeDir = null;
        if (stderr.Contains("detected dubious ownership", StringComparison.OrdinalIgnoreCase))
        {
            failure = GitFailure.DubiousOwnership;
            var m = SafeDirRegex().Match(stderr);
            if (m.Success) safeDir = m.Groups[1].Value.Trim().Trim('\'', '"');
        }
        else if (stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            failure = GitFailure.NotARepository;
        else if (stderr.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
                 || stderr.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
                 || stderr.Contains("Permission denied (publickey", StringComparison.OrdinalIgnoreCase)
                 || stderr.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase))
            failure = GitFailure.AuthenticationFailed;
        else if (stdout.Contains("CONFLICT", StringComparison.Ordinal) || stderr.Contains("CONFLICT", StringComparison.Ordinal)
                 || stderr.Contains("Resolve all conflicts", StringComparison.OrdinalIgnoreCase)
                 || stderr.Contains("unmerged files", StringComparison.OrdinalIgnoreCase))
            failure = GitFailure.MergeConflict;
        else if (stdout.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
                 || stdout.Contains("nothing added to commit", StringComparison.OrdinalIgnoreCase))
            failure = GitFailure.NothingToCommit;

        return new GitResult(false, failure, exitCode, stdout, stderr) { SafeDirectoryCandidate = safeDir };
    }

    // git 안내문: "git config --global --add safe.directory C:/path/to/repo"
    [GeneratedRegex(@"safe\.directory\s+(.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SafeDirRegex();
}

/// <summary>git --progress 의 stderr 줄 파서. "Receiving objects:  45% (45/100)" → (Receiving objects, 45).</summary>
public static partial class ProgressParser
{
    [GeneratedRegex(@"^(?:remote:\s*)?([A-Za-z][A-Za-z ]*?):\s+(\d{1,3})%", RegexOptions.CultureInvariant)]
    private static partial Regex Line();

    public static bool TryParse(string line, out GitProgress progress)
    {
        var m = Line().Match(line.TrimStart());
        if (m.Success && int.TryParse(m.Groups[2].Value, out var pct) && pct is >= 0 and <= 100)
        {
            progress = new GitProgress(m.Groups[1].Value, pct, line);
            return true;
        }
        progress = default;
        return false;
    }
}
