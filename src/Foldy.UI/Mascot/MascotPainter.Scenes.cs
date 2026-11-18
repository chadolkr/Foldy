using System.Windows;
using System.Windows.Media;
using Foldy.Presentation.Mascot;

namespace Foldy.UI.Mascot;

// 장면별 그리기. 모든 장면은 위상 _u(0~1)와 누적 시간 _t만으로 결정되므로 같은 입력은 항상 같은 그림이다.
// 두 아이는 거의 항상 달린다: 달리기 위상은 _t * RunHz(초당 걸음 주기), 바닥 눈금이 흘러가 속도감을 준다.
public sealed partial class MascotPainter
{
    private const double RunHz = 3.6;

    private static readonly Point ChestHandF = new(6.8, -15.5);
    private static readonly Point ChestHandB = new(5.4, -17.5);

    private void DrawScene()
    {
        switch (_f.Scene)
        {
            case MascotScene.Pass: PassScene(); break;
            case MascotScene.Dribble: DribbleScene(); break;
            case MascotScene.Shoot: ShotScene(0); break;
            case MascotScene.Score: ShotScene(1); break;
            case MascotScene.Miss: ShotScene(2); break;
            case MascotScene.HighFive: HighFiveScene(); break;
            case MascotScene.TossBin: TossScene(); break;
            case MascotScene.OutOfBounds: OutScene(); break;
            case MascotScene.Search: SearchScene(); break;
            case MascotScene.Practice: PracticeScene(); break;
            case MascotScene.Hold: HoldScene(); break;
            default: break;
        }
    }

    /// <summary>facing 방향 가슴 높이에 공을 안은 위치.</summary>
    private static Point Chest(double x, double facing, double dip = 0) => new(x + (facing * 6.9), Ground - 12.9 + dip);

    /// <summary>바닥 눈금이 왼쪽으로 흘러 달리는 느낌을 준다.</summary>
    private void Scroll(double pxPerSecond)
    {
        const double gap = 24;
        var off = Frac(_t * pxPerSecond / gap) * gap;
        var c = Color.FromArgb(150, _p.TextSecondary.R, _p.TextSecondary.G, _p.TextSecondary.B);
        for (var x = -off; x < _w - 8; x += gap)
        {
            if (x > 6)
            {
                Line(c, 2.4, x, Ground + 1.2, Math.Min(x + 9, _w - 6), Ground + 1.2);
            }
        }
    }

    private static double Opacity(double t, double fadeIn = 0.06, double fadeOut = 0.1) =>
        Math.Clamp(Seg(t, 0, fadeIn) * (1 - Seg(t, 1 - fadeOut, 1)), 0.02, 1);

    /// <summary>드리블 중 손이 공을 따라가는 높이(몸 좌표).</summary>
    private static double HandFor(double ballY) => Math.Clamp((ballY - 4.4 - Ground) / KidScale, -21, -9.5);

    private double DribbleBallY() => Ground - 4.2 - (10 * Math.Sin(Frac(_t * 3.6) * Math.PI));

    /// <summary>크로스오버: 공이 좌우 손을 오간다 (몸 기준 앞쪽 거리 3.2 ± 4.2).</summary>
    private double CrossOffset() => 3.2 + (4.2 * Math.Cos(Math.PI * _t * 1.8));

    // ───────────────────────── 패스: 달리며 패스 주고받기 ─────────────────────────

    private void PassScene()
    {
        var c = _w / 2;
        Scroll(85);
        var sway = Math.Sin(_u * Math.PI * 2) * 9;
        var a = c - 28 + sway;
        var b = c + 28 - sway;
        var toB = _u < 0.5;
        var s = Frac(_u * 2);
        var fl = Seg(s, 0.1, 0.78);
        var bounce = !toB; // 첫 패스는 체스트(살짝 로브), 두 번째는 바운드 패스
        var from = toB ? new Point(a, 1) : new Point(b, 1);
        var to = toB ? new Point(b, 1) : new Point(a, 1);

        Point ball;
        var p0 = Chest(from.X, 1);
        var p1 = Chest(to.X, 1);
        if (s < 0.1)
        {
            ball = p0;
        }
        else if (s < 0.78)
        {
            var x = Lerp(p0.X, p1.X, fl);
            var yLine = Lerp(p0.Y, p1.Y, fl);
            if (bounce)
            {
                var floorY = Ground - 4.2;
                var dip = (floorY - Lerp(p0.Y, p1.Y, 0.5)) * (1 - Math.Abs((2 * fl) - 1));
                ball = new Point(x, yLine + dip);
            }
            else
            {
                ball = new Point(x, yLine - (Math.Sin(fl * Math.PI) * 9));
            }
        }
        else
        {
            ball = p1;
        }

        for (var k = 0; k < 2; k++)
        {
            var isA = k == 0;
            var thrower = isA == toB;
            Point? fh = null;
            Point? bh = null;
            if (thrower && s is >= 0.05 and < 0.35)
            {
                fh = new Point(10.5, -16);
                bh = new Point(9, -17.5);
            }
            else if (!thrower && s is >= 0.15 and < 0.78)
            {
                fh = new Point(11, -16);
                bh = new Point(9.5, -17);
            }
            else if (!thrower && s >= 0.78)
            {
                fh = ChestHandF;
                bh = ChestHandB;
            }

            Kid(
                isA ? Who.Yeonju : Who.Woojin,
                isA ? a : b,
                new Pose
                {
                    Run = true,
                    Walk = (_t * RunHz) + (isA ? 0 : 0.5),
                    FrontHand = fh,
                    BackHand = bh,
                    Eyes = EyeStyle.Happy,
                    Mouth = MouthStyle.Open,
                    LookX = 0.8,
                    Bubble = !isA,
                });
        }

        Basketball(ball.X, ball.Y, _t * 700);
    }

    // ───────────────────────── 드리블: 함께 달리며 공 주고받기 ─────────────────────────

    private void DribbleScene()
    {
        var p = _f.Progress;
        var basePos = p >= 0 ? Lerp(_w * 0.30, _w * 0.62, Smooth(Seg(p, MascotViewModel.PassUntil, MascotViewModel.ShootFrom))) : _w * 0.45;
        Scroll(78);

        // 둘 사이가 가장 가까워지는 순간(u=0.5, 1.0)에 짧게 패스해서 드리블 담당이 바뀐다. 겹치지 않게 최소 20px 간격.
        var gap = 18 + (8 * Math.Cos(_u * Math.PI * 2));
        var wx = basePos + gap;
        var yx = basePos - gap;
        var toWoojin = Math.Abs(_u - 0.5) < 0.05;
        var toYeonju = _u >= 0.95 || _u < 0.05;
        var passWin = toWoojin || toYeonju;
        var woojinHolds = _u is >= 0.55 and < 0.95;

        Point ball;
        double handLocalX = 8.2;
        double handY;
        var holderX = woojinHolds ? wx : yx;
        var otherX = woojinHolds ? yx : wx;
        if (passWin)
        {
            var s = toWoojin ? Seg(_u, 0.45, 0.55) : Seg(Frac(_u + 0.05), 0, 0.1);
            var fromX = toWoojin ? yx : wx;
            var toX = toWoojin ? wx : yx;
            ball = Arc(Chest(fromX, 1), Chest(toX, 1), s, 6);
            handY = -16;
        }
        else
        {
            var by = DribbleBallY();
            var off = CrossOffset();
            ball = new Point(holderX + off, by);
            handLocalX = off / KidScale;
            handY = HandFor(by);
        }

        for (var k = 0; k < 2; k++)
        {
            var isW = k == 1;
            var x = isW ? wx : yx;
            var holds = isW == woojinHolds;
            Kid(
                isW ? Who.Woojin : Who.Yeonju,
                x,
                new Pose
                {
                    Run = true,
                    Walk = (_t * RunHz) + (isW ? 0 : 0.5),
                    FrontHand = holds && !passWin ? new Point(handLocalX, handY) : (!holds && passWin ? new Point(11, -17) : null),
                    BackHand = holds && !passWin ? new Point(-5, -15 + (Math.Sin(_t * RunHz * Math.PI * 2) * 3)) : null,
                    Eyes = EyeStyle.Happy,
                    Mouth = MouthStyle.Open,
                    LookX = 0.8,
                    Bubble = isW,
                });
        }

        Basketball(ball.X, ball.Y, _t * 600);
    }

    // ───────────────────────── 슛 / 성공 / 실패: 달려 들어가는 점프슛 ─────────────────────────

    /// <summary>mode 0 = 레이업 반복(Git push, 복사 마무리), 1 = 3점슛 성공 + 하이파이브, 2 = 림에 맞고 튕김.</summary>
    private void ShotScene(int mode)
    {
        var layup = mode == 0;
        var xs = layup ? RimX - 36 : _w - 100;
        var x0 = Math.Max(16, xs - 150);
        var T = _u;
        var hit = mode != 2;
        const double aEnd = 0.28;
        const double rel = 0.42;
        var jumpH = layup ? 9 : 7;

        var running = T < aEnd;
        var run = Seg(T, 0, aEnd);
        var wx = running ? Lerp(x0, xs, run) : xs;
        var lift = Bump(T, 0.28, 0.52) * jumpH;
        Point Rel(double tt) => new(xs + 3, Ground - 33 - (Bump(tt, 0.28, 0.52) * jumpH));
        var target = hit ? new Point(RimX, RimY - 3.5) : new Point(RimX - 6.5, RimY - 3.2);

        Point ball;
        double handY = -12;
        if (T < aEnd)
        {
            var by = DribbleBallY();
            ball = new Point(wx + (CrossOffset()), by);
            handY = HandFor(by);
        }
        else if (T < rel)
        {
            var c = Chest(xs, 1, 0);
            var r0 = Rel(T);
            var k = Smooth(Seg(T, aEnd, rel));
            ball = new Point(Lerp(c.X, r0.X, k), Lerp(c.Y, r0.Y, k));
        }
        else if (T < 0.65)
        {
            ball = Arc(Rel(rel), target, Seg(T, rel, 0.65), layup ? 9 : 15);
        }
        else if (hit)
        {
            if (T < 0.74)
            {
                ball = new Point(RimX, Lerp(RimY - 3.5, RimY + 8.5, Seg(T, 0.65, 0.74)));
            }
            else
            {
                var d = Seg(T, 0.74, 0.88);
                ball = new Point(RimX - (Seg(T, 0.88, 1) * 10), Lerp(RimY + 8.5, Ground - 4.2, d * d) - (Bump(T, 0.88, 1.0) * 6));
            }
        }
        else
        {
            var s = Seg(T, 0.65, 0.88);
            ball = Arc(target, new Point(RimX - 30, Ground - 4.2), s, 14);
            if (T > 0.88)
            {
                ball = new Point(RimX - 30 - (Seg(T, 0.88, 1) * 8), Ground - 4.2 - (Bump(T, 0.88, 1.0) * 5));
            }
        }

        var wiggle = hit && T > 0.65 ? Math.Sin(T * 90) * 5 * (1 - Seg(T, 0.65, 0.92)) : 0;
        var fade = layup ? Opacity(T) : 1;

        HoopBack();
        _dc.PushOpacity(fade);
        Basketball(ball.X, ball.Y, _t * 650);
        HoopFront(wiggle);

        // 우진
        var wFace = 1.0;
        Point? wf;
        Point? wb = null;
        var eyes = EyeStyle.Happy;
        var mouth = MouthStyle.Open;
        if (T < aEnd)
        {
            wf = new Point(8.2, handY);
            wb = new Point(-5, -15);
        }
        else if (T < 0.52)
        {
            var up = Seg(T, 0.28, 0.42);
            wf = new Point(Lerp(7, 9, up), Lerp(-16, -36, up));
            wb = new Point(Lerp(5, 4.5, up), Lerp(-17, -32, up));
        }
        else if (T < 0.65)
        {
            wf = new Point(9, -36);
            wb = new Point(-5, -14);
        }
        else
        {
            eyes = hit ? EyeStyle.Happy : EyeStyle.Worried;
            mouth = hit ? MouthStyle.Open : MouthStyle.Frown;
            wf = hit ? new Point(9, -33) : new Point(-4, -34);
            wb = hit ? new Point(-8, -31) : new Point(4, -34);
        }

        // 연주: 뒤에서 따라붙어 달려 들어온다
        var yx = (running ? wx : xs) - 28 - (30 * (1 - Smooth(run)));
        var yEyes = T > 0.7 ? (hit ? EyeStyle.Happy : EyeStyle.Worried) : EyeStyle.Happy;
        var yMouth = T > 0.7 && !hit ? MouthStyle.Frown : MouthStyle.Open;
        Point? yf = null;
        Point? yb = null;
        var yLift = 0.0;
        var yRun = running;
        if (T > 0.7 && hit && mode == 0)
        {
            yf = new Point(8, -31);
            yb = new Point(-7, -30 - (Sin01(T * 8) * 3));
            yLift = Math.Abs(Math.Sin(T * 18)) * 1.8;
        }
        else if (T > 0.7 && !hit)
        {
            yf = new Point(4, -33);
            yb = new Point(-4, -33);
        }

        double? wTurn = null;
        if (mode == 1)
        {
            // 3점슛 성공: 연주가 달려와 하이파이브
            var walk = Smooth(Seg(T, 0.62, 0.8));
            yx = Lerp(xs - 28, xs - 22, walk);
            yRun = T is > 0.6 and < 0.8;
            var raise = Smooth(Seg(T, 0.78, 0.9));
            if (T > 0.7)
            {
                wTurn = -1;
                var h = Bump(T, 0.9, 1.0) * 2.5;
                wf = new Point(Lerp(6.5, 12.5, raise), Lerp(-14, -31, raise));
                wb = new Point(-6, -14);
                yf = new Point(Lerp(6.5, 12.5, raise), Lerp(-14, -31, raise));
                yb = new Point(-6, -14);
                yLift = h;
                lift = h;
            }

            if (T > 0.9)
            {
                var sp = Sin01(T * 8);
                Sparkle(xs - 11, Ground - 33, 3 + (sp * 1.4), Gold);
                Sparkle(xs - 16, Ground - 38, 1.8, Gold);
                Sparkle(xs - 6, Ground - 38, 1.8, Gold);
            }
        }

        Kid(
            Who.Yeonju,
            yx,
            new Pose
            {
                Run = yRun,
                Walk = yRun ? (_t * RunHz) + 0.5 : double.NaN,
                FrontHand = yf,
                BackHand = yb,
                Eyes = yEyes,
                Mouth = yMouth,
                Lift = yLift,
                LookX = 1.0,
            });
        Kid(
            Who.Woojin,
            wx,
            new Pose
            {
                Facing = wTurn ?? wFace,
                Run = running,
                Walk = running ? _t * RunHz : double.NaN,
                FrontHand = wf,
                BackHand = wb,
                Lift = lift,
                Eyes = eyes,
                Mouth = mouth,
                Bubble = true,
                LookX = 0.8,
                LookY = T is > 0.3 and < 0.65 ? -0.8 : 0,
            });
        _dc.Pop();

        if (hit && T is > 0.66 and < 0.95 && mode != 1)
        {
            Sparkle(RimX - 9, 9, 2.4 + Sin01(T * 9), Gold);
            Sparkle(RimX + 9, 12, 2 + Sin01((T * 9) + 0.5), Gold);
        }
    }

    // ───────────────────────── 하이파이브: 달려와서 짝! ─────────────────────────

    private void HighFiveScene()
    {
        var c = _w / 2;
        var run = Smooth(Seg(_u, 0, 0.42));
        var xa = Lerp(Math.Max(14, c - 110), c - 12, run);
        var xb = Lerp(Math.Min(_w - 14, c + 110), c + 12, run);
        var running = _u < 0.42;
        var raise = Smooth(Seg(_u, 0.3, 0.5));
        var clap = _u >= 0.5;
        var hop = clap ? Math.Abs(Math.Sin(Seg(_u, 0.5, 1) * Math.PI * 4)) * 2.4 : 0;
        var f = new Point(Lerp(6.5, 12.5, raise), Lerp(-13, -31, raise));
        for (var k = 0; k < 2; k++)
        {
            Kid(
                k == 0 ? Who.Yeonju : Who.Woojin,
                k == 0 ? xa : xb,
                new Pose
                {
                    Facing = k == 0 ? 1 : -1,
                    Run = running,
                    Walk = running ? (_t * RunHz) + (k * 0.5) : double.NaN,
                    FrontHand = _u > 0.3 ? f : null,
                    BackHand = _u > 0.3 ? new Point(-6.5, -13) : null,
                    Lift = hop,
                    Eyes = EyeStyle.Happy,
                    Mouth = MouthStyle.Open,
                    Bubble = k == 1,
                });
        }

        if (clap)
        {
            var sp = Sin01(_t * 7);
            Sparkle(c, Ground - 33, 3.4 + (sp * 1.5), Gold);
            Sparkle(c - 9, Ground - 38, 2 + sp, Gold);
            Sparkle(c + 9, Ground - 38, 2 + (1 - sp), Gold);
        }
    }

    // ───────────────────────── 삭제: 달리며 던지기 ─────────────────────────

    private void TossScene()
    {
        var binX = _w - 26;
        var xt = binX - 58;
        var x0 = Math.Max(16, xt - 150);
        var hit = _f.Outcome != TossOutcome.Miss;
        var T = _u;
        const double aEnd = 0.4;
        var running = T < aEnd;
        var wx = running ? Lerp(x0, xt, Seg(T, 0, aEnd)) : xt;
        var rim = hit ? new Point(binX, Ground - 22) : new Point(binX - 8.5, Ground - 19.5);

        Point? ball;
        Point? inside = null;
        double handY = -12;
        if (T < aEnd)
        {
            var by = DribbleBallY();
            ball = new Point(wx + CrossOffset(), by);
            handY = HandFor(by);
        }
        else if (T < 0.7)
        {
            ball = Arc(Chest(wx, 1, 0), rim, Seg(T, aEnd + 0.04, 0.7), 18);
            if (T < aEnd + 0.04)
            {
                ball = Chest(wx, 1, 0);
            }
        }
        else if (hit)
        {
            ball = null;
            inside = new Point(binX, Lerp(Ground - 22, Ground - 9, Smooth(Seg(T, 0.7, 0.78))));
        }
        else
        {
            ball = Arc(rim, new Point(binX - 30, Ground - 4.2), Seg(T, 0.7, 0.9), 12);
        }

        var wiggle = hit && T > 0.72 ? Math.Sin(T * 90) * 5 * (1 - Seg(T, 0.72, 0.95)) : 0;
        Bin(binX, wiggle, inside);
        _dc.PushOpacity(Opacity(T, 0.05, 0.1));
        if (ball is { } bp)
        {
            Basketball(bp.X, bp.Y, _t * 650);
        }

        var react = T > 0.72;
        Point? wf = running ? new Point(8.2, handY) : new Point(10.5, -21);
        Point? wb = running ? new Point(-5, -15) : new Point(8, -18);
        Kid(
            Who.Woojin,
            wx,
            new Pose
            {
                Run = running,
                Walk = running ? _t * RunHz : double.NaN,
                Tilt = !running && !react ? 6 : 0,
                FrontHand = react ? (hit ? new Point(9, -33) : new Point(-4, -34)) : wf,
                BackHand = react ? (hit ? new Point(-8, -30) : new Point(4, -34)) : wb,
                Lift = (react && hit ? Math.Abs(Math.Sin(T * 20)) * 2 : 0) + (T is > 0.4 and < 0.5 ? Bump(T, 0.4, 0.5) * 3 : 0),
                Eyes = react ? (hit ? EyeStyle.Happy : EyeStyle.Worried) : EyeStyle.Happy,
                Mouth = react ? (hit ? MouthStyle.Open : MouthStyle.Frown) : MouthStyle.Open,
                Bubble = true,
                LookX = 0.8,
                Sweat = react && !hit,
            });
        var yx = (running ? wx : xt) - 30 - (28 * (1 - Smooth(Seg(T, 0, aEnd))));
        Kid(
            Who.Yeonju,
            yx,
            new Pose
            {
                Run = running,
                Walk = running ? (_t * RunHz) + 0.5 : double.NaN,
                Eyes = react ? (hit ? EyeStyle.Happy : EyeStyle.Worried) : EyeStyle.Happy,
                Mouth = react ? (hit ? MouthStyle.Open : MouthStyle.Frown) : MouthStyle.Open,
                FrontHand = react && hit ? new Point(8, -31) : null,
                BackHand = react && hit ? new Point(-7, -29 - (Sin01(T * 10) * 3)) : null,
                Lift = react && hit ? Math.Abs(Math.Sin(T * 18)) * 1.6 : 0,
                LookX = 1.0,
            });
        _dc.Pop();
    }

    private void OutScene()
    {
        var bx = _w - 70;
        var xt = bx - 84;
        var x0 = Math.Max(16, xt - 150);
        var T = _u;
        const double aEnd = 0.4;
        var running = T < aEnd;
        var wx = running ? Lerp(x0, xt, Seg(T, 0, aEnd)) : xt;
        Line(_p.Error, 2, bx, Ground + 1.2, _w - 6, Ground + 1.2);
        Line(_p.MetalDark, 1.4, bx, Ground - 14, bx, Ground);
        Poly(_p.Error, null, new Point(bx, Ground - 14), new Point(bx + 8, Ground - 11.5), new Point(bx, Ground - 9));

        Point? ball;
        double handY = -12;
        if (T < aEnd)
        {
            var by = DribbleBallY();
            ball = new Point(wx + CrossOffset(), by);
            handY = HandFor(by);
        }
        else if (T < 0.44)
        {
            ball = Chest(wx, 1, 0);
        }
        else if (T < 0.82)
        {
            ball = Arc(Chest(wx, 1, 0), new Point(_w + 34, Ground - 4), Seg(T, 0.44, 0.82), 30);
        }
        else
        {
            ball = null;
        }

        var thrown = T > 0.44;
        _dc.PushOpacity(Opacity(T, 0.05, 0.1));
        Kid(
            Who.Woojin,
            wx,
            new Pose
            {
                Run = running,
                Walk = running ? _t * RunHz : double.NaN,
                Tilt = thrown ? 8 : 0,
                FrontHand = running ? new Point(8.2, handY) : (T < 0.44 ? new Point(6.8, -15.5) : new Point(11, -25)),
                BackHand = running ? new Point(-5, -15) : (T < 0.44 ? new Point(5.4, -17.5) : new Point(9, -22)),
                Lift = Bump(T, 0.4, 0.5) * 3,
                Eyes = T > 0.62 ? EyeStyle.Worried : EyeStyle.Happy,
                Mouth = T > 0.62 ? MouthStyle.Open : MouthStyle.Open,
                Bubble = true,
                LookX = 1.2,
            });
        var yx = (running ? wx : xt) - 32 - (28 * (1 - Smooth(Seg(T, 0, aEnd))));
        Kid(
            Who.Yeonju,
            yx,
            new Pose
            {
                Run = running,
                Walk = running ? (_t * RunHz) + 0.5 : double.NaN,
                Eyes = T > 0.5 ? EyeStyle.Worried : EyeStyle.Happy,
                Mouth = MouthStyle.Open,
                FrontHand = T > 0.55 ? new Point(3.5, -31) : null,
                BackHand = T > 0.55 ? new Point(-3.5, -31) : null,
                Lift = Bump(T, 0.5, 0.6) * 2,
                LookX = 1.2,
            });
        if (ball is { } b)
        {
            Basketball(b.X, b.Y, _t * 900);
        }

        _dc.Pop();
    }

    // ───────────────────────── 대기·색인·정지 ─────────────────────────

    private void SearchScene()
    {
        var c = _w / 2;
        var ang = _u * Math.PI * 2;
        // 둘이 반대 방향으로 조깅하며 두리번거린다 (속도 방향 = 바라보는 방향)
        var yx = c - 36 + (26 * Math.Sin(ang));
        var wx = c + 36 - (26 * Math.Sin(ang + 0.7));
        var yFace = Math.Cos(ang) >= 0 ? 1 : -1;
        var wFace = Math.Cos(ang + 0.7) >= 0 ? -1 : 1;
        var look = Math.Sin(_t * 7);
        Kid(
            Who.Yeonju,
            yx,
            new Pose
            {
                Facing = yFace,
                Run = true,
                Walk = _t * 3.0,
                LookX = look * 1.3,
                LookY = -0.5,
                FrontHand = new Point(7.8, -31.5),
                Mouth = MouthStyle.Open,
            });
        Kid(
            Who.Woojin,
            wx,
            new Pose
            {
                Facing = wFace,
                Run = true,
                Walk = (_t * 3.0) + 0.5,
                LookX = -look * 1.3,
                LookY = -0.5,
                FrontHand = new Point(7.8, -31.5),
                Mouth = MouthStyle.Open,
                Eyes = EyeStyle.Worried,
                Bubble = true,
            });
    }

    private void Cone(double x)
    {
        Poly(_p.Accent, null, new Point(x - 3.4, Ground), new Point(x + 3.4, Ground), new Point(x, Ground - 8));
        Line(Colors.White, 1.2, x - 1.8, Ground - 3.6, x + 1.8, Ground - 3.6);
    }

    private void PracticeScene()
    {
        var c = _w / 2;
        // 우진: 콘 사이를 크로스오버 드리블로 지그재그
        var ang = _u * Math.PI * 4;
        var wx = c + 34 + (28 * Math.Sin(ang));
        var wFace = Math.Cos(ang) >= 0 ? 1 : -1;
        Cone(c + 34 - 14);
        Cone(c + 34 + 14);

        // 연주: 두 선 사이를 왕복 전력 질주
        var lineA = c - 74;
        var lineB = c - 26;
        Line(_p.TextSecondary, 1.4, lineA - 8, Ground - 9, lineA - 8, Ground);
        Line(_p.TextSecondary, 1.4, lineB + 8, Ground - 9, lineB + 8, Ground);
        var tri = Tri(_u * 2);
        var yx = Lerp(lineA, lineB, Smooth(tri));
        var yFace = Frac(_u * 2) < 0.5 ? 1 : -1;
        Kid(
            Who.Yeonju,
            yx,
            new Pose
            {
                Facing = yFace,
                Run = true,
                Walk = _t * 4.0,
                Eyes = EyeStyle.Happy,
                Mouth = MouthStyle.Open,
                LookX = 0.8,
            });

        var by = DribbleBallY();
        var off = CrossOffset();
        Kid(
            Who.Woojin,
            wx,
            new Pose
            {
                Facing = wFace,
                Run = true,
                Walk = _t * 4.0,
                FrontHand = new Point(off / KidScale, HandFor(by)),
                BackHand = new Point(-5, -15),
                Bubble = true,
                Eyes = EyeStyle.Happy,
                Mouth = MouthStyle.Open,
                LookX = 0.8,
            });
        Basketball(wx + (wFace * off), by, _t * 600);
    }

    private void HoldScene()
    {
        var c = _w / 2;
        var question = _f.Bubble == MascotBubble.Question;
        var look = question ? Math.Sin(_u * Math.PI * 2) * 1.4 : 0;
        var wx = c + 14;
        var breath = Sin01(_u * 2) * 0.6;
        Kid(
            Who.Yeonju,
            c - 22,
            new Pose
            {
                FrontHand = new Point(6, -10.5),
                BackHand = new Point(-6.5, -13),
                LookX = 1.0,
                Tilt = question ? Math.Sin(_u * Math.PI * 2) * 4 : 0,
                Eyes = question ? EyeStyle.Worried : EyeStyle.Open,
            });
        Kid(
            Who.Woojin,
            wx,
            new Pose
            {
                Facing = -1,
                FrontHand = new Point(ChestHandF.X, ChestHandF.Y + breath),
                BackHand = new Point(ChestHandB.X, ChestHandB.Y + breath),
                LookX = look,
                Tilt = question ? Math.Sin((_u * Math.PI * 2) + 1) * 3 : 0,
                Mouth = question ? MouthStyle.Flat : MouthStyle.Smile,
                Bubble = true,
            });
        var cp = Chest(wx, -1, breath * 0.8);
        Basketball(cp.X, cp.Y, 0);
    }
}
