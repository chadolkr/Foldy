using Foldy.Presentation.Navigation;

namespace Foldy.Presentation.Tests;

public class DropRulesTests
{
    [Fact]
    public void Same_folder_in_both_panes_is_already_there()
    {
        DropRules.AllAlreadyIn([@"K:\2026_SMC\보고서.pptx"], @"K:\2026_SMC", @"K:\2026_SMC").Should().BeTrue();
        DropRules.AllAlreadyIn([@"K:\2026_SMC\보고서.pptx"], @"k:\2026_smc\", @"K:\2026_SMC").Should().BeTrue();
    }

    [Fact]
    public void Files_from_explorer_in_the_same_folder_are_already_there()
    {
        DropRules.AllAlreadyIn([@"C:\a\x.txt", @"C:\a\y"], null, @"C:\a").Should().BeTrue();
    }

    [Fact]
    public void Another_folder_is_a_real_drop()
    {
        DropRules.AllAlreadyIn([@"K:\2026_SMC\보고서.pptx"], @"K:\2026_SMC", @"K:\2026_SMC\보관").Should().BeFalse();
        DropRules.AllAlreadyIn([@"C:\a\x.txt", @"C:\b\y.txt"], null, @"C:\a").Should().BeFalse();
        DropRules.AllAlreadyIn([@"K:\a\x.txt"], @"K:\a", @"C:\a").Should().BeFalse();
        DropRules.AllAlreadyIn([], null, @"C:\a").Should().BeFalse();
    }

    [Fact]
    public void A_folder_cannot_go_inside_itself()
    {
        DropRules.IntoItself([@"C:\work\프로젝트"], @"C:\work\프로젝트").Should().BeTrue();
        DropRules.IntoItself([@"C:\work\프로젝트"], @"c:\work\프로젝트\보관\2026").Should().BeTrue();
        DropRules.IntoItself([@"C:\work\a.txt", @"C:\work\프로젝트\"], @"C:\work\프로젝트\보관").Should().BeTrue();
    }

    [Fact]
    public void A_folder_with_a_similar_name_is_not_inside()
    {
        DropRules.IntoItself([@"C:\work\프로젝트"], @"C:\work\프로젝트2").Should().BeFalse();
        DropRules.IntoItself([@"C:\work\프로젝트"], @"C:\work").Should().BeFalse();
        DropRules.IntoItself([], @"C:\work").Should().BeFalse();
        DropRules.IntoItself(null, @"C:\work").Should().BeFalse();
    }
    [Fact]
    public void A_folder_under_the_cursor_is_the_drop_target()
    {
        // 같은 창 안에서 파일을 하위 폴더 위에 놓으면 그 폴더로 옮긴다
        var target = DropRules.TargetFor([@"C:\work\보고서.pptx"], @"C:\work", @"C:\work\보관");
        target.Should().Be(@"C:\work\보관");
        DropRules.AllAlreadyIn([@"C:\work\보고서.pptx"], @"C:\work", target).Should().BeFalse();
        DropRules.IntoItself([@"C:\work\보고서.pptx"], target).Should().BeFalse();
    }

    [Fact]
    public void A_folder_can_be_dropped_onto_a_sibling_folder()
    {
        var target = DropRules.TargetFor([@"C:\work\2025"], @"C:\work", @"C:\work\보관");
        target.Should().Be(@"C:\work\보관");
        DropRules.AllAlreadyIn([@"C:\work\2025"], @"C:\work", target).Should().BeFalse();
        DropRules.IntoItself([@"C:\work\2025"], target).Should().BeFalse();
    }

    [Fact]
    public void Empty_space_or_the_dragged_folder_itself_means_the_open_folder()
    {
        DropRules.TargetFor([@"C:\work\a.txt"], @"C:\work", null).Should().Be(@"C:\work");
        // 끌기를 막 시작하면 커서는 끄는 폴더 위에 있다: 그 폴더 자신은 놓을 곳이 아니다
        DropRules.TargetFor([@"C:\work\보관"], @"C:\work", @"c:\work\보관\").Should().Be(@"C:\work");
    }
}
