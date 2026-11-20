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
