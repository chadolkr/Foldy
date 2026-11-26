using Foldy.Core.FileSystem;
using Foldy.Presentation.Persistence;
using Foldy.Presentation.Services;
using Foldy.Presentation.Sidebar;

namespace Foldy.Presentation.Tests;

public class QuickAccessTests
{
    [Fact]
    public void Visit_puts_newest_first_and_caps_the_list()
    {
        var q = new QuickAccess();
        for (var i = 0; i < QuickAccess.MaxRecent + 3; i++)
        {
            q.Visit($@"C:\f{i}");
        }

        q.Recent.Should().HaveCount(QuickAccess.MaxRecent);
        q.Recent[0].Path.Should().Be($@"C:\f{QuickAccess.MaxRecent + 2}");
    }

    [Fact]
    public void Visiting_again_moves_to_top_without_duplicates_ignoring_case()
    {
        var q = new QuickAccess();
        q.Visit(@"C:\a");
        q.Visit(@"C:\b");
        q.Visit(@"c:\A\");
        q.Recent.Select(r => r.Path).Should().Equal(@"C:\a", @"C:\b");
    }

    [Fact]
    public void Visiting_the_top_folder_raises_no_change()
    {
        var q = new QuickAccess();
        q.Visit(@"C:\a");
        var changes = 0;
        q.Changed += () => changes++;
        q.Visit(@"C:\a");
        changes.Should().Be(0);
    }

    [Fact]
    public void Pinned_folders_leave_recent_and_are_not_added_back()
    {
        var q = new QuickAccess();
        q.Visit(@"C:\work");
        q.Pin(@"C:\work").Should().BeTrue();
        q.Recent.Should().BeEmpty();
        q.Visit(@"C:\work");
        q.Recent.Should().BeEmpty();
        q.Pin(@"c:\WORK").Should().BeFalse("이미 고정됨");
        q.IsPinned(@"C:\Work").Should().BeTrue();
    }

    [Fact]
    public void Unpin_move_and_invalid_paths()
    {
        var q = new QuickAccess();
        q.Pin(@"C:\a");
        q.Pin(@"C:\b");
        q.Move(@"C:\b", -1).Should().BeTrue();
        q.Pinned.Select(p => p.Path).Should().Equal(@"C:\b", @"C:\a");
        q.Move(@"C:\b", -1).Should().BeFalse("맨 위");
        q.Unpin(@"C:\a").Should().BeTrue();
        q.Unpin(@"C:\a").Should().BeFalse();
        q.Pin("상대경로").Should().BeFalse();
        q.Pin("").Should().BeFalse();
        q.Visit(null);
        q.Recent.Should().BeEmpty();
    }

    [Fact]
    public void Sftp_paths_compare_case_sensitively()
    {
        var q = new QuickAccess();
        q.Visit("sftp://me@host/home/A");
        q.Visit("sftp://me@host/home/a");
        q.Recent.Should().HaveCount(2);
    }

    [Fact]
    public void Snapshot_round_trips_and_cleans_bad_state()
    {
        var q = new QuickAccess(new QuickAccessState
        {
            Pinned = [@"C:\p", @"c:\P", "bad"],
            Recent = [@"C:\r", @"C:\p"],
        });
        q.Pinned.Select(p => p.Path).Should().Equal(@"C:\p");
        q.Recent.Select(p => p.Path).Should().Equal(@"C:\r");
        var again = new QuickAccess(q.Snapshot());
        again.Snapshot().Should().BeEquivalentTo(q.Snapshot());
    }

    [Fact]
    public void Folder_name_is_the_last_part()
    {
        new QuickFolder(@"C:\Users\me\Documents").Name.Should().Be("Documents");
    }
}

public class DriveSpaceTests
{
    [Fact]
    public void Space_text_and_used_fraction()
    {
        var d = new DriveItem(@"C:\", DriveKind.Fixed, "로컬 디스크", FreeBytes: 100L << 30, TotalBytes: 400L << 30);
        d.UsedFraction.Should().BeApproximately(0.75, 1e-9);
        d.IsAlmostFull.Should().BeFalse();
        d.SpaceText.Should().Contain("남음").And.Contain("GB");
    }

    [Fact]
    public void Almost_full_and_unknown()
    {
        new DriveItem(@"D:\", DriveKind.Fixed, FreeBytes: 5, TotalBytes: 100).IsAlmostFull.Should().BeTrue();
        var unknown = new DriveItem(@"E:\", DriveKind.Removable);
        unknown.HasSpace.Should().BeFalse();
        unknown.UsedFraction.Should().Be(0);
        unknown.SpaceText.Should().BeEmpty();
        new DriveItem(@"Z:\", DriveKind.Network, Offline: true).SpaceText.Should().Be("연결 안 됨");
    }

    [Fact]
    public void Old_session_json_gets_sidebar_and_tree_defaults()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<SessionState>("{\"schemaVersion\":1}", JsonFileStore.Options)!;
        s.SidebarExpanded.Should().BeTrue();
        s.LeftTreeWidth.Should().Be(SessionState.DefaultTreeWidth);
        s.RightTreeWidth.Should().Be(SessionState.DefaultTreeWidth);
    }
}

public class StandardFolderTests
{
    private static readonly StandardFolder[] Std =
    [
        new(@"C:\Users\me\OneDrive\바탕 화면", "바탕 화면", "\uE8FC"),
        new(@"D:\Downloads", "다운로드", "\uE896"),
    ];

    [Fact]
    public void Standard_folders_stay_out_of_recent_ignoring_case()
    {
        var q = new QuickAccess(new QuickAccessState { Recent = [@"d:\downloads", @"C:\work"] }, Std);
        q.Standard.Select(s => s.Name).Should().Equal("바탕 화면", "다운로드");
        q.Recent.Select(r => r.Path).Should().Equal(@"C:\work");

        var changes = 0;
        q.Changed += () => changes++;
        q.Visit(@"D:\DOWNLOADS\");
        q.Recent.Select(r => r.Path).Should().Equal(@"C:\work");
        changes.Should().Be(0);
        q.IsStandard(@"c:\users\me\onedrive\바탕 화면").Should().BeTrue();

        q.ClearRecent();
        q.Standard.Should().HaveCount(2, "최근 폴더를 지워도 기본 폴더는 남는다");
        q.Snapshot().Recent.Should().BeEmpty();
    }

    [Fact]
    public void All_uses_known_folder_paths_in_order_and_skips_missing_or_same_folders()
    {
        var paths = new Dictionary<KnownFolder, string?>
        {
            [KnownFolder.Desktop] = @"C:\Users\me\OneDrive\Desktop",
            [KnownFolder.Downloads] = @"D:\Downloads",
            [KnownFolder.Documents] = @"C:\Users\me\OneDrive\Documents",
            [KnownFolder.Pictures] = @"C:\Users\me\OneDrive\Documents\", // 옛 PC: 사진 = 문서
            [KnownFolder.Videos] = @"C:\Users\me\Videos",
        };
        var all = StandardFolders.All(k => paths[k], p => !p.EndsWith("Videos", StringComparison.Ordinal));
        all.Select(f => f.Path).Should().Equal(@"C:\Users\me\OneDrive\Desktop", @"D:\Downloads", @"C:\Users\me\OneDrive\Documents");
        all.Select(f => f.Glyph).Should().OnlyHaveUniqueItems("폴더마다 다른 그림");
    }

    [Fact]
    public void Music_is_not_shown()
    {
        KnownFolders.Shown.Should().Equal(KnownFolder.Desktop, KnownFolder.Downloads, KnownFolder.Documents, KnownFolder.Pictures, KnownFolder.Videos);
    }
}
