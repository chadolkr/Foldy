using Foldy.Core.FileSystem;

namespace Foldy.Git.Tests;

/// <summary>실제 git.exe 로 임시 저장소를 만들어 검증한다. git 이 없으면 건너뛴다.</summary>
public sealed class GitIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-git-it-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _svc = new();

    public GitIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal); // .git 의 읽기 전용 객체 파일
            Directory.Delete(_dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<GitResult> RunGit(params string[] args)
        => await _svc.Cli.RunAsync(_dir, args, new GitRunOptions { Write = true });

    private async Task InitAsync()
    {
        Assert.SkipUnless(_svc.IsGitAvailable, "git.exe 가 없어 건너뜀");
        (await RunGit("init", "-b", "main")).Success.Should().BeTrue();
        await RunGit("config", "user.name", "Tester");
        await RunGit("config", "user.email", "t@example.com");
        await RunGit("config", "commit.gpgsign", "false");
    }

    private async Task CommitFileAsync(string name, string content, string msg)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, name), content);
        (await _svc.StageAsync(_dir, [name])).Success.Should().BeTrue();
        (await _svc.CommitAsync(_dir, msg)).Success.Should().BeTrue();
    }

    [Fact]
    public async Task Status_reflects_untracked_modified_and_staged()
    {
        await InitAsync();
        await CommitFileAsync("a.txt", "1", "first");

        await File.WriteAllTextAsync(Path.Combine(_dir, "a.txt"), "2");
        await File.WriteAllTextAsync(Path.Combine(_dir, "한글.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(_dir, "b.txt"), "y");
        await _svc.StageAsync(_dir, ["b.txt"]);

        var st = await _svc.GetStatusAsync(_dir);
        st.Success.Should().BeTrue();
        var s = st.Value!;
        s.BranchName.Should().Be("main");
        s.HasUpstream.Should().BeFalse();
        s.Files.Single(f => f.Path == "a.txt").ToBadge().Should().Be(GitFileStatus.Modified);
        s.Files.Single(f => f.Path == "b.txt").ToBadge().Should().Be(GitFileStatus.Added);
        s.Files.Single(f => f.Path == "한글.txt").ToBadge().Should().Be(GitFileStatus.Untracked);

        (await _svc.UnstageAsync(_dir, ["b.txt"])).Success.Should().BeTrue();
        (await _svc.GetStatusAsync(_dir)).Value!.Files.Single(f => f.Path == "b.txt").ToBadge()
            .Should().Be(GitFileStatus.Untracked);
    }

    [Fact]
    public async Task Log_pages_and_amend_works()
    {
        await InitAsync();
        for (var i = 1; i <= 4; i++) await CommitFileAsync($"f{i}.txt", $"{i}",$"커밋 {i}");

        var page1 = await Collect(_svc.GetLogAsync(_dir, new LogFilter { Count = 3 }));
        var page2 = await Collect(_svc.GetLogAsync(_dir, new LogFilter { Skip = 3, Count = 3 }));
        page1.Should().HaveCount(3);
        page2.Should().HaveCount(1);
        page1[0].Subject.Should().Be("커밋 4");
        page1[0].Refs.Should().Contain(r => r.Name == "main" && r.IsCurrentHead);
        page2[^1].Parents.Should().BeEmpty();

        (await _svc.CommitAsync(_dir, "수정된 메시지", amend: true)).Success.Should().BeTrue();
        (await Collect(_svc.GetLogAsync(_dir, new LogFilter { Count = 1 })))[0].Subject.Should().Be("수정된 메시지");

        var filtered = await Collect(_svc.GetLogAsync(_dir, new LogFilter { MessageContains = "커밋 2" }));
        filtered.Should().ContainSingle();

        var rows = GraphLaneCalculator.Compute([.. page1, .. page2]);
        rows.Should().HaveCount(4).And.OnlyContain(r => r.Column == 0);
    }

    [Fact]
    public async Task Empty_commit_message_and_nothing_to_commit_fail_typed()
    {
        await InitAsync();
        (await _svc.CommitAsync(_dir, "")).Failure.Should().Be(GitFailure.Other);
        await CommitFileAsync("a.txt", "1", "first");
        (await _svc.CommitAsync(_dir, "again")).Failure.Should().Be(GitFailure.NothingToCommit);
    }

    [Fact]
    public async Task Branch_create_checkout_list()
    {
        await InitAsync();
        await CommitFileAsync("a.txt", "1", "first");

        (await _svc.CreateBranchAsync(_dir, "feature/x")).Success.Should().BeTrue();
        (await _svc.CreateBranchAsync(_dir, "other", checkout: false)).Success.Should().BeTrue();
        var list = (await _svc.GetBranchesAsync(_dir)).Value!;
        list.Select(b => b.Name).Should().BeEquivalentTo("main", "feature/x", "other");
        list.Single(b => b.IsCurrent).Name.Should().Be("feature/x");

        (await _svc.CheckoutAsync(_dir, "main")).Success.Should().BeTrue();
        (await _svc.GetStatusAsync(_dir)).Value!.BranchName.Should().Be("main");
        (await _svc.CheckoutAsync(_dir, "nope")).Success.Should().BeFalse();
    }

    [Fact]
    public async Task Stash_push_list_pop()
    {
        await InitAsync();
        await CommitFileAsync("a.txt", "1", "first");
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.txt"), "changed");

        (await _svc.StashPushAsync(_dir, "임시 저장")).Success.Should().BeTrue();
        (await _svc.GetStatusAsync(_dir)).Value!.ChangeCount.Should().Be(0);
        var stashes = (await _svc.GetStashesAsync(_dir)).Value!;
        stashes.Should().ContainSingle().Which.Message.Should().Contain("임시 저장");

        (await _svc.StashPopAsync(_dir)).Success.Should().BeTrue();
        (await _svc.GetStatusAsync(_dir)).Value!.ChangeCount.Should().Be(1);
        (await _svc.GetStashesAsync(_dir)).Value!.Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_push_pull_against_local_bare_remote_with_progress()
    {
        await InitAsync();
        await CommitFileAsync("a.txt", "1", "first");
        var bare = _dir + "-remote.git";
        try
        {
            var init = await _svc.Cli.RunAsync(Path.GetTempPath(), ["init", "--bare", "-b", "main", bare]);
            init.Success.Should().BeTrue();
            (await RunGit("remote", "add", "origin", bare)).Success.Should().BeTrue();

            var seen = new List<GitProgress>();
            (await _svc.PushAsync(_dir, setUpstream: true, forceWithLease: false, new Progress<GitProgress>(seen.Add))).Success.Should().BeTrue();
            (await _svc.FetchAsync(_dir)).Success.Should().BeTrue();
            (await _svc.PullAsync(_dir, PullMode.Merge)).Success.Should().BeTrue();
            (await _svc.PullAsync(_dir, PullMode.Rebase)).Success.Should().BeTrue();

            var s = (await _svc.GetStatusAsync(_dir)).Value!;
            s.Upstream.Should().Be("origin/main");
            (s.Ahead, s.Behind).Should().Be((0, 0));

            await CommitFileAsync("b.txt", "2", "second");
            (await _svc.GetStatusAsync(_dir)).Value!.Ahead.Should().Be(1);
            (await _svc.PushAsync(_dir, setUpstream: false, forceWithLease: true)).Success.Should().BeTrue();
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(bare, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(bare, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public async Task Not_a_repository_is_typed_failure()
    {
        Assert.SkipUnless(_svc.IsGitAvailable, "git.exe 가 없어 건너뜀");
        var r = await _svc.GetStatusAsync(_dir);
        r.Success.Should().BeFalse();
        r.Result.Failure.Should().Be(GitFailure.NotARepository);
    }

    [Fact]
    public async Task Cancellation_returns_cancelled()
    {
        await InitAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var r = await _svc.Cli.RunAsync(_dir, ["status"], null, null, cts.Token);
        r.Failure.Should().Be(GitFailure.Cancelled);
    }

    [Fact]
    public async Task Missing_git_returns_typed_result()
    {
        var svc = new GitService(new GitCli(Path.Combine(_dir, "no-such-git.exe")));
        var r = await svc.GetStatusAsync(_dir);
        r.Result.Failure.Should().Be(GitFailure.GitNotFound);
    }

    private static async Task<List<CommitNode>> Collect(IAsyncEnumerable<CommitNode> src)
    {
        var l = new List<CommitNode>();
        await foreach (var c in src) l.Add(c);
        return l;
    }
}
