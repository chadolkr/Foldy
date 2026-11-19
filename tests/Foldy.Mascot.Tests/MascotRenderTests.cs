using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Foldy.Presentation.Jobs;
using Foldy.Presentation.Mascot;
using Foldy.UI.Mascot;

namespace Foldy.Mascot.Tests;

public class MascotRenderTests
{
    private const double Width = 300;

    private static MascotFrame F(
        MascotScene scene,
        MascotBubble bubble = MascotBubble.None,
        TossOutcome outcome = TossOutcome.None,
        double progress = -1,
        MascotPhase phase = MascotPhase.None,
        MascotFinish finish = MascotFinish.None,
        int seed = 4) =>
        new(scene, 1, bubble, outcome, progress, phase, false, seed, finish);

    /// <summary>확인용 PNG에 쓰는 장면 목록 (이름, 프레임).</summary>
    private static (string Name, MascotFrame Frame)[] Showcase() =>
    [
        ("copy-progress-000", F(MascotScene.Dribble, progress: 0.0, finish: MascotFinish.ThreePointer)),
        ("copy-progress-050", F(MascotScene.Dribble, progress: 0.5, finish: MascotFinish.ThreePointer)),
        ("copy-progress-100-setup-outside-arc", F(MascotScene.Setup, progress: 1.0, finish: MascotFinish.ThreePointer)),
        ("copy-unknown-progress", F(MascotScene.Dribble, finish: MascotFinish.ThreePointer)),
        ("done-copy-3pointer", F(MascotScene.Score, finish: MascotFinish.ThreePointer, progress: 1)),
        ("done-move-dunk", F(MascotScene.Dunk, finish: MascotFinish.Dunk, progress: 1)),
        ("move-progress-060", F(MascotScene.Dribble, progress: 0.6, finish: MascotFinish.Dunk, seed: 9)),
        ("delete-progress-050", F(MascotScene.Dribble, progress: 0.5, finish: MascotFinish.BinToss, seed: 6)),
        ("done-delete-bin-hit", F(MascotScene.TossBin, outcome: TossOutcome.Hit, finish: MascotFinish.BinToss, progress: 1)),
        ("done-delete-bin-miss", F(MascotScene.TossBin, outcome: TossOutcome.Miss, finish: MascotFinish.BinToss, progress: 1)),
        ("done-permanent-delete-punt", F(MascotScene.OutOfBounds, finish: MascotFinish.Punt, progress: 1)),
        ("permanent-delete-progress-070", F(MascotScene.Dribble, progress: 0.7, finish: MascotFinish.Punt, seed: 3)),
        ("done-high-five", F(MascotScene.HighFive)),
        ("git-pull-pass", F(MascotScene.Pass)),
        ("git-push-shoot", F(MascotScene.Shoot)),
        ("search-network-wait", F(MascotScene.Search)),
        ("search-lost", F(MascotScene.Search, bubble: MascotBubble.Question)),
        ("index-practice", F(MascotScene.Practice)),
        ("state-paused", F(MascotScene.Hold)),
        ("state-conflict", F(MascotScene.Hold, bubble: MascotBubble.Question)),
        ("state-error-miss", F(MascotScene.Miss, bubble: MascotBubble.Exclaim, finish: MascotFinish.ThreePointer)),
    ];

    private static bool IsPlanScene(MascotScene s) => s is MascotScene.Dribble or MascotScene.Setup or MascotScene.Pass or MascotScene.Search;

    /// <summary>시각 확인용: 동작 순서 장면은 여러 동작이 보이도록 시간을 넓게 잡는다.</summary>
    private static double SampleSeconds(MascotFrame frame, double u, double loopSeconds) => IsPlanScene(frame.Scene) ? u * 16 : u * loopSeconds;

    private static byte[] Pixels(BitmapSource bmp)
    {
        var src = bmp.Format == PixelFormats.Pbgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0);
        var stride = src.PixelWidth * 4;
        var px = new byte[stride * src.PixelHeight];
        src.CopyPixels(px, stride, 0);
        return px;
    }

    private static int Opaque(byte[] bgra)
    {
        var n = 0;
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] > 0)
            {
                n++;
            }
        }

        return n;
    }

    [Fact]
    public void Every_visible_scene_has_a_definition()
    {
        var cat = MascotView.SceneCatalog;
        foreach (var scene in Enum.GetValues<MascotScene>().Where(s => s != MascotScene.Hidden))
        {
            cat.Scenes.Should().Contain(d => d.Id == scene.ToString(), "scenes.json에 {0} 정의가 있어야 한다", scene);
            cat.Get(scene).LoopSeconds.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void Every_scene_draws_in_both_modes_and_at_several_phases() => Sta.Run(() =>
    {
        var vector = new MascotPainter(MascotPalette.Default);
        var retro = new MascotPainter(MascotPalette.Default, minStroke: 3);
        foreach (var (name, frame) in Showcase())
        {
            foreach (var u in new[] { 0.0, 0.2, 0.5, 0.8, 1.0 })
            {
                Opaque(Pixels(MascotBitmap.RenderVector(vector, Width, frame, u, u * 3))).Should().BeGreaterThan(200, "{0} u={1} 벡터", name, u);
                Opaque(Pixels(MascotBitmap.RenderRetro(retro, Width, frame, u, u * 3))).Should().BeGreaterThan(20, "{0} u={1} 레트로", name, u);
            }
        }
    });

    [Fact]
    public void Both_names_are_labelled_in_every_scene() => Sta.Run(() =>
    {
        // 이름표가 그려지는지: 이름표 영역(땅 아래 띠)에 글자 픽셀이 있어야 한다
        var painter = new MascotPainter(MascotPalette.Default);
        foreach (var (name, frame) in Showcase())
        {
            var bmp = MascotBitmap.RenderVector(painter, Width, frame, 0.5, 1.5);
            var px = Pixels(bmp);
            var stride = bmp.PixelWidth * 4;
            var strip = 0;
            for (var y = (int)MascotPainter.Ground + 4; y < bmp.PixelHeight; y++)
            {
                for (var x = 0; x < bmp.PixelWidth; x++)
                {
                    if (px[(y * stride) + (x * 4) + 3] > 100)
                    {
                        strip++;
                    }
                }
            }

            strip.Should().BeGreaterThan(40, "{0}: 연주/우진 이름표", name);
        }
    });

    [Fact]
    public void Painted_play_center_follows_progress_left_to_right() => Sta.Run(() =>
    {
        const double width = 600;
        var painter = new MascotPainter(MascotPalette.Default);
        var means = new List<double>();
        for (var p = 0.0; p <= 1.0001; p += 0.1)
        {
            var scene = p >= MascotViewModel.SetupFrom ? MascotScene.Setup : MascotScene.Dribble;
            var frame = F(scene, progress: p, finish: MascotFinish.ThreePointer, seed: 21);
            double sum = 0;
            var n = 0;
            for (var t = 0.0; t < 20; t += 0.5)
            {
                _ = MascotBitmap.RenderVector(painter, width, frame, 0.3, t);
                sum += painter.Labels.Sum(l => l.X);
                n += painter.Labels.Count;
            }

            means.Add(sum / n);
        }

        for (var i = 1; i < means.Count; i++)
        {
            means[i].Should().BeGreaterThan(means[i - 1] - 8, "progress {0}", i / 10.0); // 국소 왕복 오차 허용
        }

        // 0%는 맨 왼쪽 근처, 100%는 3점 라인 바깥이면서 골대에서 멀다 (띠 폭의 약 1/3)
        means[0].Should().BeLessThan(110);
        means[^1].Should().BeGreaterThan(means[0] + 200);
        means[^1].Should().BeLessThan(MascotCourt.ArcX(width));
    });

    [Fact]
    public void Painted_plays_stay_outside_the_arc_during_progress() => Sta.Run(() =>
    {
        const double width = 600;
        var painter = new MascotPainter(MascotPalette.Default);
        var frame = F(MascotScene.Setup, progress: 1, finish: MascotFinish.ThreePointer, seed: 8);
        for (var t = 0.0; t < 30; t += 0.3)
        {
            _ = MascotBitmap.RenderVector(painter, width, frame, 0.3, t);
            painter.Labels.Should().OnlyContain(l => l.X < MascotCourt.ArcX(width));
        }
    });

    [Fact]
    public void Final_shooter_stands_outside_the_arc_and_far_from_the_hoop() => Sta.Run(() =>
    {
        const double width = 600;
        var painter = new MascotPainter(MascotPalette.Default);
        // 슛이 나가는 순간(u=0.42)의 우진 위치
        var frame = F(MascotScene.Score, finish: MascotFinish.ThreePointer, progress: 1);
        _ = MascotBitmap.RenderVector(painter, width, frame, 0.42, 0.7);
        var woojin = painter.Labels.Single(l => l.Name == "우진").X;
        woojin.Should().BeLessThan(MascotCourt.ArcX(width));
        (MascotCourt.RimX(width) - woojin).Should().BeGreaterThan(width / 3 - 5);
    });

    [Fact]
    public void Name_labels_never_overlap_in_any_scene() => Sta.Run(() =>
    {
        var painter = new MascotPainter(MascotPalette.Default);
        foreach (var (name, frame) in Showcase())
        {
            for (var t = 0.0; t < 40; t += 0.17)
            {
                foreach (var u in new[] { 0.1, 0.45, 0.8 })
                {
                    _ = MascotBitmap.RenderVector(painter, 600, frame, u, t);
                    var l = painter.ResolvedLabels().OrderBy(x => x.X).ToList();
                    for (var i = 1; i < l.Count; i++)
                    {
                        (l[i].X - l[i - 1].X).Should().BeGreaterThanOrEqualTo(((l[i].Width + l[i - 1].Width) / 2) + 2.5, "{0} t={1}", name, t);
                    }
                }
            }
        }
    });

    [Fact]
    public void Hidden_frame_draws_nothing() => Sta.Run(() =>
    {
        var bmp = MascotBitmap.RenderVector(new MascotPainter(MascotPalette.Default), Width, MascotFrame.Hidden, 0.5, 1);
        Opaque(Pixels(bmp)).Should().Be(0);
    });

    [Fact]
    public void Rendering_is_deterministic() => Sta.Run(() =>
    {
        var painter = new MascotPainter(MascotPalette.Default);
        var frame = F(MascotScene.Dribble, progress: 0.4);
        var a = Pixels(MascotBitmap.RenderVector(painter, Width, frame, 0.37, 1.7));
        var b = Pixels(MascotBitmap.RenderVector(painter, Width, frame, 0.37, 1.7));
        a.Should().Equal(b);
        Pixels(MascotBitmap.RenderVector(painter, Width, F(MascotScene.Dribble, progress: 0.8), 0.37, 1.7)).Should().NotEqual(a);
    });

    [Fact]
    public void Retro_is_low_resolution_with_hard_alpha_and_few_colors() => Sta.Run(() =>
    {
        var painter = new MascotPainter(MascotPalette.Default, minStroke: 3);
        var bmp = MascotBitmap.RenderRetro(painter, Width, F(MascotScene.Pass), 0.3, 1);
        bmp.PixelWidth.Should().Be((int)Math.Ceiling(Width / 3));
        bmp.PixelHeight.Should().Be(22);
        var px = Pixels(bmp);
        var colors = new HashSet<int>();
        for (var i = 0; i < px.Length; i += 4)
        {
            (px[i + 3] is 0 or 255).Should().BeTrue();
            if (px[i + 3] == 255)
            {
                colors.Add(px[i] | (px[i + 1] << 8) | (px[i + 2] << 16));
                (px[i] % 51).Should().Be(0);
            }
        }

        colors.Count.Should().BeInRange(2, 60);
    });

    [Fact]
    public void Narrow_and_wide_widths_do_not_throw() => Sta.Run(() =>
    {
        var painter = new MascotPainter(MascotPalette.Default);
        foreach (var (_, frame) in Showcase())
        {
            _ = MascotBitmap.RenderVector(painter, 120, frame, 0.5, 1);
            _ = MascotBitmap.RenderVector(painter, 900, frame, 0.5, 1);
        }
    });

    [Fact]
    public void View_takes_no_space_until_the_job_is_long_enough() => Sta.Run(() =>
    {
        var view = new MascotView { Kind = JobKind.Copy, State = JobState.Running, Progress = 0.3 };
        view.Measure(new Size(400, 100));
        view.DesiredSize.Height.Should().Be(0);
        view.CurrentFrame.Visible.Should().BeFalse();

        var off = new MascotView { IsMascotEnabled = false, State = JobState.Running };
        off.Measure(new Size(400, 100));
        off.DesiredSize.Height.Should().Be(0);
    });

    [Fact]
    public void View_snapshot_renders_both_styles() => Sta.Run(() =>
    {
        var view = new MascotView();
        var frame = F(MascotScene.TossBin, outcome: TossOutcome.Hit);
        Opaque(Pixels(view.Snapshot(frame, 0.5, 240, retro: false))).Should().BeGreaterThan(200);
        Opaque(Pixels(view.Snapshot(frame, 0.5, 240, retro: true))).Should().BeGreaterThan(20);
    });

    /// <summary>시각 확인용: 마무리 슛(복사 3점, 이동 덩크, 삭제 휴지통, 영구 삭제 던지기)의 공 궤적을 겹쳐 그려 포물선이 띠 안에 다 들어오는지 본다.</summary>
    [Fact]
    public void Export_ball_trajectories_when_requested() => Sta.Run(() =>
    {
        var dir = Environment.GetEnvironmentVariable("FOLDY_MASCOT_PNG_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var painter = new MascotPainter(MascotPalette.Default);
        const double scale = 2;
        const double w = 600;
        var cases = new (string Name, MascotFrame Frame)[]
        {
            ("trajectory-copy-3pointer", F(MascotScene.Score, finish: MascotFinish.ThreePointer, progress: 1)),
            ("trajectory-move-dunk", F(MascotScene.Dunk, finish: MascotFinish.Dunk, progress: 1)),
            ("trajectory-delete-bin", F(MascotScene.TossBin, outcome: TossOutcome.Hit, finish: MascotFinish.BinToss, progress: 1)),
            ("trajectory-permanent-punt", F(MascotScene.OutOfBounds, finish: MascotFinish.Punt, progress: 1)),
        };
        var bg = new SolidColorBrush(Colors.White);
        var dv = new DrawingVisual();
        var dg = new DrawingGroup();
        using (var dc = dg.Open())
        {
            for (var k = 0; k < cases.Length; k++)
            {
                var y = k * ((MascotPainter.DesignHeight * scale) + 6);
                dc.DrawRectangle(bg, null, new Rect(0, y, w * scale, MascotPainter.DesignHeight * scale));
                var def = MascotView.SceneCatalog.Get(cases[k].Frame.Scene);
                for (var i = 0; i <= 24; i++)
                {
                    var u = 0.38 + (i * 0.012);
                    dc.PushOpacity(i == 24 ? 1 : 0.3);
                    dc.DrawImage(MascotBitmap.RenderVector(painter, w, cases[k].Frame, u, u * def.LoopSeconds, scale), new Rect(0, y, w * scale, MascotPainter.DesignHeight * scale));
                    dc.Pop();
                }
            }
        }

        using (var dcv = dv.RenderOpen())
        {
            dcv.DrawDrawing(dg);
        }

        var rtb = new RenderTargetBitmap((int)(w * scale), (int)(cases.Length * ((MascotPainter.DesignHeight * scale) + 6)), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(dir, "ball-trajectories.png"));
        enc.Save(fs);
    });

    /// <summary>
    /// 시각 확인용 하네스. FOLDY_MASCOT_PNG_DIR 환경변수가 있으면 장면마다 PNG 한 장(밝은 모던 / 어두운 모던 / 레트로 × 시간 순 프레임)을 저장한다.
    /// </summary>
    [Fact]
    public void Export_scene_sheets_when_requested() => Sta.Run(() =>
    {
        var dir = Environment.GetEnvironmentVariable("FOLDY_MASCOT_PNG_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var dark = new MascotPalette
        {
            Text = Color.FromRgb(0xF2, 0xF2, 0xF4),
            TextSecondary = Color.FromRgb(0xA8, 0xA8, 0xB0),
            Surface = Color.FromRgb(0x2C, 0x2C, 0x30),
            Border = Color.FromRgb(0x55, 0x55, 0x5C),
            MetalDark = Color.FromRgb(0x9A, 0xA0, 0xAC),
        };
        var retroPal = new MascotPalette { Surface = Colors.White, Border = Color.FromRgb(0x80, 0x80, 0x80), Text = Colors.Black, TextSecondary = Colors.Black };
        var us = new[] { 0.08, 0.3, 0.52, 0.75, 0.95 };
        const double scale = 1.5;
        const double sceneW = 600;
        var cellW = sceneW * scale;
        var cellH = MascotPainter.DesignHeight * scale;

        foreach (var (name, frame) in Showcase())
        {
            var def = MascotView.SceneCatalog.Get(frame.Scene);
            var dv = new DrawingVisual();
            var dg = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(dg, BitmapScalingMode.NearestNeighbor);
            using (var dc = dg.Open())
            {
                var cols = new (MascotPalette Pal, Color Bg, bool Retro)[]
                {
                    (MascotPalette.Default, Colors.White, false),
                    (dark, Color.FromRgb(0x1E, 0x1E, 0x21), false),
                    (retroPal, Color.FromRgb(0xC0, 0xC0, 0xC0), true),
                };
                for (var c = 0; c < cols.Length; c++)
                {
                    var painter = new MascotPainter(cols[c].Pal, cols[c].Retro ? 3 : 0);
                    for (var r = 0; r < us.Length; r++)
                    {
                        var rect = new Rect((c * (cellW + 8)) + 4, (r * (cellH + 6)) + 4, cellW, cellH);
                        dc.DrawRectangle(new SolidColorBrush(cols[c].Bg), null, rect);
                        var t = SampleSeconds(frame, us[r], def.LoopSeconds);
                        if (cols[c].Retro)
                        {
                            dc.DrawImage(MascotBitmap.RenderRetro(painter, sceneW, frame, us[r], t), rect);
                            dc.PushTransform(new TranslateTransform(rect.X, rect.Y));
                            dc.PushTransform(new ScaleTransform(scale, scale));
                            painter.PaintLabels(dc);
                            dc.Pop();
                            dc.Pop();
                        }
                        else
                        {
                            dc.DrawImage(MascotBitmap.RenderVector(painter, sceneW, frame, us[r], t, scale), rect);
                        }
                    }
                }
            }

            using (var dcv = dv.RenderOpen())
            {
                dcv.DrawDrawing(dg);
            }

            var totalW = (int)((3 * (cellW + 8)) + 4);
            var totalH = (int)((us.Length * (cellH + 6)) + 4);
            var rtb = new RenderTargetBitmap(totalW, totalH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, $"{name}.png"));
            enc.Save(fs);
        }
    });
}
