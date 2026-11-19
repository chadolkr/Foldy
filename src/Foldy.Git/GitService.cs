using System.Globalization;
using System.Runtime.CompilerServices;

namespace Foldy.Git;

/// <summary><see cref="IGitService"/> 의 git.exe CLI 구현.</summary>
public sealed class GitService : IGitService
{
    private readonly GitCli _cli;
    private readonly GitRepoLocator _locator;

    public GitService(GitCli? cli = null, GitRepoLocator? locator = null)
    {
        _cli = cli ?? new GitCli();
        _locator = locator ?? new GitRepoLocator();
    }

    public GitCli Cli => _cli;
    public bool IsGitAvailable => _cli.IsAvailable;
    public GitRepoInfo? FindRepo(string folder) => _locator.FindRepo(folder);

    private static readonly GitRunOptions ReadOpts = new() { NoOptionalLocks = true };
    private static readonly GitRunOptions WriteOpts = new() { Write = true };
    // 사용자가 직접 누른 fetch/pull/push 는 자격 증명 관리자(GCM) 창이 뜰 수 있게 허용한다
    private static readonly GitRunOptions InteractiveWriteOpts = new() { Write = true, NonInteractive = false };

    // ── 읽기 ──

    public async Task<GitResult<RepoStatus>> GetStatusAsync(string repo, bool includeIgnored = false, CancellationToken ct = default)
    {
        var args = new List<string> { "status", "--porcelain=v2", "-z", "--branch" };
        args.Add(includeIgnored ? "--ignored=traditional" : "--ignored=no");
        var r = await _cli.RunAsync(repo, args, ReadOpts, null, ct);
        return new GitResult<RepoStatus>(r, r.Success ? GitStatusParser.Parse(r.Output) : null);
    }

    public async IAsyncEnumerable<CommitNode> GetLogAsync(string repo, LogFilter filter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var r = await _cli.RunAsync(repo, GitLogParser.BuildArgs(filter), ReadOpts, null, ct);
        if (!r.Success) yield break;
        foreach (var c in GitLogParser.Parse(r.Output))
        {
            ct.ThrowIfCancellationRequested();
            yield return c;
        }
    }

    public async Task<GitResult<IReadOnlyList<GitBranch>>> GetBranchesAsync(string repo, CancellationToken ct = default)
    {
        string[] args =
        [
            "for-each-ref", "--sort=refname",
            "--format=%(refname)%1f%(HEAD)%1f%(upstream:short)%1f%(upstream:track,nobracket)%1f%(objectname)",
            "refs/heads", "refs/remotes",
        ];
        var r = await _cli.RunAsync(repo, args, ReadOpts, null, ct);
        return new GitResult<IReadOnlyList<GitBranch>>(r, r.Success ? ParseBranches(r.Output) : null);
    }

    internal static IReadOnlyList<GitBranch> ParseBranches(string output)
    {
        var list = new List<GitBranch>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\u001f');
            if (f.Length < 5) continue;
            var full = f[0];
            bool remote;
            string name;
            if (full.StartsWith("refs/heads/", StringComparison.Ordinal)) { remote = false; name = full["refs/heads/".Length..]; }
            else if (full.StartsWith("refs/remotes/", StringComparison.Ordinal)) { remote = true; name = full["refs/remotes/".Length..]; }
            else continue;
            if (remote && name.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            ParseTrack(f[3], out var ahead, out var behind);
            list.Add(new GitBranch(name, f[1] == "*", remote, f[2].Length == 0 ? null : f[2], ahead, behind, f[4]));
        }
        return list;
    }

    /// <summary>"ahead 2, behind 1" / "ahead 2" / "behind 1" / "gone".</summary>
    internal static void ParseTrack(string track, out int ahead, out int behind)
    {
        ahead = behind = 0;
        foreach (var part in track.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var sp = part.Split(' ');
            if (sp.Length != 2 || !int.TryParse(sp[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
            if (sp[0] == "ahead") ahead = n; else if (sp[0] == "behind") behind = n;
        }
    }

    public async Task<GitResult<IReadOnlyList<GitStash>>> GetStashesAsync(string repo, CancellationToken ct = default)
    {
        string[] args = ["stash", "list", "-z", "--format=%gd%x1f%H%x1f%at%x1f%gs"];
        var r = await _cli.RunAsync(repo, args, ReadOpts, null, ct);
        return new GitResult<IReadOnlyList<GitStash>>(r, r.Success ? ParseStashes(r.Output) : null);
    }

    internal static IReadOnlyList<GitStash> ParseStashes(string output)
    {
        var list = new List<GitStash>();
        foreach (var rec in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = rec.TrimStart('\n', '\r').Split('\u001f');
            if (f.Length < 4) continue;
            var date = long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                ? DateTimeOffset.FromUnixTimeSeconds(s) : default;
            list.Add(new GitStash(f[0], f[1], date, f[3]));
        }
        return list;
    }

    // ── diff / blame ──

    public async Task<GitResult<FileDiff>> GetDiffAsync(string repo, GitDiffTarget target, int maxLines = GitDiffParser.DefaultMaxLines, CancellationToken ct = default)
    {
        var r = await _cli.RunAsync(repo, BuildDiffArgs(target), ReadOpts, null, ct);
        // --no-index 는 차이가 있으면 종료 코드 1 이다. 출력이 있으면 성공으로 본다.
        if (!r.Success && target.Untracked && r.ExitCode == 1 && r.Output.Length > 0) r = GitResult.Ok(r.Output);
        if (!r.Success) return new GitResult<FileDiff>(r, null);
        var output = r.Output;
        var diff = output.Length < 4096 ? GitDiffParser.Parse(output, maxLines) : await Task.Run(() => GitDiffParser.Parse(output, maxLines), ct);
        return new GitResult<FileDiff>(r, diff);
    }

    internal static List<string> BuildDiffArgs(GitDiffTarget t)
    {
        var args = new List<string>();
        switch (t.Kind)
        {
            case GitDiffKind.Commit:
                args.AddRange(["show", "--no-color", "--no-ext-diff", "--format=", "-m", "--first-parent", "-M"]);
                args.Add(t.Hash ?? "HEAD");
                break;
            case GitDiffKind.Staged:
                args.AddRange(["diff", "--no-color", "--no-ext-diff", "-M", "--cached"]);
                break;
            default:
                args.AddRange(["diff", "--no-color", "--no-ext-diff"]);
                if (t.Untracked) args.Add("--no-index");
                break;
        }
        args.Add("--");
        if (t.Untracked) args.Add("/dev/null");
        else if (t.OrigPath is not null) args.Add(t.OrigPath);
        args.Add(t.Path);
        return args;
    }

    public async Task<GitResult<IReadOnlyList<BlameLine>>> GetBlameAsync(string repo, string path, string? revision = null, CancellationToken ct = default)
    {
        var args = new List<string> { "blame", "--porcelain" };
        if (!string.IsNullOrEmpty(revision)) args.Add(revision);
        args.Add("--");
        args.Add(path);
        var r = await _cli.RunAsync(repo, args, ReadOpts, null, ct);
        if (!r.Success) return new GitResult<IReadOnlyList<BlameLine>>(r, null);
        var output = r.Output;
        var lines = await Task.Run(() => GitBlameParser.Parse(output), ct);
        return new GitResult<IReadOnlyList<BlameLine>>(r, lines);
    }

    // ── 쓰기 ──

    public Task<GitResult> StageAsync(string repo, IReadOnlyList<string> paths, CancellationToken ct = default)
        => paths.Count == 0 ? Task.FromResult(GitResult.Ok()) : _cli.RunAsync(repo, WithPaths(["add", "-A"], paths), WriteOpts, null, ct);

    public Task<GitResult> UnstageAsync(string repo, IReadOnlyList<string> paths, CancellationToken ct = default)
        => paths.Count == 0 ? Task.FromResult(GitResult.Ok()) : _cli.RunAsync(repo, WithPaths(["restore", "--staged"], paths), WriteOpts, null, ct);

    private static List<string> WithPaths(IEnumerable<string> head, IReadOnlyList<string> paths)
    {
        var l = new List<string>(head) { "--" };
        l.AddRange(paths);
        return l;
    }

    public Task<GitResult> CommitAsync(string repo, string message, bool amend = false, CancellationToken ct = default)
    {
        var args = new List<string> { "commit" };
        if (amend) args.Add("--amend");
        if (string.IsNullOrWhiteSpace(message))
        {
            if (!amend) return Task.FromResult(GitResult.Fail(GitFailure.Other, "커밋 메시지가 비어 있습니다."));
            args.Add("--no-edit");
        }
        else { args.Add("-m"); args.Add(message); }
        return _cli.RunAsync(repo, args, WriteOpts, null, ct);
    }

    public Task<GitResult> FetchAsync(string repo, IProgress<GitProgress>? progress = null, bool interactive = false, CancellationToken ct = default)
        => _cli.RunAsync(repo, ["fetch", "--all", "--prune", "--progress"], interactive ? InteractiveWriteOpts : WriteOpts, progress, ct);

    public Task<GitResult> PullAsync(string repo, PullMode mode, IProgress<GitProgress>? progress = null, CancellationToken ct = default)
        => _cli.RunAsync(repo, ["pull", mode == PullMode.Rebase ? "--rebase" : "--no-rebase", "--no-edit", "--progress"],
            InteractiveWriteOpts, progress, ct);

    public Task<GitResult> PushAsync(string repo, bool setUpstream, bool forceWithLease, IProgress<GitProgress>? progress = null, CancellationToken ct = default)
    {
        var args = new List<string> { "push", "--progress" };
        if (forceWithLease) args.Add("--force-with-lease"); // --force 는 의도적으로 제공하지 않는다
        if (setUpstream) { args.Add("--set-upstream"); args.Add("origin"); args.Add("HEAD"); }
        return _cli.RunAsync(repo, args, InteractiveWriteOpts, progress, ct);
    }

    public Task<GitResult> StashPushAsync(string repo, string? message = null, bool includeUntracked = true, CancellationToken ct = default)
    {
        var args = new List<string> { "stash", "push" };
        if (includeUntracked) args.Add("--include-untracked");
        if (!string.IsNullOrWhiteSpace(message)) { args.Add("-m"); args.Add(message); }
        return _cli.RunAsync(repo, args, WriteOpts, null, ct);
    }

    public Task<GitResult> StashPopAsync(string repo, string? stashName = null, CancellationToken ct = default)
    {
        var args = new List<string> { "stash", "pop" };
        if (!string.IsNullOrEmpty(stashName)) args.Add(stashName);
        return _cli.RunAsync(repo, args, WriteOpts, null, ct);
    }

    public Task<GitResult> CreateBranchAsync(string repo, string name, string? startPoint = null, bool checkout = true, CancellationToken ct = default)
    {
        var args = checkout ? new List<string> { "switch", "-c", name } : new List<string> { "branch", name };
        if (!string.IsNullOrEmpty(startPoint)) args.Add(startPoint);
        return _cli.RunAsync(repo, args, WriteOpts, null, ct);
    }

    // 원격 브랜치 이름(origin/x)이면 git switch 가 추적 브랜치를 자동 생성한다
    public Task<GitResult> CheckoutAsync(string repo, string branch, CancellationToken ct = default)
        => _cli.RunAsync(repo, ["switch", branch], WriteOpts, null, ct);

    public Task<GitResult> InitAsync(string folder, CancellationToken ct = default)
        => _cli.RunAsync(folder, ["init"], WriteOpts, null, ct);

    public Task<GitResult> AddSafeDirectoryAsync(string path, CancellationToken ct = default)
        => AddSafeDirectoryCoreAsync(_cli, path, ct);

    /// <summary>git config --global --add safe.directory. 이미 등록돼 있으면 중복 추가하지 않는다.</summary>
    internal static async Task<GitResult> AddSafeDirectoryCoreAsync(GitCli cli, string path, CancellationToken ct)
    {
        var norm = path.Replace('\\', '/').TrimEnd('/');
        var cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = await cli.RunAsync(cwd, ["config", "--global", "--get-all", "safe.directory"], null, null, ct);
        if (list.Success && list.Output.Split('\n').Any(l => string.Equals(l.Trim(), norm, StringComparison.OrdinalIgnoreCase)))
            return GitResult.Ok("이미 신뢰한 저장소입니다.");
        return await cli.RunAsync(cwd, ["config", "--global", "--add", "safe.directory", norm], new GitRunOptions { Write = true }, null, ct);
    }
}
