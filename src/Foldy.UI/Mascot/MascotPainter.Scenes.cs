using System.Windows;
using System.Windows.Media;
using Foldy.Presentation.Mascot;

namespace Foldy.UI.Mascot;

// 장면별 그리기. 모든 장면은 위상 _u(0~1)와 누적 시간 _t만으로 결정되므로 같은 입력은 항상 같은 그림이다.
public sealed partial class MascotPainter
{
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

    // ───────────────────────── 패스 ─────────────────────────

    private void PassScene()
    {
        var a = (_w / 2) - 44;
        var b = (_w / 2) + 44;
        var toB = _u < 0.5;
        var s = Frac(_u * 2);
        var fl = Seg(s, 0.22, 0.7);
        var from = toB ? new Point(a, 1) : new Point(b, -1);
        var to = toB ? new Point(b, -1) : new Point(a, 1);

        Point ball;
        if (s < 0.22)
        {
            ball = Chest(from.X, from.Y);
        }
        else if (s < 0.7)
        {
            ball = Arc(Chest(from.X, from.Y), Chest(to.X, to.Y), Smooth(fl) * 0.5 + (fl * 0.5), 7);
        }
        else
        {
            ball = Chest(to.X, to.Y);
        }

        for (var k = 0; k < 2; k++)
        {
            var isA = k == 0;
            var thrower = isA == toB;
            Point? fh;
            Point? bh;
            if (thrower)
            {
                var push = Seg(s, 0.15, 0.3);
                fh = s < 0.3 ? new Point(Lerp(6.8, 10.5, push), -15.5) : new Point(9.5, -16);
                bh = s < 0.3 ? new Point(Lerp(5.4, 9, push), -17.5) : new Point(8.5, -15);
            }
            else if (s >= 0.7)
            {
                fh = ChestHandF;
                bh = ChestHandB;
            }
            else
            {
                var open = Seg(s, 0.25, 0.45);
                fh = new Point(Lerp(7, 11, open), -15.5);
                bh = new Point(Lerp(5, 9.5, open), -17);
            }

            Kid(
                isA ? Who.Yeonju : Who.Woojin,
                isA ? a : b,
                new Pose
                {
                    Facing = isA ? 1 : -1,
                    FrontHand = fh,
                    BackHand = bh,
                    Lift = !thrower ? Bump(s, 0.68, 0.85) * 1.8 : 0,
                    Eyes = !thrower && s >= 0.7 ? EyeStyle.Happy : EyeStyle.Open,
                    LookX = 0.8,
                    Bubble = !isA,
                });
        }

        Basketball(ball.X, ball.Y, _u * 720);
    }

    // ───────────────────────── 드리블 ─────────────────────────

    private void DribbleScene()
    {
        var x0 = _w * 0.34;
        var x1 = _w * 0.62;
        var p = _f.Progress;
        double x;
        double f = 1;
        if (p >= 0)
        {
            x = Lerp(x0, x1, Smooth(Seg(p, MascotViewModel.PassUntil, MascotViewModel.ShootFrom)));
        }
        else
        {
            x = Lerp(x0, x1, Smooth(Tri(_u)));
            f = _u < 0.5 ? 1 : -1;
        }

        var ph = Frac(_t * 1.7);
        var ballY = Ground - 4.2 - (10 * Math.Sin(ph * Math.PI));
        var handY = Math.Clamp((ballY - 4.4 - Ground) / KidScale, -21, -9.5);
        var ballX = x + (f * 6.9);

        // 연주: 응원
        var cheer = Math.Abs(Math.Sin(_t * 4.2));
        Kid(
            Who.Yeonju,
            _w * 0.12,
            new Pose
            {
                Lift = cheer * 1.6,
                Eyes = EyeStyle.Happy,
                FrontHand = new Point(8.2, -31 + (Math.Sin(_t * 8.4) * 1.6)),
                BackHand = new Point(-7, -27 - (cheer * 3)),
                Mouth = MouthStyle.Open,
            });

        Kid(
            Who.Woojin,
            x,
            new Pose
            {
                Facing = f,
                Walk = _t * 1.4,
                FrontHand = new Point(8.2, handY),
                BackHand = new Point(-5.5, -15),
                Tilt = 5,
                Bubble = true,
                LookX = 0.8,
            });
        Basketball(ballX, ballY, _t * 400);
    }

    // ───────────────────────── 슛 / 성공 / 실패 ─────────────────────────

    /// <summary>mode 0 = 슛 반복(Git push), 1 = 3점슛 성공 + 하이파이브, 2 = 림에 맞고 튕김.</summary>
    private void ShotScene(int mode)
    {
        var xs = _w - 100;
        var yjStart = xs - 42;
        var T = _u;
        var hit = mode != 2;

        var crouch = Smooth(Seg(T, 0.0, 0.2)) * (T < 0.22 ? 1 : 0);
        var lift = Bump(T, 0.2, 0.46) * 6;
        var rise = Smooth(Seg(T, 0.2, 0.4));
        var release = new Point(xs + 3, Ground - 33 - lift);

        var target = hit ? new Point(RimX, RimY - 3.5) : new Point(RimX - 6.5, RimY - 3.2);
        Point ball;
        var rot = T * 500;
        if (T < 0.2)
        {
            ball = Chest(xs, 1, crouch * 2.5);
        }
        else if (T < 0.4)
        {
            var c = Chest(xs, 1, 0);
            ball = new Point(Lerp(c.X, release.X, rise), Lerp(c.Y, Ground - 33 - (Bump(T, 0.2, 0.46) * 6), rise));
        }
        else if (T < 0.7)
        {
            var s = Seg(T, 0.4, 0.7);
            ball = Arc(new Point(release.X, Ground - 33 - (Bump(0.4, 0.2, 0.46) * 6)), target, s, 16);
        }
        else if (hit)
        {
            var s = Seg(T, 0.7, 0.8);
            ball = new Point(RimX, Lerp(RimY - 3.5, RimY + 8.5, s));
            if (T >= 0.8)
            {
                var d = Seg(T, 0.8, 0.9);
                ball = new Point(RimX - (Seg(T, 0.9, 1) * 20), Lerp(RimY + 8.5, Ground - 4.2, d * d) - (Bump(T, 0.9, 1.0) * 0));
                if (mode == 0 && T > 0.9)
                {
                    ball = new Point(Lerp(RimX, xs + 7, Seg(T, 0.9, 1.0)), Ground - 4.2);
                }
            }
        }
        else
        {
            var s = Seg(T, 0.7, 0.92);
            ball = Arc(target, new Point(RimX - 30, Ground - 4.2), s, 14);
            if (T > 0.92)
            {
                ball = new Point(RimX - 30 - (Seg(T, 0.92, 1) * 6), Ground - 4.2);
            }
        }

        var wiggle = hit && T > 0.7 ? Math.Sin(T * 90) * 5 * (1 - Seg(T, 0.7, 0.95)) : 0;
        HoopBack();
        Basketball(ball.X, ball.Y, rot);
        HoopFront(wiggle);

        // 우진의 자세
        var wFace = 1.0;
        var wx = xs;
        Point? wf = null;
        Point? wb = null;
        var eyes = EyeStyle.Open;
        var mouth = MouthStyle.Smile;
        if (T < 0.2)
        {
            wf = new Point(6.8, -15.5 + (crouch * 1.5));
            wb = new Point(5.4, -17.5 + (crouch * 1.5));
            mouth = MouthStyle.Flat;
        }
        else if (T < 0.46)
        {
            var up = Seg(T, 0.2, 0.4);
            wf = new Point(Lerp(6.8, 7.5, up), Lerp(-15.5, -35, up));
            wb = new Point(Lerp(5.4, 4.5, up), Lerp(-17.5, -32, up));
            if (T > 0.4)
            {
                wf = new Point(9, -36.5);
            }
        }
        else
        {
            wf = new Point(9, -36);
            wb = new Point(-5, -14);
            if (T > 0.7)
            {
                eyes = hit ? EyeStyle.Happy : EyeStyle.Worried;
                mouth = hit ? MouthStyle.Open : MouthStyle.Frown;
                wf = hit ? new Point(9, -33) : new Point(-4, -34);
                wb = hit ? new Point(-8, -31) : new Point(4, -34);
            }
        }

        var yx = yjStart;
        var yFace = 1.0;
        Point? yf = null;
        Point? yb = null;
        var yEyes = T > 0.72 ? (hit ? EyeStyle.Happy : EyeStyle.Worried) : EyeStyle.Open;
        var yMouth = T > 0.72 ? (hit ? MouthStyle.Open : MouthStyle.Frown) : MouthStyle.Smile;
        var yLift = 0.0;
        if (T > 0.72 && hit && mode == 0)
        {
            yf = new Point(8, -31);
            yb = new Point(-7, -30 - (Sin01(T * 6) * 3));
            yLift = Math.Abs(Math.Sin(T * 14)) * 1.8;
        }
        else if (T > 0.72 && !hit)
        {
            yf = new Point(4, -33);
            yb = new Point(-4, -33);
        }

        var wLift = lift;
        double wTilt = 0;
        double? yFaceOverride = null;
        if (mode == 1)
        {
            // 3점슛 성공 후 연주가 달려와 하이파이브
            var walk = Smooth(Seg(T, 0.62, 0.8));
            yx = Lerp(yjStart, xs - 22, walk);
            var raise = Smooth(Seg(T, 0.78, 0.9));
            if (T > 0.7)
            {
                wFace = -1;
                wLift = Bump(T, 0.9, 1.0) * 2.5;
                wf = new Point(Lerp(6.5, 12.5, raise), Lerp(-14, -31, raise));
                wb = new Point(-6, -14);
                yf = new Point(Lerp(6.5, 12.5, raise), Lerp(-14, -31, raise));
                yb = new Point(-6, -14);
                yLift = Bump(T, 0.9, 1.0) * 2.5;
                yFaceOverride = 1;
            }

            if (T > 0.64 && T < 0.8)
            {
                yf = null;
            }

            var wlk = T is > 0.62 and < 0.8;
            if (T > 0.9)
            {
                var sp = Sin01(T * 8);
                Sparkle(xs - 11, Ground - 33, 3 + (sp * 1.4), Gold);
                Sparkle(xs - 16, Ground - 38, 1.8, Gold);
                Sparkle(xs - 6, Ground - 38, 1.8, Gold);
            }

            Kid(Who.Yeonju, yx, new Pose { Facing = yFaceOverride ?? yFace, Walk = wlk ? (_t * 2.2) : double.NaN, FrontHand = yf, BackHand = yb, Eyes = yEyes, Mouth = yMouth, Lift = yLift, LookX = 0.8 });
        }
        else
        {
            Kid(Who.Yeonju, yx, new Pose { Facing = yFace, FrontHand = yf, BackHand = yb, Eyes = yEyes, Mouth = yMouth, Lift = yLift, LookX = 1.0 });
        }

        Kid(Who.Woojin, wx, new Pose { Facing = wFace, FrontHand = wf, BackHand = wb, Lift = wLift, Crouch = crouch, Eyes = eyes, Mouth = mouth, Bubble = true, Tilt = wTilt, LookX = 0.8, LookY = T is > 0.2 and < 0.7 ? -0.8 : 0 });

        if (hit && T is > 0.72 and < 0.95 && mode != 1)
        {
            Sparkle(RimX - 9, 9, 2.4 + Sin01(T * 7), Gold);
            Sparkle(RimX + 9, 12, 2 + Sin01((T * 7) + 0.5), Gold);
        }
    }

    // ───────────────────────── 하이파이브 ─────────────────────────

    private void HighFiveScene()
    {
        var c = _w / 2;
        var raise = Smooth(Seg(_u, 0.18, 0.45));
        var clap = _u >= 0.47;
        var hop = clap ? Math.Abs(Math.Sin(Seg(_u, 0.47, 1) * Math.PI * 3)) * 2.2 : 0;
        var f = new Point(Lerp(6.5, 12.5, raise), Lerp(-13, -31, raise));
        for (var k = 0; k < 2; k++)
        {
            Kid(
                k == 0 ? Who.Yeonju : Who.Woojin,
                k == 0 ? c - 12 : c + 12,
                new Pose
                {
                    Facing = k == 0 ? 1 : -1,
                    FrontHand = f,
                    BackHand = new Point(-6.5, -13),
                    Lift = hop,
                    Eyes = clap ? EyeStyle.Happy : EyeStyle.Open,
                    Mouth = clap ? MouthStyle.Open : MouthStyle.Smile,
                    Bubble = k == 1,
                });
        }

        if (clap)
        {
            var sp = Sin01(_t * 6);
            Sparkle(c, Ground - 33, 3.4 + (sp * 1.5), Gold);
            Sparkle(c - 9, Ground - 38, 2 + sp, Gold);
            Sparkle(c + 9, Ground - 38, 2 + (1 - sp), Gold);
        }
    }

    // ───────────────────────── 삭제 ─────────────────────────

    private void TossScene()
    {
        var binX = _w - 26;
        var wx = binX - 58;
        var yx = wx - 34;
        var hit = _f.Outcome != TossOutcome.Miss;
        var T = _u;
        var crouch = Bump(T, 0.2, 0.42);
        var start = Chest(wx, 1, crouch * 2);
        var rim = hit ? new Point(binX, Ground - 22) : new Point(binX - 8.5, Ground - 19.5);

        Point? ball = start;
        Point? inside = null;
        if (T < 0.38)
        {
            ball = start;
        }
        else if (T < 0.7)
        {
            ball = Arc(Chest(wx, 1, 0), rim, Seg(T, 0.38, 0.7), 18);
        }
        else if (hit)
        {
            ball = null;
            inside = new Point(binX, Lerp(Ground - 22, Ground - 9, Smooth(Seg(T, 0.7, 0.8))));
        }
        else
        {
            var s = Seg(T, 0.7, 0.9);
            ball = Arc(rim, new Point(binX - 30, Ground - 4.2), s, 12);
        }

        var wiggle = hit && T > 0.72 ? Math.Sin(T * 80) * 5 * (1 - Seg(T, 0.72, 0.95)) : 0;
        Bin(binX, wiggle, inside);
        if (ball is { } bp)
        {
            Basketball(bp.X, bp.Y, T * 500);
        }

        var react = T > 0.7;
        var wf = T < 0.38 ? new Point(6.8, -15.5 + (crouch * 3)) : new Point(9.5, -20);
        var wb = T < 0.38 ? new Point(5.4, -17.5 + (crouch * 3)) : new Point(8, -18);
        Kid(
            Who.Woojin,
            wx,
            new Pose
            {
                Crouch = crouch * 0.8,
                FrontHand = react ? (hit ? new Point(9, -33) : new Point(-4, -34)) : wf,
                BackHand = react ? (hit ? new Point(-8, -30) : new Point(4, -34)) : wb,
                Lift = react && hit ? Math.Abs(Math.Sin(T * 16)) * 2 : 0,
                Eyes = react ? (hit ? EyeStyle.Happy : EyeStyle.Worried) : EyeStyle.Open,
                Mouth = react ? (hit ? MouthStyle.Open : MouthStyle.Frown) : MouthStyle.Flat,
                Bubble = true,
                LookX = 0.8,
                Sweat = react && !hit,
            });
        Kid(
            Who.Yeonju,
            yx,
            new Pose
            {
                Eyes = react ? (hit ? EyeStyle.Happy : EyeStyle.Worried) : EyeStyle.Open,
                Mouth = react ? (hit ? MouthStyle.Open : MouthStyle.Frown) : MouthStyle.Smile,
                FrontHand = react && hit ? new Point(8, -31) : null,
                BackHand = react && hit ? new Point(-7, -29 - (Sin01(T * 8) * 3)) : null,
                Lift = react && hit ? Math.Abs(Math.Sin(T * 14)) * 1.6 : 0,
                LookX = 1.0,
            });
    }

    private void OutScene()
    {
        var bx = _w - 70;
        var wx = bx - 76;
        var T = _u;
        // 사이드라인과 "아웃" 쪽 바닥
        Line(_p.Error, 2, bx, Ground + 1.2, _w - 6, Ground + 1.2);
        Line(_p.MetalDark, 1.4, bx, Ground - 14, bx, Ground);
        Poly(_p.Error, null, new Point(bx, Ground - 14), new Point(bx + 8, Ground - 11.5), new Point(bx, Ground - 9));

        var start = Chest(wx, 1, Bump(T, 0.2, 0.4) * 2);
        Point? ball = start;
        if (T >= 0.36 && T < 0.85)
        {
            ball = Arc(Chest(wx, 1, 0), new Point(_w + 34, Ground - 4), Seg(T, 0.36, 0.85), 30);
        }
        else if (T >= 0.85)
        {
            ball = null;
        }

        var thrown = T > 0.36;
        Kid(
            Who.Woojin,
            wx,
            new Pose
            {
                Crouch = Bump(T, 0.2, 0.4) * 0.6,
                Tilt = thrown ? 6 : 0,
                FrontHand = T < 0.36 ? new Point(6.8, -15.5) : new Point(11, -25),
                BackHand = T < 0.36 ? new Point(5.4, -17.5) : new Point(9, -22),
                Eyes = T > 0.62 ? EyeStyle.Worried : EyeStyle.Open,
                Mouth = T > 0.62 ? MouthStyle.Open : MouthStyle.Flat,
                Bubble = true,
                LookX = 1.2,
            });
        Kid(
            Who.Yeonju,
            wx - 34,
            new Pose
            {
                Eyes = T > 0.45 ? EyeStyle.Worried : EyeStyle.Open,
                Mouth = T > 0.45 ? MouthStyle.Open : MouthStyle.Smile,
                FrontHand = T > 0.5 ? new Point(3.5, -31) : null,
                BackHand = T > 0.5 ? new Point(-3.5, -31) : null,
                Lift = Bump(T, 0.45, 0.55) * 2,
                LookX = 1.2,
            });
        if (ball is { } b)
        {
            Basketball(b.X, b.Y, T * 900);
        }
    }

    // ───────────────────────── 대기·색인·정지 ─────────────────────────

    private void SearchScene()
    {
        var c = _w / 2;
        var sw = Math.Sin(_u * Math.PI * 2 * 2);
        var sw2 = Math.Sin((_u * Math.PI * 2 * 2) + 2.3);
        Kid(
            Who.Yeonju,
            c - 26,
            new Pose
            {
                Facing = sw >= 0 ? 1 : -1,
                LookX = sw * 1.2,
                Tilt = sw * 3,
                FrontHand = new Point(7.8, -31.5),
                Mouth = MouthStyle.Open,
            });
        Kid(
            Who.Woojin,
            c + 26,
            new Pose
            {
                Facing = sw2 >= 0 ? -1 : 1,
                LookX = sw2 * 1.2,
                Tilt = sw2 * 3,
                FrontHand = new Point(7.8, -31.5),
                Mouth = MouthStyle.Open,
                Eyes = EyeStyle.Worried,
                Bubble = true,
            });
    }

    private void PracticeScene()
    {
        var c = _w / 2;
        var wx = c + 18;
        var ph = Frac(_t * 1.7);
        var ballY = Ground - 4.2 - (10 * Math.Sin(ph * Math.PI));
        var lx = 3.5 + (6.5 * Math.Cos(_t * 0.85 * Math.PI));
        var ballX = wx + (lx * KidScale);

        // 연주: 기록판
        var yx = c - 34;
        Kid(
            Who.Yeonju,
            yx,
            new Pose
            {
                FrontHand = new Point(7.4, -14.5),
                BackHand = new Point(3.5, -13),
                LookX = 1.2,
                Mouth = MouthStyle.Flat,
            });
        RR(_p.Surface, yx + 3.8, Ground - 18.5, 8.5, 11, 1.2, Pn(_p.MetalDark, 0.8));
        RR(_p.Accent, yx + 6.2, Ground - 19.6, 3.6, 2, 0.8);
        var n = (int)(Frac(_t * 0.5) * 4) + 1;
        for (var i = 0; i < n; i++)
        {
            Line(_p.TextSecondary, 0.8, yx + 5.3, Ground - 15.6 + (i * 2.1), yx + 11, Ground - 15.6 + (i * 2.1));
        }

        var handY = Math.Clamp((ballY - 4.4 - Ground) / KidScale, -21, -9.5);
        Kid(
            Who.Woojin,
            wx,
            new Pose
            {
                Facing = 1,
                Tilt = 4,
                FrontHand = new Point(lx, handY),
                BackHand = new Point(-5.5, -15),
                Crouch = 0.3,
                Bubble = true,
                LookX = 0.8,
                Mouth = MouthStyle.Flat,
            });
        Basketball(ballX, ballY, _t * 400);
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
