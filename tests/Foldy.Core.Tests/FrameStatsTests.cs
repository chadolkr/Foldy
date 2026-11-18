using Foldy.Core.Diagnostics;

namespace Foldy.Core.Tests;

public class FrameStatsTests
{
    [Fact]
    public void Smooth_60hz_passes()
    {
        var d = Enumerable.Repeat(16.7, 1000).ToList();
        var s = FrameStats.From(d);
        s.PassesBudget.Should().BeTrue();
        s.LateFrames.Should().Be(0);
    }

    [Fact]
    public void Ten_percent_dropped_frames_fails()
    {
        var d = Enumerable.Repeat(16.7, 900).Concat(Enumerable.Repeat(33.4, 100)).ToList();
        var s = FrameStats.From(d);
        s.LatePercent.Should().BeApproximately(10, 0.01);
        s.P95Ms.Should().Be(33.4);
        s.PassesBudget.Should().BeFalse();
    }

    [Fact]
    public void Empty_does_not_pass() => FrameStats.From([]).PassesBudget.Should().BeFalse();
}
