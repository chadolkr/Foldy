namespace Foldy.Git.Tests;

/// <summary>실제 git.exe 로 diff / blame 을 확인한다. git 이 없으면 건너뛴다.</summary>
public sealed class DiffBlameIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-git-diff-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _svc = new();

    public DiffBlameIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private Task<GitResult> Git(params string[] args) => _svc.Cli.RunAsync(_dir, args, new GitRunOptions { Write = true });

    private async Task InitAsync()
    {
        Assert.SkipUnless(_svc.IsGitAvailable, "git.exe 가 없어 건너뜀");
        (await _svc.InitAsync(_dir)).Success.Should().BeTrue();
        await Git("config", "user.name", "Tester");
        await Git("config", "user.email", "t@example.com");
        await Git("config", "commit.gpgsign", "false");
    }

    private async Task CommitAsync(string name, string content, string msg)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, name), content);
        (await _svc.StageAsync(_dir, [name])).Success.Should().BeTrue();
        (await _svc.CommitAsync(_dir, msg)).Success.Should().BeTrue();
    }

    [Fact]
    public async Task Diff_for_unstaged_staged_untracked_and_commit()
    {
        await InitAsync();
        await CommitAsync("a.txt", "one\ntwo\nthree\n", "first");
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.txt"), "one\nTWO\nthree\n");

        var un = await _svc.GetDiffAsync(_dir, new GitDiffTarget(GitDiffKind.Unstaged, "a.txt"));
        un.Success.Should().BeTrue();
        un.Value!.Lines.Should().Contain(l => l.Kind == DiffLineKind.Removed && l.Text == "two");
        un.Value.Lines.Should().Contain(l => l.Kind == DiffLineKind.Added && l.Text == "TWO");

        await _svc.StageAsync(_dir, ["a.txt"]);
        (await _svc.GetDiffAsync(_dir, new GitDiffTarget(GitDiffKind.Unstaged, "a.txt"))).Value!.IsEmpty.Should().BeTrue();
        var st = await _svc.GetDiffAsync(_dir, new GitDiffTarget(GitDiffKind.Staged, "a.txt"));
        st.Value!.AddedCount.Should().Be(1);

        await File.WriteAllTextAsync(Path.Combine(_dir, "새 파일.txt"), "hello\nworld\n");
        var nw = await _svc.GetDiffAsync(_dir, new GitDiffTarget(GitDiffKind.Unstaged, "새 파일.txt", Untracked: true));
        nw.Success.Should().BeTrue(nw.Result.Error);
        nw.Value!.AddedCount.Should().Be(2);

        await _svc.CommitAsync(_dir, "second");
        var head = (await _svc.GetLogAsync(_dir, new LogFilter { Count = 1 }).ToListAsync())[0];
        var cm = await _svc.GetDiffAsync(_dir, new GitDiffTarget(GitDiffKind.Commit, "a.txt", Hash: head.Hash));
        cm.Value!.Lines.Should().Contain(l => l.Kind == DiffLineKind.Added && l.Text == "TWO");

        var first = (await _svc.GetLogAsync(_dir, new LogFilter { Count = 5 }).ToListAsync())[1];
        var root = await _svc.GetDiffAsync(_dir, new GitDiffTarget(GitDiffKind.Commit, "a.txt", Hash: first.Hash));
        root.Value!.AddedCount.Should().Be(3);
    }

    [Fact]
    public async Task Binary_file_is_flagged()
    {
        await InitAsync();
        await File.WriteAllBytesAsync(Path.Combine(_dir, "b.bin"), [0, 1, 2, 0, 255, 0, 3]);
        var d = await _svc.GetDiffAsync(_dir, new GitDiffTarget(GitDiffKind.Unstaged, "b.bin", Untracked: true));
        d.Success.Should().BeTrue();
        d.Value!.IsBinary.Should().BeTrue();
    }

    [Fact]
    public async Task Blame_assigns_lines_to_commits()
    {
        await InitAsync();
        await CommitAsync("a.txt", "one\ntwo\n", "first");
        await CommitAsync("a.txt", "one\ntwo\nthree\n", "add three");
        var r = await _svc.GetBlameAsync(_dir, "a.txt");
        r.Success.Should().BeTrue(r.Result.Error);
        var lines = r.Value!;
        lines.Select(l => l.Text).Should().Equal("one", "two", "three");
        lines[0].Summary.Should().Be("first");
        lines[2].Summary.Should().Be("add three");
        lines[0].Author.Should().Be("Tester");
        lines[0].BlockIndex.Should().Be(0);
        lines[2].BlockIndex.Should().Be(1);

        var bad = await _svc.GetBlameAsync(_dir, "missing.txt");
        bad.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Init_creates_repository()
    {
        Assert.SkipUnless(_svc.IsGitAvailable, "git.exe 가 없어 건너뜀");
        (await _svc.GetStatusAsync(_dir)).Result.Failure.Should().Be(GitFailure.NotARepository);
        (await _svc.InitAsync(_dir)).Success.Should().BeTrue();
        (await _svc.GetStatusAsync(_dir)).Success.Should().BeTrue();
    }
}
