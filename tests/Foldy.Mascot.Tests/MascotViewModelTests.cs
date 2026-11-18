using Foldy.Presentation.Jobs;
using Foldy.Presentation.Mascot;

namespace Foldy.Mascot.Tests;

public class MascotViewModelTests
{
    private static MascotInput Running(JobKind kind, double seconds = 10) => new()
    {
        Kind = kind,
        State = JobState.Running,
        Elapsed = TimeSpan.FromSeconds(seconds),
        Progress = 0.5,
    };

    [Theory]
    [InlineData(0)]
    [InlineData(4.9)]
    public void Short_jobs_are_hidden(double seconds) =>
        MascotViewModel.Evaluate(Running(JobKind.Copy, seconds)).Visible.Should().BeFalse();

    [Fact]
    public void Visible_from_five_seconds() =>
        MascotViewModel.Evaluate(Running(JobKind.Copy, 5)).Visible.Should().BeTrue();

    [Theory]
    [InlineData(JobState.Queued)]
    [InlineData(JobState.Canceled)]
    public void Queued_and_canceled_are_hidden(JobState state) =>
        MascotViewModel.Evaluate(Running(JobKind.Copy) with { State = state }).Visible.Should().BeFalse();

    [Fact]
    public void User_setting_off_hides() =>
        MascotViewModel.Evaluate(Running(JobKind.Copy), new MascotOptions { Enabled = false }).Visible.Should().BeFalse();

    [Fact]
    public void Os_reduced_motion_hides() =>
        MascotViewModel.Evaluate(Running(JobKind.Copy), new MascotOptions { AnimationsAllowed = false }).Visible.Should().BeFalse();

    [Theory]
    [InlineData(JobKind.Copy)]
    [InlineData(JobKind.Move)]
    [InlineData(JobKind.MoveSameDrive)]
    public void Transfer_progress_maps_pass_then_dribble_then_shot(JobKind kind)
    {
        MascotScene Scene(double progress) => MascotViewModel.Evaluate(Running(kind) with { Progress = progress }).Scene;

        Scene(0.0).Should().Be(MascotScene.Pass);
        Scene(0.14).Should().Be(MascotScene.Pass);
        Scene(0.15).Should().Be(MascotScene.Dribble);
        Scene(0.5).Should().Be(MascotScene.Dribble);
        Scene(0.91).Should().Be(MascotScene.Dribble);
        Scene(0.92).Should().Be(MascotScene.Shoot);
        Scene(1.0).Should().Be(MascotScene.Shoot);
        Scene(-1).Should().Be(MascotScene.Dribble); // 진행률을 모르면 드리블
    }

    [Fact]
    public void Explicit_phase_wins_over_progress()
    {
        MascotScene Scene(MascotPhase phase) => MascotViewModel.Evaluate(Running(JobKind.Move) with { Progress = 0.5, Phase = phase }).Scene;

        Scene(MascotPhase.Pack).Should().Be(MascotScene.Pass);
        Scene(MascotPhase.Carry).Should().Be(MascotScene.Dribble);
        Scene(MascotPhase.Confirm).Should().Be(MascotScene.Shoot);
    }

    [Fact]
    public void Dribble_tempo_follows_throughput()
    {
        double Speed(double mbps) => MascotViewModel.Evaluate(Running(JobKind.Copy) with { BytesPerSecond = mbps * 1024 * 1024 }).Speed;

        Speed(0).Should().Be(MascotViewModel.MinSpeed);
        Speed(1).Should().BeGreaterThan(Speed(0));
        Speed(100).Should().BeGreaterThan(Speed(10));
        Speed(100_000).Should().Be(MascotViewModel.MaxSpeed);
        Speed(10).Should().BeInRange(1.2, 1.45);
    }

    [Fact]
    public void Delete_tosses_ball_hit_or_miss_and_permanent_goes_out_of_bounds()
    {
        var hit = MascotViewModel.Evaluate(Running(JobKind.Delete));
        hit.Scene.Should().Be(MascotScene.TossBin);
        hit.Outcome.Should().Be(TossOutcome.Hit);
        MascotViewModel.Evaluate(Running(JobKind.Delete) with { ItemFailed = true }).Outcome.Should().Be(TossOutcome.Miss);
        MascotViewModel.Evaluate(Running(JobKind.DeletePermanent)).Scene.Should().Be(MascotScene.OutOfBounds);
    }

    [Theory]
    [InlineData(JobKind.Search, MascotScene.Search)]
    [InlineData(JobKind.Analyze, MascotScene.Practice)]
    [InlineData(JobKind.Index, MascotScene.Practice)]
    [InlineData(JobKind.GitPush, MascotScene.Shoot)]
    [InlineData(JobKind.GitPull, MascotScene.Pass)]
    public void Other_kinds(JobKind kind, MascotScene scene) =>
        MascotViewModel.Evaluate(Running(kind)).Scene.Should().Be(scene);

    [Fact]
    public void Network_wait_searches_then_asks_and_offers_cancel()
    {
        var early = MascotViewModel.Evaluate(Running(JobKind.NetworkWait, 6));
        early.Scene.Should().Be(MascotScene.Search);
        early.Bubble.Should().Be(MascotBubble.None);
        early.ShowCancelHint.Should().BeFalse();

        var late = MascotViewModel.Evaluate(Running(JobKind.NetworkWait, 16));
        late.Scene.Should().Be(MascotScene.Search);
        late.Bubble.Should().Be(MascotBubble.Question);
        late.ShowCancelHint.Should().BeTrue();
    }

    [Fact]
    public void Every_kind_has_a_visible_scene() =>
        Enum.GetValues<JobKind>().Should().OnlyContain(k => MascotViewModel.Evaluate(Running(k)).Visible);

    [Fact]
    public void Paused_holds_the_ball_and_conflict_asks()
    {
        var paused = MascotViewModel.Evaluate(Running(JobKind.Copy) with { State = JobState.Paused });
        paused.Scene.Should().Be(MascotScene.Hold);
        paused.Bubble.Should().Be(MascotBubble.None);

        var needs = MascotViewModel.Evaluate(Running(JobKind.Copy) with { State = JobState.NeedsAttention });
        needs.Scene.Should().Be(MascotScene.Hold);
        needs.Bubble.Should().Be(MascotBubble.Question);

        MascotViewModel.Evaluate(Running(JobKind.Copy) with { HasConflict = true }).Bubble.Should().Be(MascotBubble.Question);
    }

    [Fact]
    public void Network_loss_makes_them_look_around() =>
        MascotViewModel.Evaluate(Running(JobKind.Copy) with { NetworkLost = true }).Scene.Should().Be(MascotScene.Search);

    [Fact]
    public void Copy_done_sinks_a_three_pointer_then_hides_after_two_seconds()
    {
        var done = Running(JobKind.Copy, 30) with { State = JobState.Done };
        MascotViewModel.Evaluate(done with { SinceFinished = TimeSpan.FromSeconds(0.5) }).Scene.Should().Be(MascotScene.Score);
        MascotViewModel.Evaluate(done with { SinceFinished = TimeSpan.FromSeconds(1.99) }).Visible.Should().BeTrue();
        MascotViewModel.Evaluate(done with { SinceFinished = TimeSpan.FromSeconds(2) }).Visible.Should().BeFalse();
    }

    [Theory]
    [InlineData(JobKind.Delete)]
    [InlineData(JobKind.Analyze)]
    [InlineData(JobKind.GitPush)]
    [InlineData(JobKind.Search)]
    public void Other_done_is_a_high_five(JobKind kind) =>
        MascotViewModel.Evaluate(Running(kind, 30) with { State = JobState.Done, SinceFinished = TimeSpan.Zero }).Scene.Should().Be(MascotScene.HighFive);

    [Fact]
    public void Done_quick_job_never_shows() =>
        MascotViewModel.Evaluate(Running(JobKind.Copy, 3) with { State = JobState.Done, SinceFinished = TimeSpan.Zero }).Visible.Should().BeFalse();

    [Fact]
    public void Failed_misses_the_shot_then_hides()
    {
        var failed = Running(JobKind.Copy, 30) with { State = JobState.Failed };
        var f = MascotViewModel.Evaluate(failed with { SinceFinished = TimeSpan.FromSeconds(1) });
        f.Scene.Should().Be(MascotScene.Miss);
        f.Bubble.Should().Be(MascotBubble.Exclaim);
        MascotViewModel.Evaluate(failed with { SinceFinished = TimeSpan.FromSeconds(4) }).Visible.Should().BeFalse();
    }

    [Fact]
    public void Clock_tracks_elapsed_and_freezes_on_finish()
    {
        var time = new FakeTime();
        var clock = new MascotClock(time);
        clock.Update(JobState.Queued);
        time.Advance(TimeSpan.FromSeconds(3));
        clock.Elapsed.Should().Be(TimeSpan.Zero);

        clock.Update(JobState.Running);
        time.Advance(TimeSpan.FromSeconds(6));
        clock.Elapsed.Should().Be(TimeSpan.FromSeconds(6));
        clock.SinceFinished.Should().BeNull();

        clock.Update(JobState.Done);
        time.Advance(TimeSpan.FromSeconds(1.5));
        clock.Elapsed.Should().Be(TimeSpan.FromSeconds(6));
        clock.SinceFinished.Should().Be(TimeSpan.FromSeconds(1.5));

        var input = new MascotInput { Kind = JobKind.Copy, State = JobState.Done, Elapsed = clock.Elapsed, SinceFinished = clock.SinceFinished };
        MascotViewModel.Evaluate(input).Scene.Should().Be(MascotScene.Score);
        time.Advance(TimeSpan.FromSeconds(0.6));
        MascotViewModel.Evaluate(input with { SinceFinished = clock.SinceFinished }).Visible.Should().BeFalse();
    }

    [Fact]
    public void Catalog_parses_and_rejects_bad_loop()
    {
        var c = MascotSceneCatalog.Parse("""
            { // 주석 허용
              "render": { "retroFps": 8 },
              "scenes": [ { "id": "Pass", "loopSeconds": 3.5 }, { "id": "Score", "loopSeconds": 1, "loop": false } ]
            }
            """);
        c.Render.RetroFps.Should().Be(8);
        c.Render.ModernFps.Should().Be(30);
        c.Get(MascotScene.Pass).LoopSeconds.Should().Be(3.5);
        c.Get(MascotScene.Score).Loop.Should().BeFalse();
        c.Get(MascotScene.Hold).LoopSeconds.Should().Be(2.0); // 없는 장면은 기본값

        var bad = () => MascotSceneCatalog.Parse("""{ "scenes": [ { "id": "Pass", "loopSeconds": 0 } ] }""");
        bad.Should().Throw<FormatException>();
    }
}
