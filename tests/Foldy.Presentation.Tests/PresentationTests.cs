using Foldy.Core.FileSystem;
using Foldy.Core.Threading;
using Foldy.Presentation.Files;
using Foldy.Presentation.Navigation;
using Foldy.Presentation.Panes;
using Foldy.Presentation.Persistence;
using Foldy.Presentation.Services;
using Foldy.Presentation.Shell;
using Foldy.Presentation.Tabs;
using Foldy.Providers.Local;

namespace Foldy.Presentation.Tests;

public class NaturalComparerTests
{
    [Fact]
    public void Numbers_compare_by_value_like_explorer()
    {
        string[] input = ["file10.txt", "file2.txt", "File1.txt", "file02.txt", "a", "file1b.txt", "file1a.txt"];
        var sorted = input.Order(NaturalComparer.Instance).ToArray();
        sorted.Should().Equal("a", "File1.txt", "file1a.txt", "file1b.txt", "file2.txt", "file02.txt", "file10.txt");
    }

    [Fact]
    public void Huge_numbers_do_not_overflow() =>
        NaturalComparer.Instance.Compare("x99999999999999999999999", "x100000000000000000000000").Should().BeNegative();

    [Fact]
    public void Korean_names_sort() =>
        new[] { "사진2", "문서", "사진10", "가방" }.Order(NaturalComparer.Instance).Should().Equal("가방", "문서", "사진2", "사진10");
}

public class SortingTests
{
    private static FileItem F(string name, long size = 0, bool folder = false, int day = 1) => new(new FsEntry(
        name, FsPath.Local(@"C:\t\" + name), folder ? FsKind.Folder : FsKind.File, size, null,
        new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc), default, folder ? FileAttributes.Directory : 0, null));

    [Fact]
    public void Folders_first_then_natural_name()
    {
        var r = FileSorter.Sort([F("b10"), F("z", folder: true), F("b2"), F("a", folder: true)], new SortSpec());
        r.Select(x => x.Name).Should().Equal("a", "z", "b2", "b10");
    }

    [Fact]
    public void Size_descending_keeps_folders_first()
    {
        var spec = new SortSpec().Toggle(SortKey.Size);
        spec.Descending.Should().BeTrue("크기·날짜는 처음 누르면 큰 것/최근 것부터");
        var r = FileSorter.Sort([F("s", 1), F("d", folder: true), F("l", 100)], spec);
        r.Select(x => x.Name).Should().Equal("d", "l", "s");
    }

    [Fact]
    public void Toggle_same_key_flips_direction() =>
        new SortSpec().Toggle(SortKey.Name).Should().Be(new SortSpec(SortKey.Name, true));
}

public class PathUtilTests
{
    [Theory]
    [InlineData(@"c:", @"C:\")]
    [InlineData(@"C:\Windows\", @"C:\Windows")]
    [InlineData(@"C:\a\..\b", @"C:\b")]
    [InlineData(@"""C:\Program Files""", @"C:\Program Files")]
    [InlineData(@"\\nas\share\dir\", @"\\nas\share\dir")]
    public void Normalize(string input, string expected) => PathUtil.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("relative\\x")]
    [InlineData("")]
    [InlineData("C:\\bad|name")]
    public void Normalize_rejects(string input) => PathUtil.Normalize(input).Should().BeNull();

    [Fact]
    public void Sftp_paths()
    {
        PathUtil.Normalize("sftp://me@host:2222//var//log/").Should().Be("sftp://me@host:2222/var/log");
        PathUtil.Normalize("sftp://me@host").Should().Be("sftp://me@host/");
        PathUtil.Parent("sftp://me@host/var/log").Should().Be("sftp://me@host/var");
        PathUtil.Parent("sftp://me@host/var").Should().Be("sftp://me@host/");
        PathUtil.Parent("sftp://me@host/").Should().BeNull();
        PathUtil.DisplayName("sftp://me@host/").Should().Be("me@host");
        PathUtil.Segments("sftp://me@host/var/log").Select(s => s.Name).Should().Equal("me@host", "var", "log");
    }

    [Fact]
    public void Parent_of_root_is_null()
    {
        PathUtil.Parent(@"C:\").Should().BeNull();
        PathUtil.Parent(@"C:\a\b").Should().Be(@"C:\a");
        PathUtil.Parent(@"C:\a").Should().Be(@"C:\");
    }

    [Fact]
    public void Breadcrumb_segments()
    {
        PathUtil.Segments(@"C:\Users\me").Select(s => s.Name).Should().Equal("C:", "Users", "me");
        PathUtil.Segments(@"C:\Users\me").Last().Path.Should().Be(@"C:\Users\me");
        PathUtil.Segments(@"\\nas\share\x").Select(s => s.Name).Should().Equal(@"\\nas\share", "x");
    }
}

public class HistoryTests
{
    [Fact]
    public void Back_forward_roundtrip()
    {
        var h = new NavigationHistory();
        h.Push("A");
        h.Push("B"); // 현재 C
        h.GoBack("C").Should().Be("B");
        h.GoBack("B").Should().Be("A");
        h.GoForward("A").Should().Be("B");
        h.CanGoForward.Should().BeTrue();
        h.Push("B"); // 새 위치로 가면 앞으로 기록은 지워진다
        h.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void Limit_is_enforced()
    {
        var h = new NavigationHistory();
        for (var i = 0; i < 150; i++)
        {
            h.Push("p" + i);
        }

        h.Back.Count.Should().Be(NavigationHistory.Limit);
        h.Back[0].Should().Be("p50");
    }
}

public sealed class FolderTabTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "foldy-tab-" + Guid.NewGuid().ToString("N"));
    private readonly DriveQueueRegistry _queues = new();
    private readonly TabEnvironment _env;

    public FolderTabTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub", "deep"));
        File.WriteAllText(Path.Combine(_root, "b10.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "b2.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "hidden.txt"), "x");
        File.SetAttributes(Path.Combine(_root, "hidden.txt"), FileAttributes.Hidden);
        _env = new TabEnvironment(new LocalProvider(_queues), new InlineDispatcher());
    }

    public async ValueTask DisposeAsync()
    {
        await _queues.DisposeAsync();
        File.SetAttributes(Path.Combine(_root, "hidden.txt"), FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Navigate_lists_sorted_and_hides_hidden()
    {
        var tab = new FolderTabViewModel(_env);
        await tab.NavigateAsync(_root);
        tab.State.Should().Be(LoadState.Idle);
        tab.Items.Select(i => i.Name).Should().Equal("sub", "b2.txt", "b10.txt");
        tab.Title.Should().Be(Path.GetFileName(_root));
    }

    [Fact]
    public async Task Up_back_forward_and_focus_on_previous_folder()
    {
        var tab = new FolderTabViewModel(_env);
        await tab.NavigateAsync(_root);
        await tab.NavigateAsync(Path.Combine(_root, "sub"));
        string? focused = null;
        tab.ItemsReplaced += f => focused = f;

        await tab.GoUpAsync();
        tab.Path.Should().Be(_root);
        focused.Should().Be("sub", "위로 가면 방금 있던 폴더에 포커스");

        await tab.GoBackAsync();
        tab.Path.Should().Be(Path.Combine(_root, "sub"));
        await tab.GoForwardAsync();
        tab.Path.Should().Be(_root);
    }

    [Fact]
    public async Task Invalid_path_shows_error_and_stays()
    {
        var tab = new FolderTabViewModel(_env);
        await tab.NavigateAsync(_root);
        await tab.NavigateAsync(Path.Combine(_root, "없는 폴더"));
        tab.State.Should().Be(LoadState.Error);
        tab.Error.Should().Contain("찾을 수 없어요");
        tab.Path.Should().Be(_root, "잘못된 경로면 현재 위치에 머문다");
        tab.Items.Should().HaveCount(3);

        await tab.NavigateAsync("상대경로");
        tab.Error.Should().Contain("형식");
    }

    [Fact]
    public async Task Type_ahead_wraps()
    {
        var tab = new FolderTabViewModel(_env);
        await tab.NavigateAsync(_root);
        tab.FindPrefix("b", 0).Should().Be(1);
        tab.FindPrefix("b", 2).Should().Be(2);
        tab.FindPrefix("s", 2).Should().Be(0, "끝에서 처음으로 돌아간다");
        tab.FindPrefix("zz", 0).Should().Be(-1);
    }

    [Fact]
    public async Task Sort_toggle_reorders()
    {
        var tab = new FolderTabViewModel(_env);
        await tab.NavigateAsync(_root);
        await tab.SetSortAsync(SortKey.Name); // 이름 내림차순
        tab.Items.Select(i => i.Name).Should().Equal("sub", "b10.txt", "b2.txt");
    }
}

public sealed class SessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-sess-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static ShellViewModel NewShell()
    {
        var env = new TabEnvironment(new FakeProvider(), new InlineDispatcher());
        return new ShellViewModel(env, new ShellServices(null!, null!, null!));
    }

    [Fact]
    public void Capture_restore_roundtrip_through_disk()
    {
        var shell = NewShell();
        var a = shell.Left.NewFolderTab(@"C:\A");
        a.History.Restore([@"C:\Prev"], []);
        a.FocusName = "x.txt";
        shell.Left.NewFolderTab(@"C:\B");
        shell.Right.NewFolderTab(@"D:\");
        shell.Activate(shell.Right);
        var state = shell.Capture(new WindowPlacement(10, 20, 800, 600, false), 0.4);

        var file = Path.Combine(_dir, "session.json");
        JsonFileStore.Save(file, state);
        var loaded = JsonFileStore.Load(file, () => new SessionState());

        var restored = NewShell();
        restored.Restore(loaded, @"C:\");
        restored.Left.Tabs.Select(t => t.Location).Should().Equal(@"C:\A", @"C:\B");
        ((FolderTabViewModel)restored.Left.Tabs[0]).History.Back.Should().Equal(@"C:\Prev");
        ((FolderTabViewModel)restored.Left.Tabs[0]).FocusName.Should().Be("x.txt");
        restored.Left.Tabs.Select(t => t.ColorIndex).Should().Equal(shell.Left.Tabs.Select(t => t.ColorIndex));
        restored.ActivePane.Should().Be(restored.Right);
        loaded.Window!.Width.Should().Be(800);
    }

    [Fact]
    public void Broken_file_is_kept_and_defaults_used()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, "{ not json");
        var s = JsonFileStore.Load(file, () => new AppSettings());
        s.Theme.Should().Be("system");
        Directory.GetFiles(_dir, "settings.broken-*.json").Should().ContainSingle();
    }

    [Fact]
    public void Tabs_get_distinct_onenote_colors_and_last_tab_never_closes()
    {
        var shell = NewShell();
        var t1 = shell.Left.NewFolderTab(@"C:\1");
        var t2 = shell.Left.NewFolderTab(@"C:\2");
        t1.ColorIndex.Should().NotBe(t2.ColorIndex);
        shell.Right.NewFolderTab(@"C:\r").ColorIndex.Should().NotBe(t1.ColorIndex, "좌우 첫 탭 색이 다르다");

        shell.Left.CloseTab(t2);
        shell.Left.CloseTab(t1);
        shell.Left.Tabs.Should().ContainSingle();
    }

    private sealed class FakeProvider : IFileSystemProvider
    {
        public ProviderId Id => ProviderId.Local;

        public async IAsyncEnumerable<IReadOnlyList<FsEntry>> ListBatchedAsync(FsPath path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return [];
        }
    }
}
