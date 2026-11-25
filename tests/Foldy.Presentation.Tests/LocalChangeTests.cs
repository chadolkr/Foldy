using Foldy.Core.FileSystem;
using Foldy.Presentation.Services;
using Foldy.Presentation.Shell;
using Foldy.Presentation.Tabs;

namespace Foldy.Presentation.Tests;

/// <summary>
/// Foldy가 직접 한 이름 바꾸기·새 폴더·삭제는 드라이브 알림이나 새로 고침을 기다리지 않고 목록에 바로 보인다.
/// 느린 드라이브(회사 보안 드라이브 K:)는 바꾼 뒤에도 한동안 옛 목록을 돌려준다 (사용자 2026-11-24).
/// </summary>
public class LocalChangeTests
{
    private const string Dir = @"K:\문서";

    private static FsEntry E(string name, bool folder = false) => new(
        name, FsPath.Local(Path.Combine(Dir, name)), folder ? FsKind.Folder : FsKind.File, 10, null,
        new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc), default, folder ? FileAttributes.Directory : FileAttributes.Normal, null);

    /// <summary>돌려줄 목록을 마음대로 바꾸는 드라이브. Gate를 걸면 다음 읽기가 풀어 줄 때까지 멈춘다 (느린 드라이브).</summary>
    private sealed class ListingProvider : IFileSystemProvider
    {
        public List<FsEntry> Listing { get; set; } = [];

        public TaskCompletionSource? Gate { get; set; }

        public ProviderId Id => ProviderId.Local;

        public async IAsyncEnumerable<IReadOnlyList<FsEntry>> ListBatchedAsync(FsPath path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            if (Gate is { } g)
            {
                await g.Task.WaitAsync(ct);
            }

            await Task.Yield();
            yield return [.. Listing];
        }
    }

    private sealed class FakeOps : IFileOperationService
    {
        public Task<FileOpResult> CopyAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) => Task.FromResult(FileOpResult.Ok());

        public Task<FileOpResult> MoveAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) => Task.FromResult(FileOpResult.Ok());

        public Task<FileOpResult> DeleteAsync(IReadOnlyList<string> paths, bool recycle, IProgress<FileOpProgress>? progress, CancellationToken ct) => Task.FromResult(FileOpResult.Ok());

        public Task<FileOpResult> RenameAsync(string path, string newName, CancellationToken ct) => Task.FromResult(FileOpResult.Ok());

        public Task<FileOpResult> NewFolderAsync(string parent, string name, CancellationToken ct) => Task.FromResult(FileOpResult.Ok(Path.Combine(parent, name)));
    }

    private static async Task<FolderTabViewModel> OpenAsync(ListingProvider p)
    {
        var tab = new FolderTabViewModel(new TabEnvironment(p, new InlineDispatcher()));
        await tab.NavigateAsync(Dir);
        return tab;
    }

    private static string[] Names(FolderTabViewModel t) => [.. t.Items.Select(i => i.Name)];

    [Fact]
    public async Task Rename_shows_new_name_even_when_drive_still_lists_old_name()
    {
        var p = new ListingProvider { Listing = [E("보고서.docx"), E("사진.jpg")] };
        var tab = await OpenAsync(p);

        tab.ApplyLocalChanges(["보고서.docx"], [E("최종 보고서.docx")], focus: "최종 보고서.docx");
        Names(tab).Should().BeEquivalentTo("최종 보고서.docx", "사진.jpg");
        tab.FocusName.Should().Be("최종 보고서.docx");

        await tab.RefreshAsync(); // 드라이브는 아직 옛 이름
        Names(tab).Should().BeEquivalentTo(new[] { "최종 보고서.docx", "사진.jpg" }, "새로 고쳐도 옛 이름으로 돌아가지 않는다");

        p.Listing = [E("최종 보고서.docx"), E("사진.jpg")];
        await tab.RefreshAsync(); // 드라이브가 따라왔다: 기록을 놓는다
        p.Listing = [E("보고서.docx"), E("사진.jpg")];
        await tab.RefreshAsync(); // 그 뒤 다른 데서 다시 바꾼 것은 그대로 보인다
        Names(tab).Should().BeEquivalentTo("보고서.docx", "사진.jpg");
    }

    [Fact]
    public async Task Changes_are_let_go_after_the_hold_time()
    {
        var p = new ListingProvider { Listing = [E("a.txt")] };
        var tab = await OpenAsync(p);
        tab.LocalChangeHold = TimeSpan.FromMilliseconds(100);
        tab.ApplyLocalChanges(["a.txt"], []);
        Names(tab).Should().BeEmpty();

        await Task.Delay(250, TestContext.Current.CancellationToken);
        await tab.RefreshAsync();
        Names(tab).Should().Equal(new[] { "a.txt" }, "오래된 기록은 드라이브가 보여 주는 것을 가리지 않는다");
    }

    [Fact]
    public async Task Case_only_rename_and_rename_twice()
    {
        var p = new ListingProvider { Listing = [E("memo.txt")] };
        var tab = await OpenAsync(p);

        tab.ApplyLocalChanges(["memo.txt"], [E("Memo.txt")]);
        await tab.RefreshAsync();
        Names(tab).Should().Equal("Memo.txt");

        tab.ApplyLocalChanges(["Memo.txt"], [E("메모.txt")]);
        await tab.RefreshAsync();
        Names(tab).Should().Equal("메모.txt");
    }

    [Fact]
    public async Task Delete_then_create_same_name()
    {
        var p = new ListingProvider { Listing = [E("새 폴더", folder: true), E("b.txt")] };
        var tab = await OpenAsync(p);

        tab.ApplyLocalChanges(["b.txt"], []);
        tab.ApplyLocalChanges([], [E("b.txt")]);
        await tab.RefreshAsync();
        Names(tab).Should().BeEquivalentTo("새 폴더", "b.txt");

        tab.ApplyLocalChanges(["새 폴더"], []);
        await tab.RefreshAsync();
        Names(tab).Should().Equal("b.txt");
    }

    /// <summary>디자인 원칙 11 "안내는 손이 있는 자리에": 안 되는 이유는 화면이 손이 있는 자리에 보여 주고, 못 보여 줄 때만 상태 칸에 쓴다.</summary>
    [Fact]
    public async Task Why_a_rename_failed_is_shown_where_the_hands_are()
    {
        var p = new ListingProvider { Listing = [E("old.txt")] };
        var env = new TabEnvironment(p, new InlineDispatcher());
        var shell = new ShellViewModel(env, new ShellServices(new FakeOps(), null!, null!));
        var tab = shell.Left.NewFolderTab(Dir);
        await tab.NavigateAsync(Dir);

        var shown = new List<string>();
        shell.ShowHint = t =>
        {
            shown.Add(t);
            return true;
        };
        (await shell.RenameAsync(tab, tab.Items[0], "a/b.txt")).Should().BeFalse();
        shown.Should().ContainSingle().Which.Should().Be(Foldy.Core.Localization.Loc.T("ops.badName"));
        shell.Status.Should().BeEmpty("아래 상태 칸에는 쓰지 않는다");

        shell.ShowHint = _ => false; // 보일 곳이 없으면(창이 안 보일 때) 상태 칸에
        (await shell.RenameAsync(tab, tab.Items[0], "a/b.txt")).Should().BeFalse();
        shell.Status.Should().Be(Foldy.Core.Localization.Loc.T("ops.badName"));
    }

    [Fact]
    public async Task Rename_and_new_folder_do_not_wait_for_a_slow_refresh()
    {
        var p = new ListingProvider { Listing = [E("old.txt")] };
        var env = new TabEnvironment(p, new InlineDispatcher());
        var shell = new ShellViewModel(env, new ShellServices(new FakeOps(), null!, null!));
        var tab = shell.Left.NewFolderTab(Dir);
        await tab.NavigateAsync(Dir);
        var other = shell.Right.NewFolderTab(Dir); // 오른쪽도 같은 폴더
        await other.NavigateAsync(Dir);

        p.Gate = new TaskCompletionSource(); // 이제 드라이브가 아주 느리다
        var renamed = shell.RenameAsync(tab, tab.Items[0], "new.txt");
        renamed.IsCompleted.Should().BeTrue("새로 고침이 끝나기를 기다리지 않는다");
        (await renamed).Should().BeTrue();
        Names(tab).Should().Equal("new.txt");
        Names(other).Should().Equal("new.txt");

        var created = shell.NewFolderAsync(tab, "새 폴더");
        created.IsCompleted.Should().BeTrue();
        (await created).Should().Be("새 폴더");
        Names(tab).Should().Equal("새 폴더", "new.txt");
        tab.FocusName.Should().Be("새 폴더", "바로 이름을 입력할 수 있게");

        p.Gate.SetResult(); // 느린 드라이브가 옛 목록을 늦게 돌려줘도
        await tab.RefreshAsync();
        Names(tab).Should().Equal("새 폴더", "new.txt");
    }
}
