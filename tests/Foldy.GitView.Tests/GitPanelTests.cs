using Foldy.Git;
using Foldy.Presentation.Git;

namespace Foldy.GitView.Tests;

public class GitPanelTests
{
    private static GitFileEntry Mod(string p) => new(p, null, GitChange.None, GitChange.Modified);

    private static (GitTabViewModel vm, FakeGit git) Make(Action<FakeGit>? setup = null)
    {
        var git = new FakeGit();
        setup?.Invoke(git);
        return (new GitTabViewModel(git, @"C:\repos\Foldy", new FakeCommitFiles()), git);
    }

    private static FileDiff SampleDiff(int added = 2) => GitDiffParser.Parse(
        "@@ -1,2 +1," + (2 + added - 1) + " @@\n one\n-two\n" + string.Concat(Enumerable.Range(0, added).Select(i => "+new" + i + "\n")));

    [Fact]
    public async Task Selecting_a_change_shows_its_diff_in_the_side_panel()
    {
        var (vm, git) = Make(g =>
        {
            g.Files = [Mod("src/a.cs")];
            g.Diffs["src/a.cs"] = SampleDiff();
        });
        await vm.RefreshAsync();
        vm.HasSidePanel.Should().BeFalse();

        vm.SelectedChange = vm.ChangeRows.First(r => !r.IsHeader);
        await vm.PanelLoadingTask;

        vm.PanelMode.Should().Be(GitSidePanelMode.Diff);
        vm.PanelTitle.Should().Be("a.cs");
        vm.DiffLines.Should().Contain(l => l.Kind == DiffLineKind.Added);
        vm.DiffSummary.Should().Contain("+2");
        vm.ShowInlineDiff.Should().BeTrue();
        vm.HasPanelMessage.Should().BeFalse();
        git.Calls.Should().Contain("diff:Unstaged:src/a.cs:20000");
    }

    [Fact]
    public async Task Side_by_side_toggle_builds_rows_and_binary_shows_message()
    {
        var (vm, _) = Make(g =>
        {
            g.Files = [Mod("a.cs"), Mod("img.png")];
            g.Diffs["a.cs"] = SampleDiff();
            g.Diffs["img.png"] = GitDiffParser.Parse("Binary files a/img.png and b/img.png differ\n");
        });
        await vm.RefreshAsync();
        await vm.ShowDiffAsync(new GitDiffTarget(GitDiffKind.Unstaged, "a.cs"));
        vm.SideBySide = true;
        vm.ShowSideBySideDiff.Should().BeTrue();
        vm.SideRows.Should().Contain(r => r.Left != null && r.Right != null && r.Left.Kind == DiffLineKind.Removed);

        await vm.ShowDiffAsync(new GitDiffTarget(GitDiffKind.Unstaged, "img.png"));
        vm.PanelMessage.Should().Be("바이너리 파일");
        vm.ShowSideBySideDiff.Should().BeFalse();
    }

    [Fact]
    public async Task Large_diff_can_show_more()
    {
        var (vm, git) = Make(g => g.Diffs["big.txt"] = GitDiffParser.Parse("@@ -0,0 +1,50 @@\n" + string.Concat(Enumerable.Range(0, 50).Select(i => "+l" + i + "\n")), 10));
        await vm.ShowDiffAsync(new GitDiffTarget(GitDiffKind.Unstaged, "big.txt"));
        vm.DiffTruncated.Should().BeTrue();
        vm.DiffTruncatedText.Should().Contain("10").And.Contain("51");
        await vm.ShowMoreDiffAsync();
        git.Calls.Should().Contain("diff:Unstaged:big.txt:40000");
    }

    [Fact]
    public async Task Diff_failure_shows_a_message_not_a_crash()
    {
        var (vm, _) = Make(g => g.FailDiff = GitResult.Fail(GitFailure.Other, "bad object", 128));
        await vm.ShowDiffAsync(new GitDiffTarget(GitDiffKind.Staged, "x.txt"));
        vm.PanelMessage.Should().Contain("bad object");
        vm.ShowInlineDiff.Should().BeFalse();
    }

    [Fact]
    public async Task Commit_file_selection_shows_that_commits_diff()
    {
        var (vm, git) = Make(g => g.Log = [new CommitNode("h00001", [], "Kim", "k@x", DateTimeOffset.UnixEpoch, "first", [])]);
        await vm.RefreshAsync();
        vm.SelectedCommit = vm.Commits[0];
        await vm.CommitFilesLoading;
        vm.SelectedCommitFile = vm.CommitFiles[0];
        await vm.PanelLoadingTask;
        git.Calls.Should().Contain("diff:Commit:src/a.cs:20000");
        vm.PanelSubtitle.Should().Contain("커밋");
    }

    [Fact]
    public async Task Blame_rows_alternate_shading_and_hash_click_selects_commit()
    {
        var now = DateTimeOffset.UtcNow;
        BlameLine L(int no, string h, int block, bool start) => new(no, h, "Kim", now.AddDays(-3), "msg", "code" + no, false, false, block, start);
        var (vm, git) = Make(g =>
        {
            g.Log = [new CommitNode("h00002", ["h00001"], "Lee", "l@x", now, "second", []), new CommitNode("h00001", [], "Kim", "k@x", now.AddDays(-3), "first", [])];
            g.Blame = [L(1, "h00001", 0, true), L(2, "h00001", 0, false), L(3, "h00002", 1, true)];
        });
        await vm.RefreshAsync();
        await vm.ShowBlameAsync("src/a.cs");
        git.Calls.Should().Contain("blame:src/a.cs:");
        vm.PanelMode.Should().Be(GitSidePanelMode.Blame);
        vm.ShowBlameList.Should().BeTrue();
        vm.BlameRows.Select(r => r.Shaded).Should().Equal(false, false, true);
        vm.BlameRows[0].ShaShown.Should().Be("h00001");
        vm.BlameRows[1].ShaShown.Should().BeEmpty();
        vm.BlameRows[0].DateShown.Should().Be("3일 전");

        await vm.SelectCommitByHashAsync("h00001");
        vm.SelectedCommit!.Node.Hash.Should().Be("h00001");
        vm.ShowLocalChanges.Should().BeFalse();
    }

    [Fact]
    public async Task Blame_of_commit_file_uses_that_revision()
    {
        var (vm, git) = Make(g => g.Log = [new CommitNode("h00001", [], "Kim", "k@x", DateTimeOffset.UnixEpoch, "first", [])]);
        await vm.RefreshAsync();
        vm.SelectedCommit = vm.Commits[0];
        await vm.ShowBlameForCommitFileAsync(new GitCommitFile("M", "src/a.cs"));
        git.Calls.Should().Contain("blame:src/a.cs:h00001");
    }

    [Theory]
    [InlineData(10, "방금 전")]
    [InlineData(5 * 60, "5분 전")]
    [InlineData(3 * 3600, "3시간 전")]
    [InlineData(4 * 86400, "4일 전")]
    [InlineData(70 * 86400, "2개월 전")]
    [InlineData(800 * 86400, "2년 전")]
    public void Relative_time_is_korean(int seconds, string expected)
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(2000);
        GitTime.Relative(now.AddSeconds(-seconds), now).Should().Be(expected);
    }

    [Fact]
    public async Task Changes_are_grouped_and_checkbox_toggles_stage()
    {
        var (vm, git) = Make(g => g.Files =
        [
            new GitFileEntry("staged.cs", null, GitChange.Added, GitChange.None),
            Mod("mod.cs"),
            new GitFileEntry("new.txt", null, GitChange.None, GitChange.Untracked),
        ]);
        await vm.RefreshAsync();
        vm.ChangeRows.Where(r => r.IsHeader).Select(r => r.HeaderLabel).Should().Equal("스테이지됨 (1)", "변경됨 (1)", "새 파일 (1)");
        vm.ChangeRows.Where(r => !r.IsHeader).Select(r => r.IsChecked).Should().Equal(true, false, false);
        vm.ChangeRows.First(r => r.Item?.Path == "new.txt").Item!.Label.Should().Be("새 파일");

        await vm.ToggleStageAsync(vm.ChangeRows.First(r => r.Item?.Path == "mod.cs").Item!);
        git.Calls.Should().Contain("stage:mod.cs");
        vm.Staged.Select(i => i.Path).Should().Contain("mod.cs");

        await vm.ToggleStageAsync(vm.ChangeRows.First(r => r.Item is { IsStaged: true, Path: "mod.cs" }).Item!);
        git.Calls.Should().Contain("unstage:mod.cs");

        await vm.SelectAllChangesAsync();
        vm.Unstaged.Should().BeEmpty();
        await vm.ClearAllChangesAsync();
        vm.Staged.Should().BeEmpty();
    }

    [Fact]
    public async Task Selection_survives_status_refresh_and_diff_closes_when_file_disappears()
    {
        var (vm, git) = Make(g => { g.Files = [Mod("a.cs")]; g.Diffs["a.cs"] = SampleDiff(); });
        await vm.RefreshAsync();
        vm.SelectedChange = vm.ChangeRows.First(r => !r.IsHeader);
        await vm.PanelLoadingTask;
        await vm.RefreshStatusOnlyAsync();
        vm.SelectedChange.Should().NotBeNull();
        vm.HasSidePanel.Should().BeTrue();

        git.Files = [];
        await vm.RefreshStatusOnlyAsync();
        vm.HasSidePanel.Should().BeFalse();
        vm.ShowNoChanges.Should().BeTrue();
    }

    [Fact]
    public async Task Commit_button_explains_why_it_is_disabled()
    {
        var (vm, git) = Make(g => g.Files = [Mod("a.cs")]);
        await vm.RefreshAsync();
        vm.CanCommit.Should().BeFalse();
        vm.CommitTooltip.Should().Contain("체크");
        await vm.SelectAllChangesAsync();
        vm.CommitTooltip.Should().Contain("메시지");
        vm.CommitMessage = "고침";
        vm.CanCommit.Should().BeTrue();
        vm.CommitDisabledReason.Should().BeEmpty();
        vm.CommitTooltip.Should().Contain("1개 파일");

        await vm.CommitAsync();
        git.Calls.Should().Contain("commit:고침:False");
        vm.ToastText.Should().Be("커밋 완료");
    }

    [Fact]
    public async Task Push_reports_commit_count_in_toast()
    {
        var (vm, _) = Make(g => g.Ahead = 3);
        await vm.RefreshAsync();
        await vm.PushAsync();
        vm.ToastText.Should().Be("올리기 완료: 3개 커밋");
    }

    [Fact]
    public async Task Not_a_repository_offers_init_and_init_recovers()
    {
        var (vm, git) = Make(g => g.FailStatus = GitResult.Fail(GitFailure.NotARepository, "fatal: not a git repository", 128));
        await vm.RefreshAsync();
        vm.NeedsInit.Should().BeTrue();
        vm.ShowInitOffer.Should().BeTrue();
        vm.ShowSetup.Should().BeTrue();

        git.FailStatus = null;
        await vm.InitRepositoryAsync();
        git.Calls.Should().Contain("init");
        vm.NeedsInit.Should().BeFalse();
        vm.ShowSetup.Should().BeFalse();
    }

    [Fact]
    public void Auth_failure_has_plain_korean_explanation()
    {
        var t = GitErrorText.Describe(GitResult.Fail(GitFailure.AuthenticationFailed, "x", 128));
        t.Should().Contain("로그인").And.Contain("권한");
    }

    [Fact]
    public async Task Tabs_switch_between_changes_and_history()
    {
        var (vm, _) = Make();
        vm.IsHistoryTab.Should().BeFalse();
        vm.ShowHistoryTab();
        vm.IsHistoryTab.Should().BeTrue();
        vm.ShowLocalChanges.Should().BeFalse();
        vm.ShowChangesTab();
        vm.ShowLocalChanges.Should().BeTrue();
        await Task.CompletedTask;
    }
}
