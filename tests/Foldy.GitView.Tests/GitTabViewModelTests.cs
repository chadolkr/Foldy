using System.Runtime.CompilerServices;
using Foldy.Git;
using Foldy.Presentation.Git;
using Foldy.Presentation.Tabs;

namespace Foldy.GitView.Tests;

/// <summary>메모리 안에서 동작하는 가짜 IGitService. 호출 기록과 실패 주입을 지원한다.</summary>
internal sealed class FakeGit : IGitService
{
    public List<string> Calls { get; } = [];
    public List<GitFileEntry> Files { get; set; } = [];
    public List<CommitNode> Log { get; set; } = [];
    public string? Branch { get; set; } = "main";
    public string? Upstream { get; set; } = "origin/main";
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public GitResult? FailStatus { get; set; }
    public GitResult? FailNext { get; set; }
    public GitResult? FailPush { get; set; }
    public bool IsGitAvailable => true;
    public GitRepoInfo? FindRepo(string folder) => null;

    private GitResult Next(string call)
    {
        Calls.Add(call);
        var f = FailNext;
        FailNext = null;
        return f ?? GitResult.Ok();
    }

    public Task<GitResult<RepoStatus>> GetStatusAsync(string repo, bool includeIgnored = false, CancellationToken ct = default)
    {
        if (FailStatus is { } f) return Task.FromResult(new GitResult<RepoStatus>(f, null));
        return Task.FromResult(new GitResult<RepoStatus>(GitResult.Ok(),
            new RepoStatus(Branch, "abcdef0123", false, Upstream, Ahead, Behind, [.. Files])));
    }

    public async IAsyncEnumerable<CommitNode> GetLogAsync(string repo, LogFilter filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Calls.Add($"log:{filter.Skip}+{filter.Count}");
        await Task.Yield();
        foreach (var c in Log.Skip(filter.Skip).Take(filter.Count)) yield return c;
    }

    public Task<GitResult<IReadOnlyList<GitBranch>>> GetBranchesAsync(string repo, CancellationToken ct = default) =>
        Task.FromResult(new GitResult<IReadOnlyList<GitBranch>>(GitResult.Ok(),
            [new GitBranch("main", true, false, "origin/main", 0, 0, "a"), new GitBranch("dev", false, false, null, 0, 0, "b"),
             new GitBranch("origin/main", false, true, null, 0, 0, "a")]));

    public Task<GitResult<IReadOnlyList<GitStash>>> GetStashesAsync(string repo, CancellationToken ct = default) =>
        Task.FromResult(new GitResult<IReadOnlyList<GitStash>>(GitResult.Ok(), [new GitStash("stash@{0}", "h", DateTimeOffset.UnixEpoch, "wip")]));

    public Task<GitResult> StageAsync(string repo, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var r = Next("stage:" + string.Join(",", paths));
        if (!r.Success) return Task.FromResult(r);
        // 가짜 인덱스 반영: Unstaged 변경을 Staged 로 옮긴다
        Files = [.. Files.Select(f => paths.Contains(f.Path)
            ? f with { Staged = f.Unstaged == GitChange.Untracked ? GitChange.Added : f.Unstaged, Unstaged = GitChange.None }
            : f)];
        return Task.FromResult(r);
    }

    public Task<GitResult> UnstageAsync(string repo, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var r = Next("unstage:" + string.Join(",", paths));
        if (!r.Success) return Task.FromResult(r);
        Files = [.. Files.Select(f => paths.Contains(f.Path) ? f with { Unstaged = f.Staged, Staged = GitChange.None } : f)];
        return Task.FromResult(r);
    }

    public Task<GitResult> CommitAsync(string repo, string message, bool amend = false, CancellationToken ct = default)
    {
        var r = Next($"commit:{message}:{amend}");
        if (r.Success) Files = [.. Files.Where(f => f.Staged == GitChange.None)];
        return Task.FromResult(r);
    }

    public Task<GitResult> FetchAsync(string repo, IProgress<GitProgress>? progress = null, bool interactive = false, CancellationToken ct = default)
    {
        progress?.Report(new GitProgress("Receiving objects", 42, "x"));
        return Task.FromResult(Next("fetch"));
    }

    public Task<GitResult> PullAsync(string repo, PullMode mode, IProgress<GitProgress>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(Next("pull:" + mode));

    public Task<GitResult> PushAsync(string repo, bool setUpstream, bool forceWithLease, IProgress<GitProgress>? progress = null, CancellationToken ct = default)
    {
        var r = Next($"push:{setUpstream}:{forceWithLease}");
        return Task.FromResult(r.Success && FailPush is { } f ? f : r);
    }

    public Task<GitResult> StashPushAsync(string repo, string? message = null, bool includeUntracked = true, CancellationToken ct = default) =>
        Task.FromResult(Next("stash"));

    public Task<GitResult> StashPopAsync(string repo, string? stashName = null, CancellationToken ct = default) =>
        Task.FromResult(Next("pop:" + stashName));

    public Task<GitResult> CreateBranchAsync(string repo, string name, string? startPoint = null, bool checkout = true, CancellationToken ct = default) =>
        Task.FromResult(Next("create:" + name));

    public Task<GitResult> CheckoutAsync(string repo, string branch, CancellationToken ct = default) =>
        Task.FromResult(Next("checkout:" + branch));

    public Dictionary<string, FileDiff> Diffs { get; } = [];
    public List<BlameLine> Blame { get; set; } = [];
    public GitResult? FailDiff { get; set; }

    public Task<GitResult<FileDiff>> GetDiffAsync(string repo, GitDiffTarget target, int maxLines = GitDiffParser.DefaultMaxLines, CancellationToken ct = default)
    {
        Calls.Add($"diff:{target.Kind}:{target.Path}:{maxLines}");
        if (FailDiff is { } f) return Task.FromResult(new GitResult<FileDiff>(f, null));
        return Task.FromResult(new GitResult<FileDiff>(GitResult.Ok(), Diffs.TryGetValue(target.Path, out var d) ? d : FileDiff.Empty));
    }

    public Task<GitResult<IReadOnlyList<BlameLine>>> GetBlameAsync(string repo, string path, string? revision = null, CancellationToken ct = default)
    {
        Calls.Add($"blame:{path}:{revision}");
        return Task.FromResult(new GitResult<IReadOnlyList<BlameLine>>(GitResult.Ok(), Blame));
    }

    public Task<GitResult> InitAsync(string folder, CancellationToken ct = default) => Task.FromResult(Next("init"));

    public Task<GitResult> AddSafeDirectoryAsync(string path, CancellationToken ct = default)
    {
        var r = Next("safe:" + path);
        if (r.Success) FailStatus = null;
        return Task.FromResult(r);
    }
}

internal sealed class FakeCommitFiles : ICommitFileSource
{
    public Task<GitResult<IReadOnlyList<GitCommitFile>>> GetChangedFilesAsync(string repo, string hash, CancellationToken ct = default) =>
        Task.FromResult(new GitResult<IReadOnlyList<GitCommitFile>>(GitResult.Ok(),
            [new GitCommitFile("M", "src/a.cs"), new GitCommitFile("A", "docs/" + hash + ".md")]));
}

public class GitTabViewModelTests
{
    private static string H(int i) => "h" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);

    private static CommitNode C(int i, params int[] parents) => new(
        H(i), [.. parents.Select(H)],
        i % 2 == 0 ? "Kim" : "Lee", "x@y", DateTimeOffset.UnixEpoch.AddMinutes(i), "subject " + i, []);

    // 선형 이력: 커밋 n-1 이 가장 최신 (토폴로지 순서: 자식이 먼저)
    private static List<CommitNode> Linear(int n) =>
        [.. Enumerable.Range(0, n).Select(k => { var i = n - 1 - k; return i == 0 ? C(i) : C(i, i - 1); })];

    private static GitFileEntry Mod(string p) => new(p, null, GitChange.None, GitChange.Modified);

    private static (GitTabViewModel vm, FakeGit git) Make(Action<FakeGit>? setup = null)
    {
        var git = new FakeGit();
        setup?.Invoke(git);
        return (new GitTabViewModel(git, @"C:\repos\Foldy", new FakeCommitFiles()), git);
    }

    [Fact]
    public void Tab_identity()
    {
        var (vm, _) = Make();
        vm.Kind.Should().Be(TabKind.Git);
        vm.Title.Should().Be("Git: Foldy");
        vm.Location.Should().Be(@"C:\repos\Foldy");
    }

    [Fact]
    public async Task Refresh_splits_staged_and_unstaged_and_reads_branch_info()
    {
        var (vm, _) = Make(g =>
        {
            g.Ahead = 2;
            g.Behind = 1;
            g.Files =
            [
                Mod("a.txt"),
                new GitFileEntry("b.txt", null, GitChange.Added, GitChange.Modified),   // 양쪽에 나타난다
                new GitFileEntry("new.txt", null, GitChange.None, GitChange.Untracked),
                new GitFileEntry("c.txt", null, GitChange.Modified, GitChange.None),
            ];
        });
        await vm.RefreshAsync();

        vm.BranchName.Should().Be("main");
        vm.AheadBehindText.Should().Be("↑2 ↓1");
        vm.Unstaged.Select(i => i.Path).Should().Equal("a.txt", "b.txt", "new.txt");
        vm.Staged.Select(i => i.Path).Should().Equal("b.txt", "c.txt");
        vm.ChangeCount.Should().Be(4);
        vm.LocalBranches.Select(b => b.Name).Should().Equal("main", "dev");
        vm.RemoteBranches.Should().ContainSingle();
        vm.Stashes.Should().ContainSingle();
    }

    [Fact]
    public async Task Stage_selected_then_commit_clears_message_and_lists()
    {
        var (vm, git) = Make(g => g.Files = [Mod("a.txt"), Mod("b.txt")]);
        await vm.RefreshAsync();

        vm.SetSelectedUnstaged([vm.Unstaged[0]]);
        await vm.StageSelectedAsync();
        git.Calls.Should().Contain("stage:a.txt");
        vm.Staged.Select(i => i.Path).Should().Equal("a.txt");
        vm.Unstaged.Select(i => i.Path).Should().Equal("b.txt");

        vm.CanCommit.Should().BeFalse("메시지가 없다");
        vm.CommitMessage = "첫 커밋";
        vm.CanCommit.Should().BeTrue();
        await vm.CommitAsync();

        git.Calls.Should().Contain("commit:첫 커밋:False");
        vm.CommitMessage.Should().BeEmpty();
        vm.Staged.Should().BeEmpty();
        vm.ErrorText.Should().BeEmpty();
    }

    [Fact]
    public async Task Stage_without_selection_stages_all_and_unstage_all_reverses()
    {
        var (vm, git) = Make(g => g.Files = [Mod("a.txt"), Mod("b.txt")]);
        await vm.RefreshAsync();

        await vm.StageSelectedAsync();
        git.Calls.Should().Contain("stage:a.txt,b.txt");
        vm.Staged.Should().HaveCount(2);

        await vm.UnstageAllAsync();
        vm.Staged.Should().BeEmpty();
        vm.Unstaged.Should().HaveCount(2);
    }

    [Fact]
    public async Task Unstage_of_rename_includes_original_path()
    {
        var (vm, git) = Make(g => g.Files = [new GitFileEntry("new.txt", "old.txt", GitChange.Renamed, GitChange.None)]);
        await vm.RefreshAsync();
        await vm.UnstageAllAsync();
        git.Calls.Should().Contain("unstage:new.txt,old.txt");
    }

    [Fact]
    public async Task Commit_without_staged_files_or_message_does_nothing()
    {
        var (vm, git) = Make(g => g.Files = [Mod("a.txt")]);
        await vm.RefreshAsync();
        vm.CommitMessage = "msg";
        await vm.CommitAsync();
        git.Calls.Should().NotContain(c => c.StartsWith("commit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Amend_allows_empty_message_and_resets_flag()
    {
        var (vm, git) = Make();
        await vm.RefreshAsync();
        vm.Amend = true;
        vm.CanCommit.Should().BeTrue();
        await vm.CommitAsync();
        git.Calls.Should().Contain("commit::True");
        vm.Amend.Should().BeFalse();
    }

    [Fact]
    public async Task Commit_failure_keeps_message_and_shows_text()
    {
        var (vm, git) = Make(g => g.Files = [new GitFileEntry("a.txt", null, GitChange.Modified, GitChange.None)]);
        await vm.RefreshAsync();
        vm.CommitMessage = "msg";
        git.FailNext = GitResult.Fail(GitFailure.NothingToCommit, "nothing to commit");
        await vm.CommitAsync();
        vm.CommitMessage.Should().Be("msg");
        vm.ErrorText.Should().Contain("커밋할 변경이 없습니다");
    }

    [Fact]
    public async Task Commit_and_push_sets_upstream_when_missing()
    {
        var (vm, git) = Make(g =>
        {
            g.Upstream = null;
            g.Files = [new GitFileEntry("a.txt", null, GitChange.Modified, GitChange.None)];
        });
        await vm.RefreshAsync();
        vm.CommitMessage = "msg";
        await vm.CommitAndPushAsync();
        git.Calls.Should().ContainInOrder("commit:msg:False", "push:True:False");
        vm.CommitMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task Commit_and_push_stops_when_commit_fails()
    {
        var (vm, git) = Make(g => g.Files = [new GitFileEntry("a.txt", null, GitChange.Modified, GitChange.None)]);
        await vm.RefreshAsync();
        vm.CommitMessage = "msg";
        git.FailNext = GitResult.Fail(GitFailure.Other, "hook failed");
        await vm.CommitAndPushAsync();
        git.Calls.Should().NotContain(c => c.StartsWith("push", StringComparison.Ordinal));
        vm.ErrorText.Should().Contain("hook failed");
        vm.CommitMessage.Should().Be("msg");
    }

    [Fact]
    public async Task Force_push_requires_confirmation()
    {
        var (vm, git) = Make();
        await vm.RefreshAsync();

        await vm.PushForceWithLeaseAsync();                 // 콜백 없음 → 실행 안 함
        git.Calls.Should().NotContain(c => c.StartsWith("push", StringComparison.Ordinal));

        var asked = 0;
        vm.Confirm = _ => { asked++; return Task.FromResult(false); };
        await vm.PushForceWithLeaseAsync();
        asked.Should().Be(1);
        git.Calls.Should().NotContain(c => c.StartsWith("push", StringComparison.Ordinal));

        vm.Confirm = _ => Task.FromResult(true);
        await vm.PushForceWithLeaseAsync();
        git.Calls.Should().Contain("push:False:True");
    }

    [Fact]
    public async Task Pull_modes_and_stash_and_branch_commands_call_service()
    {
        var (vm, git) = Make();
        await vm.RefreshAsync();

        await vm.PullMergeAsync();
        await vm.PullRebaseAsync();
        await vm.StashPushAsync();
        vm.SelectedStash = vm.Stashes[0];
        await vm.StashPopAsync();
        await vm.CheckoutAsync(vm.LocalBranches[0]);        // 현재 브랜치는 무시
        await vm.CheckoutAsync(vm.LocalBranches[1]);
        vm.NewBranchName = "feature/x";
        await vm.CreateBranchAsync();

        git.Calls.Should().ContainInOrder("pull:Merge", "pull:Rebase", "stash", "pop:stash@{0}", "checkout:dev", "create:feature/x");
        git.Calls.Should().NotContain("checkout:main");
        vm.NewBranchName.Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_branch_name_is_rejected_without_calling_git()
    {
        var (vm, git) = Make();
        vm.NewBranchName = "has space";
        await vm.CreateBranchAsync();
        vm.ErrorText.Should().NotBeEmpty();
        git.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_reports_progress_text()
    {
        var (vm, _) = Make();
        await vm.FetchAsync();
        // Progress<T> 알림은 스레드풀로 돌아올 수 있어 정확한 문장 대신 접두사만 본다
        vm.ProgressText.Should().StartWith("가져오기");
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_write_is_ignored_while_busy()
    {
        var (vm, git) = Make();
        await vm.RefreshAsync();
        var a = vm.FetchAsync();
        var b = vm.FetchAsync();
        await Task.WhenAll(a, b);
        git.Calls.Count(c => c == "fetch").Should().BeLessThanOrEqualTo(2).And.BeGreaterThan(0);
    }

    // ───────── History ─────────

    [Fact]
    public async Task History_loads_first_page_with_lane_data()
    {
        var (vm, git) = Make(g => g.Log = Linear(2500));
        await vm.RefreshAsync();

        vm.Commits.Should().HaveCount(GitTabViewModel.PageSize);
        vm.HasMoreHistory.Should().BeTrue();
        vm.Commits.Should().OnlyContain(r => r.Graph.Column == 0);
        vm.MaxLaneCount.Should().Be(1);
        git.Calls.Should().Contain("log:0+1000");
    }

    [Fact]
    public async Task History_paging_continues_lanes_and_stops_on_short_page()
    {
        var (vm, git) = Make(g => g.Log = Linear(2500));
        await vm.RefreshAsync();
        await vm.LoadMoreHistoryAsync();
        await vm.LoadMoreHistoryAsync();

        vm.Commits.Should().HaveCount(2500);
        vm.HasMoreHistory.Should().BeFalse();
        git.Calls.Should().Contain(["log:1000+1000", "log:2000+1000"]);

        // 페이지 경계의 행도 이전 행에서 이어지는 선을 가진다 (레인 계산기 상태 유지)
        var boundary = vm.Commits[1000].Graph;
        boundary.IncomingColumns.Should().Equal(0);
        vm.Commits[^1].Graph.ParentColumns.Should().BeEmpty("루트 커밋");

        await vm.LoadMoreHistoryAsync();   // 더 없으면 호출하지 않는다
        git.Calls.Count(c => c.StartsWith("log:", StringComparison.Ordinal)).Should().Be(3);
    }

    [Fact]
    public async Task History_merge_creates_second_lane()
    {
        // m(부모 a,b) → a → b → root: 병합이 열 2개를 만든다
        var log = new List<CommitNode>
        {
            new("m", ["a", "b"], "K", "k@x", DateTimeOffset.UnixEpoch, "merge", []),
            new("a", ["r"], "K", "k@x", DateTimeOffset.UnixEpoch, "a", []),
            new("b", ["r"], "K", "k@x", DateTimeOffset.UnixEpoch, "b", []),
            new("r", [], "K", "k@x", DateTimeOffset.UnixEpoch, "root", []),
        };
        var (vm, _) = Make(g => g.Log = log);
        await vm.RefreshAsync();
        vm.MaxLaneCount.Should().BeGreaterThan(1);
        vm.Commits[0].Graph.ParentColumns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Search_filters_by_message_author_and_hash_and_hides_graph()
    {
        var (vm, _) = Make(g => g.Log = Linear(20));
        await vm.RefreshAsync();

        vm.SearchText = "subject 7";
        vm.Commits.Should().ContainSingle().Which.Node.Subject.Should().Be("subject 7");
        vm.ShowGraph.Should().BeFalse();

        vm.SearchText = "lee";   // 홀수 번째 커밋 (작성자 Lee), 대소문자 무시
        vm.Commits.Should().HaveCount(10);

        vm.SearchText = "h00003";
        vm.Commits.Should().ContainSingle();

        vm.SearchText = "";
        vm.Commits.Should().HaveCount(20);
        vm.ShowGraph.Should().BeTrue();
    }

    [Fact]
    public async Task Selecting_commit_loads_changed_files_and_switches_pane()
    {
        var (vm, _) = Make(g => g.Log = Linear(3));
        await vm.RefreshAsync();
        vm.ShowLocalChanges.Should().BeTrue();

        vm.SelectedCommit = vm.Commits[0];
        await vm.CommitFilesLoading;
        vm.ShowLocalChanges.Should().BeFalse();
        vm.CommitFiles.Select(f => f.Path).Should().Equal("src/a.cs", "docs/h00002.md");

        vm.SelectLocalChanges();
        vm.ShowLocalChanges.Should().BeTrue();
        vm.SelectedCommit.Should().BeNull();
        vm.CommitFiles.Should().BeEmpty();
    }

    // ───────── 오류 매핑 ─────────

    [Fact]
    public async Task Dubious_ownership_offers_trust_and_retries()
    {
        var (vm, git) = Make(g => g.FailStatus = new GitResult(false, GitFailure.DubiousOwnership, 128, "", "fatal: detected dubious ownership")
        {
            SafeDirectoryCandidate = "C:/repos/Foldy",
        });
        await vm.RefreshAsync();
        vm.CanTrustRepo.Should().BeTrue();
        vm.HasError.Should().BeTrue();
        vm.ErrorText.Should().Contain("신뢰");

        await vm.TrustRepositoryAsync();
        git.Calls.Should().Contain("safe:C:/repos/Foldy");
        vm.CanTrustRepo.Should().BeFalse();
        vm.HasError.Should().BeFalse();
        vm.BranchName.Should().Be("main");
    }

    [Theory]
    [InlineData(GitFailure.GitNotFound, "Git for Windows")]
    [InlineData(GitFailure.AuthenticationFailed, "인증")]
    [InlineData(GitFailure.MergeConflict, "충돌")]
    [InlineData(GitFailure.NotARepository, "저장소가 아닙니다")]
    [InlineData(GitFailure.Other, "boom")]
    public void Failure_maps_to_text(GitFailure f, string expected) =>
        GitErrorText.Describe(GitResult.Fail(f, "boom", 1)).Should().Contain(expected);

    [Fact]
    public void Cancelled_and_success_have_no_error_text()
    {
        GitErrorText.Describe(GitResult.Ok()).Should().BeEmpty();
        GitErrorText.Describe(GitResult.Fail(GitFailure.Cancelled)).Should().BeEmpty();
    }

    [Fact]
    public async Task Pull_conflict_shows_error_and_still_refreshes()
    {
        var (vm, git) = Make(g => g.Files = [new GitFileEntry("a.txt", null, GitChange.Conflicted, GitChange.Conflicted)]);
        git.FailNext = GitResult.Fail(GitFailure.MergeConflict, "CONFLICT (content)", 1);
        await vm.PullMergeAsync();
        vm.ErrorText.Should().Contain("병합 충돌");
        vm.Unstaged.Should().ContainSingle().Which.Change.Should().Be(GitChange.Conflicted);
    }

    // ───────── 변경 파일 파서 ─────────

    [Fact]
    public void NameStatus_parser_handles_rename_and_plain_entries()
    {
        var files = GitCliCommitFiles.ParseNameStatus("M\0src/a.cs\0R100\0old name.txt\0new name.txt\0A\0b.md\0D\0c.md\0");
        files.Should().Equal(
            new GitCommitFile("M", "src/a.cs"),
            new GitCommitFile("R", "new name.txt", "old name.txt"),
            new GitCommitFile("A", "b.md"),
            new GitCommitFile("D", "c.md"));
    }
}
