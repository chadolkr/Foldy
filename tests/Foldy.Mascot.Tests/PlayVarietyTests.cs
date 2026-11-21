using Foldy.Presentation.Mascot;

namespace Foldy.Mascot.Tests;

/// <summary>농구 동작이 덜 반복되게 (사용자 2026-11-21: "같은 동작이 반복돼서 지루하다").</summary>
public class PlayVarietyTests
{
    [Fact]
    public void Library_has_well_known_moves()
    {
        var all = PlayPlan.Library(PlaySet.All);
        Assert.Contains(PlayKind.EuroStep, all);
        Assert.Contains(PlayKind.Hesitation, all);
        Assert.Contains(PlayKind.StepBack, all);
        Assert.Contains(PlayKind.NoLookPass, all);
        Assert.True(all.Count >= 15);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(123)]
    public void Every_move_shows_up_before_any_repeats(int seed)
    {
        var plan = PlayPlan.Create(seed, PlaySet.All);
        var kinds = PlayPlan.Library(PlaySet.All).Count;
        var first = plan.Segments.Take(kinds - 1).Select(s => s.Play).ToList(); // 마지막 한 칸은 처음과 이어지도록 고르는 제약이 있다
        Assert.Equal(first.Count, first.Distinct().Count());
        Assert.True(plan.Segments.Select(s => s.Play).Distinct().Count() >= kinds - 1);
    }
}
