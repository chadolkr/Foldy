using System.Runtime.CompilerServices;
using Foldy.Core.FileSystem;
using Foldy.Presentation.Services;
using Foldy.Presentation.Tree;

namespace Foldy.Presentation.Tests;

public class FolderTreeTests
{
    private const string Server = "sftp://me@example.test:22/";

    [Fact]
    public void Sftp_tab_shows_that_servers_tree_and_local_tab_brings_the_drives_back()
    {
        var tree = new FolderTreeViewModel(new FakeProvider(), [new DriveItem(@"C:\", DriveKind.Fixed)]);
        var local = tree.Roots.Select(r => r.Path).ToList();
        local.Should().Contain(@"C:\");

        tree.ShowFor(Server + "home/me");
        tree.SftpRoot.Should().Be(Server);
        tree.Roots.Select(r => r.Path).Should().Equal(Server + "home/me", Server);

        tree.ShowFor(Server + "var/log"); // 같은 서버 안에서는 트리를 그대로 둔다
        tree.Roots.Select(r => r.Path).Should().Equal(Server + "home/me", Server);

        tree.SetDrives([new DriveItem(@"C:\", DriveKind.Fixed), new DriveItem(@"D:\", DriveKind.Fixed)]);
        tree.Roots.Select(r => r.Path).Should().Equal([Server + "home/me", Server], "SFTP를 보는 동안 드라이브 변화는 로컬 트리에만");

        tree.ShowFor(@"C:\Users");
        tree.SftpRoot.Should().BeNull();
        tree.Roots.Select(r => r.Path).Should().Contain(@"D:\").And.Contain(@"C:\");
    }

    [Fact]
    public async Task Sftp_tree_opens_expanded_and_lists_through_the_sftp_provider()
    {
        var fake = new FakeProvider();
        var tree = new FolderTreeViewModel(fake, []);
        tree.ShowFor(Server);
        var root = tree.Roots.Single();

        // 접속하면 서버 맨 위가 저절로 펼쳐져 폴더가 보인다 (0.9.7, 사용자 요청)
        root.IsExpanded.Should().BeTrue();
        for (var i = 0; i < 100 && root.Children.Count < 2; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        fake.Requests.Should().ContainSingle().Which.Should().Be(new FsPath(ProviderId.Sftp, Server));
        root.Children.Select(c => c.Path).Should().Equal(Server + "etc", Server + "home");
    }

    [Fact]
    public async Task Tree_follows_the_current_folder_expanding_parents_and_highlighting_it()
    {
        // 사용자 2026-11-21: "양쪽 탐색기에 현재 폴더 위치가 표시되어야": 주소 입력·Ctrl+Space 등 어떻게 옮겨도 트리가 따라간다
        var fake = new LocalTreeProvider();
        var tree = new FolderTreeViewModel(fake, [new DriveItem(@"X:\", DriveKind.Fixed)]);
        var chain = await tree.RevealAsync(@"X:\프로젝트\2026\보고서");
        chain.Select(n => n.Path).Should().Equal(@"X:\", @"X:\프로젝트", @"X:\프로젝트\2026", @"X:\프로젝트\2026\보고서");
        chain.Take(3).Should().OnlyContain(n => n.IsExpanded);
        chain[^1].IsSelected.Should().BeTrue();

        // 다른 곳으로 옮기면 강조도 옮겨 간다
        var next = await tree.RevealAsync(@"X:\프로젝트");
        next[^1].IsSelected.Should().BeTrue();
        chain[^1].IsSelected.Should().BeFalse();

        // 트리에 없는 곳(숨김 폴더 등)은 아무것도 강조하지 않는다
        await tree.RevealAsync(@"X:\없는폴더\a");
        next[^1].IsSelected.Should().BeFalse();
        FolderTreeViewModel.Under(@"X:\프로젝트2", @"X:\프로젝트").Should().BeFalse("이름이 앞부분만 같은 폴더는 아래가 아니다");
        FolderTreeViewModel.Same(@"X:\프로젝트\", @"x:\프로젝트").Should().BeTrue();
    }

    private sealed class LocalTreeProvider : IFileSystemProvider
    {
        private static readonly Dictionary<string, string[]> Dirs = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"X:\"] = ["프로젝트", "사진"],
            [@"X:\프로젝트"] = ["2025", "2026"],
            [@"X:\프로젝트\2026"] = ["보고서"],
        };

        public ProviderId Id => ProviderId.Local;

        public async IAsyncEnumerable<IReadOnlyList<FsEntry>> ListBatchedAsync(FsPath path, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            var names = Dirs.TryGetValue(path.Value.Length > 3 ? path.Value.TrimEnd('\\') : path.Value, out var n) ? n : [];
            yield return [.. names.Select(x => new FsEntry(x, FsPath.Local(Path.Combine(path.Value, x)), FsKind.Folder, 0, null, default, default, FileAttributes.Directory, null))];
        }
    }

    private sealed class FakeProvider : IFileSystemProvider
    {
        public List<FsPath> Requests { get; } = [];

        public ProviderId Id => ProviderId.Local;

        public async IAsyncEnumerable<IReadOnlyList<FsEntry>> ListBatchedAsync(FsPath path, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(path);
            await Task.Yield();
            FsEntry Dir(string n) => new(n, new FsPath(path.Provider, path.Value + n), FsKind.Folder, 0, null, default, default, FileAttributes.Directory, null);
            yield return [Dir("home"), Dir("etc"), new FsEntry("a.txt", path, FsKind.File, 1, null, default, default, FileAttributes.Normal, null)];
        }
    }
}

public class SftpDeleteConfirmTests
{
    [Fact]
    public void Message_says_there_is_no_recycle_bin_and_lists_count_and_names()
    {
        var paths = Enumerable.Range(1, 7).Select(i => $"sftp://me@example.test:22/data/f{i}.txt").ToList();
        var m = Foldy.Presentation.Shell.ShellViewModel.SftpDeleteConfirmMessage(paths);
        m.Should().Contain("휴지통이 없어요").And.Contain("7개").And.Contain("f1.txt").And.Contain("f5.txt").And.Contain("외 2개");
        m.Should().NotContain("f6.txt");
    }
}
