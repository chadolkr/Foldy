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

    private static MascotInput Done(JobKind kind) =>
        Running(kind, 30) with { State = JobState.Done, SinceFinished = TimeSpan.FromSeconds(0.5), Progress = 1 };

    [Theory]
    [InlineData(0)]
    [InlineData(1.4)]
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
    [InlineData(JobKind.Delete)]
    [InlineData(JobKind.DeletePermanent)]
    public void Progress_never_forces_a_shot_it_only_switches_to_setup_near_the_end(JobKind kind)
    {
        MascotScene Scene(double progress) => MascotViewModel.Evaluate(Running(kind) with { Progress = progress }).Scene;

        foreach (var p in new[] { 0.0, 0.1, 0.5, 0.91 })
        {
            Scene(p).Should().Be(MascotScene.Dribble);
        }

        Scene(0.92).Should().Be(MascotScene.Setup);
        Scene(1.0).Should().Be(MascotScene.Setup);
        Scene(-1).Should().Be(MascotScene.Dribble);
        // 어떤 진행률에서도 슛·덩크·던지기 마무리 장면은 나오지 않는다
        var finishScenes = new[] { MascotScene.Shoot, MascotScene.Score, MascotScene.Dunk, MascotScene.TossBin, MascotScene.OutOfBounds };
        for (var p = 0.0; p <= 1.0; p += 0.05)
        {
            finishScenes.Should().NotContain(Scene(p));
        }
    }

    [Fact]
    public void Explicit_phase_wins_over_progress()
    {
        MascotScene Scene(MascotPhase phase) => MascotViewModel.Evaluate(Running(JobKind.Move) with { Progress = 0.5, Phase = phase }).Scene;

        Scene(MascotPhase.Pack).Should().Be(MascotScene.Dribble);
        Scene(MascotPhase.Carry).Should().Be(MascotScene.Dribble);
        Scene(MascotPhase.Confirm).Should().Be(MascotScene.Setup);
    }

    [Fact]
    public void Frame_carries_seed_and_finish_kind()
    {
        var f = MascotViewModel.Evaluate(Running(JobKind.Move) with { Seed = 1234 });
        f.Seed.Should().Be(1234);
        f.Finish.Should().Be(MascotFinish.Dunk);
        MascotViewModel.Evaluate(Running(JobKind.Copy)).Finish.Should().Be(MascotFinish.ThreePointer);
        MascotViewModel.Evaluate(Running(JobKind.Delete)).Finish.Should().Be(MascotFinish.BinToss);
        MascotViewModel.Evaluate(Running(JobKind.DeletePermanent)).Finish.Should().Be(MascotFinish.Punt);
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
    public void Each_job_kind_ends_with_a_different_finish_scene()
    {
        var copy = MascotViewModel.Evaluate(Done(JobKind.Copy)).Scene;
        var move = MascotViewModel.Evaluate(Done(JobKind.Move)).Scene;
        var del = MascotViewModel.Evaluate(Done(JobKind.Delete)).Scene;
        var perm = MascotViewModel.Evaluate(Done(JobKind.DeletePermanent)).Scene;

        copy.Should().Be(MascotScene.Score);        // 3점 라인 밖 3점슛
        move.Should().Be(MascotScene.Dunk);         // 속공 앨리웁 덩크
        del.Should().Be(MascotScene.TossBin);       // 사이드라인 너머 휴지통
        perm.Should().Be(MascotScene.OutOfBounds);  // 경기장 밖으로 멀리
        new[] { copy, move, del, perm }.Distinct().Should().HaveCount(4);
        MascotViewModel.Evaluate(Done(JobKind.MoveSameDrive)).Scene.Should().Be(MascotScene.Dunk);
    }

    [Fact]
    public void Delete_finish_misses_when_an_item_failed()
    {
        MascotViewModel.Evaluate(Done(JobKind.Delete)).Outcome.Should().Be(TossOutcome.Hit);
        MascotViewModel.Evaluate(Done(JobKind.Delete) with { ItemFailed = true }).Outcome.Should().Be(TossOutcome.Miss);
        var failed = MascotViewModel.Evaluate(Running(JobKind.Delete, 30) with { State = JobState.Failed, SinceFinished = TimeSpan.Zero });
        failed.Scene.Should().Be(MascotScene.TossBin);
        failed.Outcome.Should().Be(TossOutcome.Miss);
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
    public void Done_finish_hides_after_two_seconds()
    {
        var done = Done(JobKind.Copy);
        MascotViewModel.Evaluate(done with { SinceFinished = TimeSpan.FromSeconds(1.99) }).Visible.Should().BeTrue();
        MascotViewModel.Evaluate(done with { SinceFinished = TimeSpan.FromSeconds(2) }).Visible.Should().BeFalse();
    }

    [Theory]
    [InlineData(JobKind.Analyze)]
    [InlineData(JobKind.GitPush)]
    [InlineData(JobKind.Search)]
    public void Other_done_is_a_high_five(JobKind kind) =>
        MascotViewModel.Evaluate(Done(kind)).Scene.Should().Be(MascotScene.HighFive);

    [Fact]
    public void Done_quick_job_never_shows() =>
        MascotViewModel.Evaluate(Running(JobKind.Copy, 1) with { State = JobState.Done, SinceFinished = TimeSpan.Zero }).Visible.Should().BeFalse();

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

public class MascotPlanTests
{
    [Theory]
    [InlineData(1, PlaySet.All)]
    [InlineData(7, PlaySet.All)]
    [InlineData(12345, PlaySet.All)]
    [InlineData(99, PlaySet.Setup)]
    [InlineData(5, PlaySet.Passing)]
    public void Consecutive_plays_always_differ_including_the_wraparound(int seed, PlaySet set)
    {
        var segs = PlayPlan.Create(seed, set).Segments;
        for (var i = 0; i < segs.Count; i++)
        {
            segs[i].Play.Should().NotBe(segs[(i + 1) % segs.Count].Play, "seed {0} index {1}", seed, i);
        }
    }

    [Fact]
    public void Plan_uses_many_different_plays_and_spans_most_of_the_width_in_both_directions()
    {
        foreach (var seed in new[] { 1, 2, 3, 42, 777 })
        {
            var plan = PlayPlan.Create(seed);
            plan.Segments.Select(s => s.Play).Distinct().Count().Should().BeGreaterThanOrEqualTo(8);
            var xs = plan.Segments.SelectMany(s => new[] { s.From, s.To }).ToList();
            (xs.Max() - xs.Min()).Should().BeGreaterThan(0.7);
            plan.Segments.Count(s => s.To > s.From).Should().BeGreaterThan(3);
            plan.Segments.Count(s => s.To < s.From).Should().BeGreaterThan(3);
        }
    }

    [Fact]
    public void Plan_is_deterministic_per_seed_and_differs_between_seeds()
    {
        var a = PlayPlan.Create(11).Segments.Select(s => s.Play).ToArray();
        var b = PlayPlan.Create(11).Segments.Select(s => s.Play).ToArray();
        var c = PlayPlan.Create(12).Segments.Select(s => s.Play).ToArray();
        a.Should().Equal(b);
        a.Should().NotEqual(c);
    }

    [Fact]
    public void Ball_ownership_is_continuous_and_returns_at_the_wrap()
    {
        var segs = PlayPlan.Create(31).Segments;
        for (var i = 0; i < segs.Count; i++)
        {
            segs[i].HolderEnd.Should().Be(segs[(i + 1) % segs.Count].Holder);
        }
    }

    [Fact]
    public void At_walks_through_all_segments_and_wraps()
    {
        var plan = PlayPlan.Create(3);
        plan.At(0).Index.Should().Be(0);
        plan.At(plan.TotalDuration - 0.01).Index.Should().Be(plan.Segments.Count - 1);
        plan.At(plan.TotalDuration + 0.01).Index.Should().Be(0);
        var seen = new HashSet<int>();
        for (var t = 0.0; t < plan.TotalDuration; t += 0.1)
        {
            seen.Add(plan.At(t).Index);
        }

        seen.Count.Should().Be(plan.Segments.Count);
    }

    [Fact]
    public void Play_center_increases_with_progress_and_ends_at_a_third_of_the_band_from_the_hoop()
    {
        foreach (var finish in new[] { MascotFinish.ThreePointer, MascotFinish.Dunk, MascotFinish.BinToss, MascotFinish.Punt })
        {
            var prev = double.MinValue;
            for (var p = 0.0; p <= 1.0001; p += 0.05)
            {
                var c = MascotCourt.PlayCenter(p, 600, finish);
                c.Should().BeGreaterThan(prev);
                prev = c;
            }

            MascotCourt.PlayCenter(0, 600, finish).Should().BeLessThan(60); // 0%는 맨 왼쪽
        }

        // 3점슛 위치: 골대에서 띠 폭의 약 1/3, 라인(ArcX) 바깥
        var shotX = MascotCourt.ShotX(600);
        (MascotCourt.RimX(600) - shotX).Should().BeApproximately(200, 1);
        MascotCourt.ArcX(600).Should().BeGreaterThan(shotX);
        MascotCourt.PlayCenter(1, 600, MascotFinish.ThreePointer).Should().BeLessThan(MascotCourt.ArcX(600));
        MascotCourt.PlayCenter(-1, 600).Should().BeInRange(100, 400);
    }
}
