using Foldy.Core.Organize;
using Foldy.Presentation.Services;
using Foldy.Presentation.Templates;

namespace Foldy.Templates.Tests;

/// <summary>
/// 같은 파일 묶음 (사용자 2026-11-23): 중복 후보가 어느 파일과 같은지 경로로 보이고, 남길 파일을 골라 바꿀 수 있다.
/// 아무것도 지우지 않고, 남기지 않는 것은 _중복 후보로만 옮긴다.
/// </summary>
public class DuplicateKeeperTests
{
    private static readonly DateTime Old = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static OrganizePlan PlanOf(TempDir tmp) =>
        OrganizePlanner.Plan(OrganizeScanner.Scan(tmp.Path, includeSubfolders: true), new OrganizeOptions { Mode = OrganizeMode.ByType }, p => File.Exists(p) || Directory.Exists(p));

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    private static bool IsDupFolder(string rel) => rel == OrganizeOptions.DuplicatesFolder || rel == OrganizeOptions.DuplicatesFolderKo;

    [Fact]
    public void Plan_keeps_the_set_and_reason_shows_where_the_kept_file_is()
    {
        using var tmp = new TempDir();
        tmp.File(@"a\보고서.pdf", "same", Old);
        tmp.File(@"b\보고서.pdf", "same", New);
        var plan = PlanOf(tmp);

        var set = plan.DuplicateSets.Should().ContainSingle().Subject;
        set.Members.Select(m => m.FullPath).Should().Equal(tmp[@"a\보고서.pdf"], tmp[@"b\보고서.pdf"]);
        set.Keeper.FullPath.Should().Be(tmp[@"a\보고서.pdf"], "처음에는 가장 오래된 것을 남긴다");
        set.Members.Should().OnlyContain(m => m.KeepTarget == "문서");

        var dup = plan.Moves.Single(m => m.Source == tmp[@"b\보고서.pdf"]);
        IsDupFolder(dup.TargetFolder).Should().BeTrue();
        dup.Reason.Should().Contain(@"a\보고서.pdf", "어느 폴더의 파일과 같은지 경로로 보인다");
        dup.DuplicateSet.Should().BeSameAs(set);
        plan.Moves.Single(m => m.Source == tmp[@"a\보고서.pdf"]).DuplicateSet.Should().BeSameAs(set);
    }

    [Fact]
    public void Choosing_another_keeper_swaps_and_can_be_switched_back()
    {
        using var tmp = new TempDir();
        tmp.File(@"a\보고서.pdf", "same", Old);
        tmp.File(@"b\보고서.pdf", "same", New);
        var plan = PlanOf(tmp);
        var set = plan.DuplicateSets.Single();
        var a = set.Members[0];
        var b = set.Members[1];

        var change = OrganizePlanner.ChooseKeeper(plan, set, b, Exists);
        change.Added.Should().BeEmpty();
        change.Removed.Should().BeEmpty();
        change.Changed.Should().HaveCount(2);
        set.Keeper.Should().BeSameAs(b);
        var moveA = plan.Moves.Single(m => m.Source == a.FullPath);
        var moveB = plan.Moves.Single(m => m.Source == b.FullPath);
        moveB.TargetFolder.Should().Be("문서");
        moveB.Destination.Should().Be(tmp[@"문서\보고서.pdf"]);
        moveB.Reason.Should().StartWith("종류");
        IsDupFolder(moveA.TargetFolder).Should().BeTrue();
        moveA.Reason.Should().Contain(@"b\보고서.pdf");
        plan.Moves.Should().HaveCount(2, "지우는 것은 없고 옮기는 곳만 바뀐다");

        OrganizePlanner.ChooseKeeper(plan, set, a, Exists);
        moveA.TargetFolder.Should().Be("문서");
        IsDupFolder(moveB.TargetFolder).Should().BeTrue();
        moveB.Reason.Should().Contain(@"a\보고서.pdf");
    }

    [Fact]
    public void Kept_file_already_in_place_is_moved_away_only_when_another_is_chosen()
    {
        using var tmp = new TempDir();
        tmp.File(@"문서\메모.txt", "same", Old);
        tmp.File(@"x\메모.txt", "same", New);
        var plan = PlanOf(tmp);
        var set = plan.DuplicateSets.Single();
        plan.Moves.Should().ContainSingle("이미 제자리인 원본은 옮기지 않는다").Which.Source.Should().Be(tmp[@"x\메모.txt"]);

        var change = OrganizePlanner.ChooseKeeper(plan, set, set.Members.Single(m => m.FullPath == tmp[@"x\메모.txt"]), Exists);
        var added = change.Added.Should().ContainSingle().Subject;
        added.Source.Should().Be(tmp[@"문서\메모.txt"]);
        IsDupFolder(added.TargetFolder).Should().BeTrue();
        var kept = plan.Moves.Single(m => m.Source == tmp[@"x\메모.txt"]);
        kept.TargetFolder.Should().Be("문서");
        kept.Destination.Should().Be(tmp[@"문서\메모 (2).txt"], "옮기기 전에는 원래 파일이 아직 그 자리에 있으니 이름이 겹치지 않게");

        var back = OrganizePlanner.ChooseKeeper(plan, set, set.Members.Single(m => m.FullPath == tmp[@"문서\메모.txt"]), Exists);
        back.Removed.Should().ContainSingle().Which.Should().BeSameAs(added, "다시 고르면 원래 파일은 그 자리에 둔다");
        plan.Moves.Should().ContainSingle().Which.Should().BeSameAs(kept);
        IsDupFolder(kept.TargetFolder).Should().BeTrue();
    }

    [Fact]
    public void Preview_shows_full_paths_and_keep_button_changes_the_plan()
    {
        using var tmp = new TempDir();
        tmp.File(@"문서\메모.txt", "same", Old);
        tmp.File(@"x\메모.txt", "same", New);
        tmp.File(@"y\다른.txt", "other", New);
        var vm = new OrganizeViewModel(tmp.Path, new InlineDispatcher(), journalFolder: tmp["jobs"]);
        vm.ApplyPlan(PlanOf(tmp));

        vm.HasDuplicateSets.Should().BeTrue();
        vm.DuplicatesTabHeader.Should().Contain("1");
        vm.HasDuplicateSelection.Should().BeFalse();

        vm.SelectedMove = vm.Moves.Single(m => m.SourceName == "다른.txt");
        vm.HasDuplicateSelection.Should().BeFalse("같은 파일이 없는 줄은 아래 칸을 보이지 않는다");

        vm.SelectedMove = vm.Moves.Single(m => m.SourceName == "메모.txt");
        vm.HasDuplicateSelection.Should().BeTrue();
        vm.DuplicateMembers.Select(m => m.FullPath).Should().BeEquivalentTo([tmp[@"문서\메모.txt"], tmp[@"x\메모.txt"]], "전체 경로로 보인다");
        var original = vm.DuplicateMembers.Single(m => m.FullPath == tmp[@"문서\메모.txt"]);
        var copy = vm.DuplicateMembers.Single(m => m.FullPath == tmp[@"x\메모.txt"]);
        original.IsKeeper.Should().BeTrue();
        copy.IsKeeper.Should().BeFalse();
        original.Outcome.Should().NotBeEmpty();
        copy.Outcome.Should().Contain(OrganizeOptions.DuplicatesFolder);

        copy.IsKeeper = true;
        original.IsKeeper.Should().BeFalse();
        vm.Moves.Should().HaveCount(3, "원래 파일이 _중복 후보로 가는 줄이 생긴다");
        vm.Moves.Single(m => m.SourceFull == tmp[@"x\메모.txt"]).TargetFolder.Should().Be("문서");
        vm.Moves.Single(m => m.SourceFull == tmp[@"문서\메모.txt"]).IsDuplicate.Should().BeTrue();
        original.Outcome.Should().Contain(OrganizeOptions.DuplicatesFolder);
        vm.DuplicateSets.Single().KeeperRelative.Should().Be(@"x\메모.txt");
        vm.Summary.Should().Contain("3");

        original.IsKeeper = true;
        vm.Moves.Should().HaveCount(2);
        vm.Moves.Single(m => m.SourceFull == tmp[@"x\메모.txt"]).IsIncluded.Should().BeTrue();
    }

    [Fact]
    public void Delphi_and_vb_project_folders_are_left_whole()
    {
        using var tmp = new TempDir();
        tmp.File(@"src\App\App.dproj", "<Project/>");
        tmp.File(@"src\App\Main.pas", "unit Main;");
        tmp.File(@"src\Group\All.groupproj", "<Project/>");
        tmp.File(@"src\Vb\Old.vbp", "Type=Exe");
        tmp.File(@"loose\Util.pas", "unit Util;");
        var scan = OrganizeScanner.Scan(tmp.Path, includeSubfolders: true);
        scan.Entries.Where(e => e.IsProjectFolder).Select(e => e.Name).Should().BeEquivalentTo(["App", "Group", "Vb"]);
        scan.Entries.Should().NotContain(e => e.Name == "Main.pas", "프로젝트 폴더 안으로는 들어가지 않는다");
        scan.Entries.Should().Contain(e => e.Name == "Util.pas");
        FileTypeGroups.GroupOf("Util.pas").Should().Be(FileTypeGroups.Code);
        FileTypeGroups.GroupOf("Main.dfm").Should().Be(FileTypeGroups.Code);
    }

    [Fact]
    public void Reasons_are_counted_most_first_and_check_all_applies_to_what_is_shown()
    {
        using var tmp = new TempDir();
        tmp.File("a.pdf", "A");
        tmp.File("b.pdf", "BB");
        tmp.File("c.jpg", "CCC");
        var vm = new OrganizeViewModel(tmp.Path, new InlineDispatcher(), journalFolder: tmp["jobs"]);
        vm.ApplyPlan(OrganizePlanner.Plan(OrganizeScanner.Scan(tmp.Path, false), new OrganizeOptions { Mode = OrganizeMode.ByType }, Exists));

        vm.Categories.Select(c => c.Name).Should().Equal("모두", "문서", "이미지");
        vm.Categories.Select(c => c.Count).Should().Equal(3, 2, 1);
        vm.Categories[1].BarWidth.Should().BeGreaterThan(vm.Categories[2].BarWidth, "많은 것이 더 긴 막대");
        vm.VisibleMoves.Should().HaveCount(3);

        vm.SelectedCategory = vm.Categories.Single(c => c.Name == "이미지");
        vm.VisibleMoves.Should().ContainSingle().Which.SourceName.Should().Be("c.jpg");
        vm.UncheckAllVisibleCommand.Execute(null);
        vm.Moves.Single(m => m.SourceName == "c.jpg").IsIncluded.Should().BeFalse();
        vm.Moves.Where(m => m.SourceName != "c.jpg").Should().OnlyContain(m => m.IsIncluded, "보이는 것만 바뀐다");
        vm.Categories.Single(c => c.Name == "이미지").Checked.Should().Be(0);
        vm.Categories.Single(c => c.IsAll).Checked.Should().Be(2);
        vm.SelectedCategory!.Name.Should().Be("이미지", "수를 고쳐도 고른 묶음은 그대로");

        vm.SelectedCategory = vm.Categories.Single(c => c.IsAll);
        vm.VisibleMoves.Should().HaveCount(3);
        vm.UncheckAllVisibleCommand.Execute(null);
        vm.ExecuteCommand.CanExecute(null).Should().BeFalse("모두 해제하면 옮길 것이 없다");
        vm.CheckAllVisibleCommand.Execute(null);
        vm.Moves.Should().OnlyContain(m => m.IsIncluded);
        vm.Categories.Single(c => c.IsAll).Checked.Should().Be(3);
    }

    [Fact]
    public void One_tree_can_be_hidden_and_shown_again()
    {
        using var tmp = new TempDir();
        var vm = new OrganizeViewModel(tmp.Path, new InlineDispatcher(), journalFolder: tmp["jobs"]);
        vm.ShowBothTrees.Should().BeTrue();
        vm.HideBeforeTreeCommand.Execute(null);
        vm.ShowBeforeTree.Should().BeFalse();
        vm.ShowAfterTree.Should().BeTrue("둘 다 숨기지는 않는다");
        vm.HideAfterTreeCommand.Execute(null);
        vm.ShowBeforeTree.Should().BeTrue();
        vm.ShowAfterTree.Should().BeFalse();
        vm.ShowTreesAgainCommand.Execute(null);
        vm.ShowBothTrees.Should().BeTrue();
    }

    [Fact]
    public void Long_paths_keep_the_first_folder_and_the_end()
    {
        OrganizeViewModel.ShortPath("a\\b.txt").Should().Be("a\\b.txt", "짧으면 그대로");
        var longPath = @"00.CodeReview\SRC_CLIENT\03.BIZ\BIZ_MCOMVAR\Source\Forms\Common\frMDA600U1.pas";
        OrganizeViewModel.ShortPath(longPath).Should().Be(@"00.CodeReview\…\Common\frMDA600U1.pas");
    }
}
