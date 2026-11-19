using Foldy.Core.Threading;
using Foldy.Presentation.Services;
using Foldy.Presentation.Tabs;
using Foldy.Providers.Local;

namespace Foldy.Presentation.Tests;

public sealed class DriveSpeedTrackerTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);

    [Fact]
    public void Ema_over_threshold_needs_two_samples()
    {
        var t = new DriveSpeedTracker();
        t.Record(@"K:\a", TimeSpan.FromMilliseconds(600));
        t.IsSlow(@"K:\a").Should().BeFalse("표본 1개, 2초 미만");
        t.Record(@"K:\b", TimeSpan.FromMilliseconds(600)).Should().BeTrue();
        t.IsSlow(@"k:\anything").Should().BeTrue();
    }

    [Fact]
    public void Single_open_over_two_seconds_is_slow()
    {
        var t = new DriveSpeedTracker();
        string? raised = null;
        t.BecameSlow += r => raised = r;
        t.Record(@"K:\", TimeSpan.FromMilliseconds(2500)).Should().BeTrue();
        raised.Should().Be("K:");
        t.Record(@"K:\", TimeSpan.FromMilliseconds(2500)).Should().BeFalse("이미 느림: 이벤트는 한 번");
    }

    [Fact]
    public void Fast_drive_stays_fast_and_drives_are_isolated()
    {
        var t = new DriveSpeedTracker();
        for (var i = 0; i < 5; i++)
        {
            t.Record(@"C:\x", Fast);
        }

        t.Record(@"K:\x", TimeSpan.FromSeconds(3));
        t.IsSlow(@"C:\y").Should().BeFalse();
        t.IsSlow(@"K:\y").Should().BeTrue();
        t.Classify(@"K:\y").Should().Be(Foldy.Core.FileSystem.DriveSpeedClass.Slow);
    }

    [Fact]
    public void Slow_drive_recovers_when_average_drops()
    {
        var t = new DriveSpeedTracker();
        t.Record(@"K:\", TimeSpan.FromSeconds(3));
        for (var i = 0; i < 6; i++)
        {
            t.Record(@"K:\", Fast);
        }

        t.IsSlow(@"K:\").Should().BeFalse();
    }

    [Fact]
    public void Unc_roots_are_per_share()
    {
        var t = new DriveSpeedTracker();
        DriveSpeedTracker.RootOf(@"\\localhost\Redirector\dir\x").Should().Be(@"\\localhost\Redirector");
        DriveSpeedTracker.RootOf(@"\\?\UNC\srv\share\a").Should().Be(@"\\srv\share");
        t.Record(@"\\srv\share\a", TimeSpan.FromSeconds(3));
        t.IsSlow(@"\\SRV\Share\b\c").Should().BeTrue();
        t.IsSlow(@"\\srv\other\b").Should().BeFalse();
        t.IsSlow("sftp://host/x").Should().BeFalse();
        DriveSpeedTracker.RootOf("").Should().BeNull();
    }
}

public sealed class SlowDriveFolderTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "foldy-slow-" + Guid.NewGuid().ToString("N"));
    private readonly DriveQueueRegistry _queues = new();

    public SlowDriveFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
    }

    public async ValueTask DisposeAsync()
    {
        await _queues.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private sealed class SpyGit : IGitStatusProvider
    {
        public int Calls;

        public Task<GitFolderInfo?> GetAsync(string folder, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<GitFolderInfo?>(new GitFolderInfo(folder, "main", _ => Foldy.Core.FileSystem.GitFileStatus.None, 0));
        }
    }

    private sealed class SpySizes(Func<int> itemCount) : IFolderSizeProvider
    {
        public int ItemsWhenStarted = -1;

        public Task<IReadOnlyDictionary<string, long>?> GetAsync(string folder, CancellationToken ct)
        {
            ItemsWhenStarted = itemCount();
            return Task.FromResult<IReadOnlyDictionary<string, long>?>(null);
        }
    }

    [Fact]
    public async Task Slow_drive_skips_git_and_defers_sizes_until_list_is_committed()
    {
        var speed = new DriveSpeedTracker();
        speed.Record(_root, TimeSpan.FromSeconds(3)); // 이 드라이브는 이미 느리다고 학습됨
        var git = new SpyGit();
        FolderTabViewModel? tabRef = null;
        var sizes = new SpySizes(() => tabRef!.Items.Count);
        var env = new TabEnvironment(new LocalProvider(_queues), new InlineDispatcher()) { Git = git, FolderSizes = sizes, Speed = speed };
        var tab = tabRef = new FolderTabViewModel(env);

        await tab.NavigateAsync(_root);
        await Task.Delay(200);

        tab.IsSlowDrive.Should().BeTrue();
        tab.Git.Should().BeNull("Git 바는 안 보인다");
        git.Calls.Should().Be(0);
        sizes.ItemsWhenStarted.Should().Be(2, "폴더 크기 작업은 목록이 커밋된 뒤에 시작");
    }

    [Fact]
    public async Task Fast_drive_still_detects_git()
    {
        var git = new SpyGit();
        var env = new TabEnvironment(new LocalProvider(_queues), new InlineDispatcher()) { Git = git };
        var tab = new FolderTabViewModel(env);
        await tab.NavigateAsync(_root);
        await Task.Delay(200);
        git.Calls.Should().Be(1);
        tab.IsSlowDrive.Should().BeFalse();
    }
}
