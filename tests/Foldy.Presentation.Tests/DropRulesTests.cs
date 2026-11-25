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
}
