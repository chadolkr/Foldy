namespace Foldy.Git.Tests;

public sealed class RepoLocatorTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "foldy-git-loc-" + Guid.NewGuid().ToString("N"));

    public RepoLocatorTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    [Fact]
    public void Finds_repo_walking_up_and_caches()
    {
        var repo = Path.Combine(_tmp, "r");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var deep = Path.Combine(repo, "a", "b");
        Directory.CreateDirectory(deep);

        var loc = new GitRepoLocator();
        var info = loc.FindRepo(deep);
        info.Should().NotBeNull();
        info!.Root.Should().Be(repo);
        info.IsLinkedGitDir.Should().BeFalse();

        // 캐시: .git 이 사라져도 무효화 전까지 같은 결과
        Directory.Delete(Path.Combine(repo, ".git"));
        loc.FindRepo(deep)!.Root.Should().Be(repo);
        loc.Invalidate();
        loc.FindRepo(deep).Should().BeNull();
    }

    [Fact]
    public void Follows_gitdir_file_for_worktrees()
    {
        var wt = Path.Combine(_tmp, "wt");
        var real = Path.Combine(_tmp, "main", ".git", "worktrees", "wt");
        Directory.CreateDirectory(wt);
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + real.Replace('\\', '/') + "\n");

        var info = new GitRepoLocator().FindRepo(wt);
        info.Should().NotBeNull();
        info!.IsLinkedGitDir.Should().BeTrue();
        info.GitDir.Should().Be(real);
    }

    [Fact]
    public void Relative_gitdir_is_resolved_against_folder()
    {
        GitRepoLocator.ParseGitDirFile("gitdir: ../.git/modules/x\n", Path.Combine(_tmp, "sub"))
            .Should().Be(Path.GetFullPath(Path.Combine(_tmp, ".git", "modules", "x")));
        GitRepoLocator.ParseGitDirFile("garbage", _tmp).Should().BeNull();
    }

    [Fact]
    public void Nearest_repo_wins_and_network_is_skipped_unless_allowed()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, ".git"));
        var inner = Path.Combine(_tmp, "inner");
        Directory.CreateDirectory(Path.Combine(inner, ".git"));
        new GitRepoLocator().FindRepo(inner)!.Root.Should().Be(inner);

        var net = new GitRepoLocator(allowNetwork: false, isNetworkPath: _ => true);
        net.FindRepo(inner).Should().BeNull();
        net.AllowNetwork = true;
        // 네트워크 판정 함수는 생성 시 고정이므로 허용 플래그만 켜면 통과한다
        net.FindRepo(inner)!.Root.Should().Be(inner);
    }
}
