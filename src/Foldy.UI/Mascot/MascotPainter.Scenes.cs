using System.Windows;
using System.Windows.Media;
using Foldy.Presentation.Mascot;

namespace Foldy.UI.Mascot;

// 장면별 그리기. 같은 입력(프레임, u, t, cycle)은 항상 같은 그림이다.
// 복사·이동·삭제 진행 중에는 시드로 정해진 동작 순서(PlayPlan)를 이어 붙이고, 플레이 중심은 진행률을 따라 왼쪽에서 오른쪽으로 간다.
public sealed partial class MascotPainter
{
    private const double RunHz = 3.6;

    private static readonly Point ChestHandF = new(6.8, -15.5);
    private static readonly Point ChestHandB = new(5.4, -17.5);

    private void DrawScene()
    {
        switch (_f.Scene)
        {
            case MascotScene.Pass: PlayScene(PlaySet.Passing, court: false); break;
            case MascotScene.Dribble: PlayScene(PlaySet.All, court: true); break;
            case MascotScene.Setup: PlayScene(PlaySet.Setup, court: true); break;
            case MascotScene.Shoot: ShotScene(0); break;
            case MascotScene.Score: ShotScene(1); break;
            case MascotScene.Miss: ShotScene(2); break;
            case MascotScene.TossBin: ShotScene(3); break;
            case MascotScene.OutOfBounds: ShotScene(4); break;
            case MascotScene.Dunk: DunkScene(); break;
            case MascotScene.HighFive: HighFiveScene(); break;
            case MascotScene.Search: SearchScene(); break;
            case MascotScene.Practice: PracticeScene(); break;
            case MascotScene.Hold: HoldScene(); break;
            default: break;
        }
    }

    /// <summary>facing 방향 가슴 높이에 공을 안은 위치.</summary>
    private static Point Chest(double x, double facing, double dip = 0) => new(x + (facing * 6.9), Ground - 12.9 + dip);

    /// <summary>좁은 폭에서도 범위가 뒤집히지 않는 Clamp.</summary>
    private static double Cl(double v, double lo, double hi) => Math.Min(Math.Max(v, lo), Math.Max(lo, hi));

    private static double Env(double s, double a, double b, double c, double d) => Smooth(Seg(s, a, b)) * (1 - Smooth(Seg(s, c, d)));

    private static double Opacity(double t, double fadeIn = 0.06, double fadeOut = 0.1) =>
        Math.Clamp(Seg(t, 0, fadeIn) * (1 - Seg(t, 1 - fadeOut, 1)), 0.02, 1);

    private static double Hash01(int a, int b)
    {
        unchecked
        {
            var h = (uint)((a * 374761393) + (b * 668265263));
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return h / (double)uint.MaxValue;
        }
    }

    /// <summary>드리블 중 손이 공을 따라가는 높이(몸 좌표).</summary>
    private static double HandFor(double ballY) => Math.Clamp((ballY - 4.4 - Ground) / KidScale, -21, -9.5);

    private double DribbleBallY() => Ground - 4.2 - (10 * Math.Sin(Frac(_t * 3.6) * Math.PI));

    /// <summary>크로스오버: 공이 좌우 손을 오간다 (몸 기준 앞쪽 거리 3.2 ± 4.2).</summary>
    private double CrossOffset() => 3.2 + (4.2 * Math.Cos(Math.PI * _t * 1.8));

    // ───────────────────────── 동작 순서 ─────────────────────────

    private PlayPlan GetPlan(PlaySet set)
    {
        if (_plan is null || _planKey != (_f.Seed, set))
        {
            _plan = PlayPlan.Create(_f.Seed, set);
            _planKey = (_f.Seed, set);
        }

        return _plan;
    }

    /// <summary>동작 곡선: 기본은 부드럽게 가속·감속, 슛 페이크는 중간에 멈칫, 방향 전환은 반대쪽으로 속이고 치고 나간다.</summary>
    private static double Profile(PlayKind k, double s) => k switch
    {
        PlayKind.FakeShot => s < 0.35 ? 0.45 * (s / 0.35) : (s < 0.6 ? 0.45 + (0.03 * Seg(s, 0.35, 0.6)) : 0.48 + (0.52 * Smooth(Seg(s, 0.6, 1)))),
        PlayKind.ChangeDirection => s < 0.3 ? -0.1 * Smooth(s / 0.3) : -0.1 + (1.1 * Smooth(Seg(s, 0.3, 1))),
        _ => (0.7 * Smooth(s)) + (0.3 * s),
    };

    private (PlaySegment Seg, double S, double G, double Dir, double Walk, double Xa, double Xb) Travel(PlaySet set, double c, double r)
    {
        var plan = GetPlan(set);
        var (seg, s, _) = plan.At(_t);
        var xa = c + ((seg.From - 0.5) * 2 * r);
        var xb = c + ((seg.To - 0.5) * 2 * r);
        var dir = xb >= xa ? 1.0 : -1.0;
        var prog = Profile(seg.Play, s);
        var g = Lerp(xa, xb, prog);
        var dist = (seg.DistStart * 2 * r) + (Math.Abs(xb - xa) * Math.Abs(prog));
        return (seg, s, g, dir, (dist / 20) + (_t * 1.6), xa, xb);
    }

    /// <summary>플레이 장면 바닥: 3점 라인과 골대 / 사이드라인과 휴지통 / 사이드라인.</summary>
    private void PlanCourt(MascotFinish fin)
    {
        switch (fin)
        {
            case MascotFinish.BinToss:
                Sideline();
                Bin(MascotCourt.BinX(_w), 0, null);
                break;
            case MascotFinish.Punt:
                Sideline();
                break;
            default:
                Arc3();
                HoopBack();
                HoopFront(0);
                break;
        }
    }

    private void PlayScene(PlaySet set, bool court)
    {
        var fin = _f.Finish == MascotFinish.None ? MascotFinish.ThreePointer : _f.Finish;
        var c = MascotCourt.PlayCenter(_f.Progress, _w, fin);
        var r = MascotCourt.Excursion(_f.Progress, _w);
        var maxX = court ? MascotCourt.MaxPlayX(_w, fin) : _w - 30;
        var (seg, s, g, f, walk, xa, xb) = Travel(set, c, r);
        if (court)
        {
            PlanCourt(_f.Finish);
        }

        var h = seg.Holder; // 0 = 연주, 1 = 우진
        var p = 1 - h;
        double[] off = [-14, 14]; // 기본: 우진이 앞, 연주가 뒤
        double[] x = new double[2];
        double[] face = [f, f];
        double[] lift = new double[2];
        double[] crouch = new double[2];
        Point?[] fh = new Point?[2];
        Point?[] bh = new Point?[2];
        var running = new[] { true, true };
        var ball = new Point(0, Ground - 6);
        var behind = false;

        switch (seg.Play)
        {
            case PlayKind.GiveAndGo:
                {
                    var sh = Env(s, 0.3, 0.6, 0.8, 1);
                    off[1] += 40 * sh;
                    off[0] += (h == 0 ? 34 : 8) * sh;
                    break;
                }

            case PlayKind.CutToBasket:
                {
                    var sh = Env(s, 0, 0.5, 0.6, 1);
                    off[1] += 44 * sh;
                    off[0] += (p == 0 ? 36 : 6) * sh;
                    break;
                }

            case PlayKind.AlleyOop:
                { var sh3 = Env(s, 0.05, 0.45, 0.85, 1); off[1] += 34 * sh3; off[0] += (p == 0 ? 26 : 4) * sh3; }
                break;
            default:
                break;
        }

        x[0] = g + (f * off[0]);
        x[1] = g + (f * off[1]);

        if (seg.Play == PlayKind.ScreenRoll)
        {
            // 스크린: 한 명이 제자리에 버티고 서 있고 드리블러가 그 옆을 돌아 지나간 뒤 앞으로 롤
            var g0 = Lerp(xa, xb, Profile(PlayKind.ScreenRoll, 0.2));
            var x0 = g0 + (f * off[p]);
            var roll = Smooth(Seg(s, 0.6, 0.85));
            var back = Smooth(Seg(s, 0.85, 1));
            var target = g + (f * (off[p] + (26 * (1 - back))));
            x[p] = s < 0.2 ? x[p] : (s < 0.6 ? x0 : Lerp(x0, target, roll));
            if (s is >= 0.2 and < 0.6)
            {
                running[p] = false;
                fh[p] = new Point(3.5, -15);
                bh[p] = new Point(-3.5, -15.5);
            }
        }

        x[0] = Cl(x[0], 20, maxX);
        x[1] = Cl(x[1], 20, maxX);

        Point CarryBall(int k, double fc)
        {
            var by = DribbleBallY();
            var offb = CrossOffset();
            fh[k] = new Point(offb / KidScale, HandFor(by));
            bh[k] = new Point(-5, -15 + (Math.Sin(_t * RunHz * Math.PI * 2) * 3));
            return new Point(x[k] + (Math.Sign(fc) * offb), by);
        }

        bool Fly(int from, int to, double a, double b, double hgt, bool bounce)
        {
            if (s < a || s >= b)
            {
                return false;
            }

            var k = Seg(s, a, b);
            var p0 = Chest(x[from], f);
            var p1 = Chest(x[to], f);
            var yLine = Lerp(p0.Y, p1.Y, k);
            ball = bounce
                ? new Point(Lerp(p0.X, p1.X, k), yLine + (((Ground - 4.2) - Lerp(p0.Y, p1.Y, 0.5)) * (1 - Math.Abs((2 * k) - 1))))
                : new Point(Lerp(p0.X, p1.X, k), yLine - (Math.Sin(k * Math.PI) * hgt));
            fh[from] = new Point(10.5, -16);
            bh[from] = new Point(9, -17.5);
            fh[to] = new Point(11, -16);
            bh[to] = new Point(9.5, -17);
            return true;
        }

        switch (seg.Play)
        {
            case PlayKind.Crossover:
            case PlayKind.ScreenRoll:
                ball = CarryBall(h, f);
                break;

            case PlayKind.BehindBack:
                {
                    ball = CarryBall(h, f);
                    var w = Bump(s, 0.35, 0.65);
                    if (w > 0.01)
                    {
                        ball = new Point(x[h] + (f * (7 - (16 * w))), Lerp(ball.Y, Ground - 10, w));
                        behind = w > 0.45;
                        fh[h] = new Point(Lerp(fh[h]!.Value.X, 2, w), -13);
                        bh[h] = new Point(Lerp(-5, -10, w), -11);
                    }

                    break;
                }

            case PlayKind.BetweenLegs:
                {
                    ball = CarryBall(h, f);
                    var w = Bump(s, 0.38, 0.62);
                    crouch[h] = 0.9 * w;
                    ball = new Point(Lerp(ball.X, x[h], w), Lerp(ball.Y, Ground - 6, w));
                    behind = w > 0.5;
                    fh[h] = new Point(Lerp(fh[h]!.Value.X, 5, w), Lerp(fh[h]!.Value.Y, -9, w));
                    bh[h] = new Point(-3, Lerp(-14, -9, w));
                    break;
                }

            case PlayKind.SpinMove:
                {
                    ball = CarryBall(h, f);
                    var w = Seg(s, 0.35, 0.65);
                    if (w is > 0 and < 1)
                    {
                        var cs = Math.Cos(Math.PI * 2 * Smooth(w));
                        face[h] = f * cs;
                        ball = new Point(x[h] + (f * 6 * cs), Ground - 12.9);
                        fh[h] = ChestHandF;
                        bh[h] = ChestHandB;
                        lift[h] = Bump(s, 0.35, 0.65) * 1.5;
                        behind = cs < 0;
                    }

                    break;
                }

            case PlayKind.GiveAndGo:
                if (!Fly(h, p, 0.28, 0.42, 7, false) && !Fly(p, h, 0.72, 0.86, 7, false))
                {
                    ball = CarryBall(s is >= 0.42 and < 0.72 ? p : h, f);
                }

                break;

            case PlayKind.BouncePass:
                if (!Fly(h, p, 0.3, 0.55, 0, true))
                {
                    ball = CarryBall(s < 0.3 ? h : p, f);
                }

                break;

            case PlayKind.CutToBasket:
                if (!Fly(h, p, 0.45, 0.62, 6, false))
                {
                    ball = CarryBall(s < 0.45 ? h : p, f);
                }

                break;

            case PlayKind.AlleyOop:
                {
                    lift[p] = 7 * Bump(s, 0.5, 0.82);
                    var catchPt = new Point(x[p] + (f * 3.5), Ground - 33 - lift[p]);
                    if (s is >= 0.3 and < 0.7)
                    {
                        ball = Arc(Chest(x[h], f), new Point(x[p] + (f * 3.5), Ground - 33 - (7 * Bump(0.7, 0.5, 0.82))), Seg(s, 0.3, 0.7), 24);
                        fh[h] = new Point(10.5, -22);
                        bh[h] = new Point(9, -20);
                    }
                    else if (s is >= 0.7 and < 0.82)
                    {
                        var k = Smooth(Seg(s, 0.7, 0.82));
                        var cp = Chest(x[p], f);
                        ball = new Point(Lerp(catchPt.X, cp.X, k), Lerp(catchPt.Y, cp.Y, k));
                    }
                    else
                    {
                        ball = CarryBall(s < 0.3 ? h : p, f);
                    }

                    if (s is >= 0.45 and < 0.82)
                    {
                        fh[p] = new Point(10, -35);
                        bh[p] = new Point(5, -33);
                    }

                    break;
                }

            case PlayKind.FakeShot:
                {
                    ball = CarryBall(h, f);
                    if (s is > 0.35 and < 0.6)
                    {
                        lift[h] = Bump(s, 0.42, 0.58) * 3;
                        ball = new Point(x[h] + (f * 7.5), Ground - 27.5 - lift[h]);
                        fh[h] = new Point(9, -34);
                        bh[h] = new Point(5, -31);
                    }

                    break;
                }

            case PlayKind.ChangeDirection:
                {
                    var fd = s < 0.3 ? -f : f;
                    face[0] = fd;
                    face[1] = fd;
                    ball = CarryBall(h, fd);
                    break;
                }

            default:
                ball = CarryBall(h, f);
                break;
        }

        if (!behind)
        {
            // 공은 아이들 앞에 그린다 (뒤로 가는 동작만 아이 뒤에)
        }
        else
        {
            Basketball(ball.X, ball.Y, _t * 650);
        }

        for (var k = 0; k < 2; k++)
        {
            Kid(
                k == 0 ? Who.Yeonju : Who.Woojin,
                x[k],
                new Pose
                {
                    Facing = face[k],
                    Run = running[k],
                    Walk = running[k] ? walk + (k * 0.5) : double.NaN,
                    FrontHand = fh[k],
                    BackHand = bh[k],
                    Lift = lift[k],
                    Crouch = crouch[k],
                    Eyes = EyeStyle.Happy,
                    Mouth = MouthStyle.Open,
                    LookX = 0.8,
                    Bubble = k == 1,
                });
        }

        if (!behind)
        {
            Basketball(ball.X, ball.Y, _t * 650);
        }
    }

    // ───────────────────────── 마무리: 3점슛 / 던지기 / 실패 ─────────────────────────

    /// <summary>mode 0 = 점프슛 반복(Git push), 1 = 3점슛 성공(복사), 2 = 3점슛 실패(오류), 3 = 휴지통 던지기(삭제), 4 = 경기장 밖으로 멀리 던지기(영구 삭제).</summary>
    private void ShotScene(int mode)
    {
        var basket = mode <= 2;
        var bin = mode == 3;
        var punt = mode == 4;
        var hit = mode == 2 ? false : (mode != 3 || _f.Outcome != TossOutcome.Miss);
        var variant = mode == 0 ? (int)(Hash01(_f.Seed, _cycle) * 3) : 2;

        double xs;
        if (mode == 0)
        {
            xs = RimX - (variant == 0 ? 36 : (variant == 1 ? 58 : MascotCourt.ShotDistance(_w)));
        }
        else
        {
            xs = basket ? MascotCourt.ShotX(_w) : MascotCourt.ThrowX(_w);
        }

        var x0 = Math.Max(16, xs - 80);
        var T = _u;
        const double aEnd = 0.28;
        const double rel = 0.42;
        var flightEnd = punt ? 0.88 : (bin ? 0.68 : 0.65);
        var jumpH = mode == 0 ? (variant == 0 ? 8 : 5) : (punt ? 0 : (bin ? 3 : 4.5));
        var binX = MascotCourt.BinX(_w);

        var running = T < aEnd;
        var wx = running ? Lerp(x0, xs, Seg(T, 0, aEnd)) : xs;
        var lift = Bump(T, 0.28, 0.52) * jumpH;
        Point Rel(double tt) => new(xs + 7.5, Ground - 22 - (Bump(tt, 0.28, 0.52) * jumpH));

        Point target;
        double arcH;
        if (basket)
        {
            target = hit ? new Point(RimX, RimY - 3.5) : new Point(RimX - 6.5, RimY - 3.2);
            arcH = mode == 0 && variant == 0 ? 8 : 9;
        }
        else if (bin)
        {
            target = hit ? new Point(binX, Ground - 22) : new Point(binX - 8.5, Ground - 19.5);
            arcH = 24;
        }
        else
        {
            target = new Point(_w + 50, Ground - 6);
            arcH = 24;
        }

        Point? ball;
        Point? inside = null;
        double handY = -12;
        if (T < aEnd)
        {
            var by = DribbleBallY();
            ball = new Point(wx + CrossOffset(), by);
            handY = HandFor(by);
        }
        else if (T < rel)
        {
            var c = Chest(xs, 1, 0);
            var r0 = Rel(T);
            var k = Smooth(Seg(T, aEnd, rel));
            ball = new Point(Lerp(c.X, r0.X, k), Lerp(c.Y, r0.Y, k));
        }
        else if (T < flightEnd)
        {
            ball = Arc(Rel(rel), target, Seg(T, rel, flightEnd), arcH);
        }
        else if (punt)
        {
            ball = null;
        }
        else if (hit)
        {
            if (bin)
            {
                ball = null;
                inside = new Point(binX, Lerp(Ground - 22, Ground - 9, Smooth(Seg(T, flightEnd, flightEnd + 0.08))));
            }
            else if (T < 0.74)
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
            var land = new Point((bin ? binX : RimX) - 30, Ground - 4.2);
            var s = Seg(T, flightEnd, 0.9);
            ball = Arc(target, land, s, 14);
            if (T > 0.9)
            {
                ball = new Point(land.X - (Seg(T, 0.9, 1) * 8), Ground - 4.2 - (Bump(T, 0.9, 1.0) * 5));
            }
        }

        var wiggle = hit && T > flightEnd ? Math.Sin(T * 90) * 5 * (1 - Seg(T, flightEnd, 0.92)) : 0;
        var fade = mode == 0 ? Opacity(T) : 1;

        // 코트
        if (basket)
        {
            Arc3();
            HoopBack();
        }
        else
        {
            Sideline();
            if (bin)
            {
                Bin(binX, wiggle, inside);
            }
        }

        _dc.PushOpacity(fade);
        if (ball is { } bp)
        {
            Basketball(bp.X, bp.Y, _t * 650);
        }

        if (basket)
        {
            HoopFront(wiggle);
        }

        // 우진
        var wFace = 1.0;
        Point? wf;
        Point? wb = null;
        var eyes = EyeStyle.Happy;
        var mouth = MouthStyle.Open;
        double wTilt = 0;
        if (T < aEnd)
        {
            wf = new Point(8.2, handY);
            wb = new Point(-5, -15);
        }
        else if (punt)
        {
            // 야구 투수처럼 뒤로 크게 젖혔다가 던진다
            var wind = Smooth(Seg(T, aEnd, rel));
            wTilt = T < rel ? -14 * wind : Lerp(-14, 10, Smooth(Seg(T, rel, 0.55)));
            wf = T < rel ? new Point(Lerp(7, -3, wind), Lerp(-16, -30, wind)) : new Point(13, -30);
            wb = new Point(2, -22);
        }
        else if (T < 0.52)
        {
            var up = Seg(T, 0.28, 0.42);
            wf = new Point(Lerp(7, 9, up), Lerp(-16, -27, up));
            wb = new Point(Lerp(5, 4.5, up), Lerp(-17, -25, up));
        }
        else if (T < flightEnd)
        {
            wf = new Point(9, -27);
            wb = new Point(-5, -14);
        }
        else
        {
            eyes = hit ? EyeStyle.Happy : EyeStyle.Worried;
            mouth = hit ? MouthStyle.Open : MouthStyle.Frown;
            wf = hit ? new Point(9, -33) : new Point(-4, -34);
            wb = hit ? new Point(-8, -31) : new Point(4, -34);
        }

        if (punt && T >= 0.55)
        {
            eyes = EyeStyle.Happy;
            wf = new Point(9, -33);
            wb = new Point(-8, -31);
        }

        // 연주: 뒤에서 따라붙어 달려 들어온다
        var yx = (running ? wx : xs) - 28 - (30 * (1 - Smooth(Seg(T, 0, aEnd))));
        var good = hit || punt;
        var yEyes = T > 0.7 ? (good ? EyeStyle.Happy : EyeStyle.Worried) : EyeStyle.Happy;
        var yMouth = T > 0.7 && !good ? MouthStyle.Frown : MouthStyle.Open;
        Point? yf = null;
        Point? yb = null;
        var yLift = 0.0;
        var yRun = running;
        if (T > 0.7 && good && mode == 0)
        {
            yf = new Point(8, -31);
            yb = new Point(-7, -30 - (Sin01(T * 8) * 3));
            yLift = Math.Abs(Math.Sin(T * 18)) * 1.8;
        }
        else if (T > 0.7 && !good)
        {
            yf = new Point(4, -33);
            yb = new Point(-4, -33);
        }

        var highFive = mode != 0 && good;
        if (highFive)
        {
            // 성공하면 연주가 달려와 하이파이브
            var walk = Smooth(Seg(T, 0.62, 0.8));
            yx = Lerp(xs - 28, xs - 22, walk);
            yRun = T is > 0.6 and < 0.8;
            var raise = Smooth(Seg(T, 0.78, 0.9));
            if (T > 0.7)
            {
                wFace = -1;
                var hop = Bump(T, 0.9, 1.0) * 2.5;
                wf = new Point(Lerp(6.5, 12.5, raise), Lerp(-14, -31, raise));
                wb = new Point(-6, -14);
                yf = new Point(Lerp(6.5, 12.5, raise), Lerp(-14, -31, raise));
                yb = new Point(-6, -14);
                yLift = hop;
                lift = hop;
                wTilt = 0;
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
                Facing = wFace,
                Run = running,
                Walk = running ? _t * RunHz : double.NaN,
                FrontHand = wf,
                BackHand = wb,
                Lift = lift,
                Tilt = wTilt,
                Eyes = eyes,
                Mouth = mouth,
                Bubble = true,
                LookX = 0.8,
                LookY = T is > 0.3 and < 0.65 ? -0.8 : 0,
            });
        _dc.Pop();

        if (basket && hit && mode == 0 && T is > 0.66 and < 0.95)
        {
            Sparkle(RimX - 9, 9, 2.4 + Sin01(T * 9), Gold);
            Sparkle(RimX + 9, 12, 2 + Sin01((T * 9) + 0.5), Gold);
        }
    }

    // ───────────────────────── 이동 마무리: 속공 앨리웁 덩크 ─────────────────────────

    private void DunkScene()
    {
        var xs = RimX - 16;
        var x0 = Math.Max(16, xs - 190);
        var T = _u;
        var wx = Lerp(x0 + 30, xs, Smooth(Seg(T, 0, 0.4)));
        var yRunEnd = 0.34;
        var yx = T < yRunEnd ? Lerp(x0, xs - 34, Seg(T, 0, yRunEnd)) : Lerp(xs - 34, xs - 22, Smooth(Seg(T, 0.7, 0.82)));
        var running = T < 0.4;
        var lift = 11 * Bump(T, 0.38, 0.68);
        var catchPt = new Point(wx + 7.5, Ground - 34 - (11 * Bump(0.55, 0.38, 0.68)));
        var hit = new Point(RimX, RimY + 2);

        Point? ball;
        if (T < 0.28)
        {
            var by = DribbleBallY();
            ball = new Point(yx + CrossOffset(), by);
        }
        else if (T < 0.55)
        {
            ball = Arc(Chest(yx, 1, 0), catchPt, Seg(T, 0.28, 0.55), 20);
        }
        else if (T < 0.63)
        {
            ball = Arc(new Point(wx + 7.5, Ground - 34 - lift), hit, Seg(T, 0.55, 0.63), 0);
        }
        else if (T < 0.72)
        {
            ball = new Point(RimX, Lerp(RimY + 2, RimY + 8.5, Seg(T, 0.63, 0.72)));
        }
        else
        {
            var d = Seg(T, 0.72, 0.86);
            ball = new Point(RimX - (Seg(T, 0.86, 1) * 8), Lerp(RimY + 8.5, Ground - 4.2, d * d) - (Bump(T, 0.86, 1.0) * 6));
        }

        var wiggle = T > 0.6 ? Math.Sin(T * 100) * 7 * (1 - Seg(T, 0.6, 0.95)) : 0;
        Arc3();
        HoopBack();
        if (ball is { } b)
        {
            Basketball(b.X, b.Y, _t * 650);
        }

        HoopFront(wiggle);

        var raise = Smooth(Seg(T, 0.8, 0.92));
        var celebrate = T > 0.7;
        Point? wf = T switch
        {
            < 0.28 => null,
            < 0.7 => new Point(10, -36),
            _ => new Point(Lerp(6.5, 12.5, raise), Lerp(-14, -31, raise)),
        };
        Point? wb = T switch
        {
            < 0.28 => null,
            < 0.7 => new Point(6, -33),
            _ => new Point(-6, -14),
        };
        var hop = Bump(T, 0.9, 1.0) * 2.5;
        Kid(
            Who.Yeonju,
            yx,
            new Pose
            {
                Run = T < 0.34,
                Walk = T < 0.34 ? _t * RunHz : (T is >= 0.7 and < 0.82 ? _t * RunHz : double.NaN),
                FrontHand = T switch
                {
                    < 0.28 => null,
                    < 0.42 => new Point(10.5, -24),
                    < 0.7 => null,
                    _ => new Point(Lerp(6.5, 12.5, raise), Lerp(-14, -31, raise)),
                },
                BackHand = T is >= 0.7 ? new Point(-6, -14) : (T is >= 0.28 and < 0.42 ? new Point(9, -22) : null),
                Lift = hop,
                Eyes = EyeStyle.Happy,
                Mouth = MouthStyle.Open,
                LookX = 1,
            });
        Kid(
            Who.Woojin,
            wx,
            new Pose
            {
                Facing = celebrate ? -1 : 1,
                Run = running,
                Walk = running ? _t * RunHz : double.NaN,
                FrontHand = wf,
                BackHand = wb,
                Lift = celebrate ? hop : lift,
                Eyes = EyeStyle.Happy,
                Mouth = MouthStyle.Open,
                Bubble = true,
                LookX = 0.8,
                LookY = T is > 0.3 and < 0.6 ? -0.8 : 0,
            });
        if (T > 0.9)
        {
            var sp = Sin01(T * 8);
            Sparkle(xs - 11, Ground - 33, 3 + (sp * 1.4), Gold);
            Sparkle(xs - 16, Ground - 38, 1.8, Gold);
            Sparkle(xs - 6, Ground - 38, 1.8, Gold);
        }
    }

    // ───────────────────────── 하이파이브 ─────────────────────────

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

    // ───────────────────────── 대기·색인·정지 ─────────────────────────

    private void SearchScene()
    {
        // 둘이 코트 곳곳을 조깅하며 두리번거린다: 위치는 동작 순서의 이동 경로를 그대로 쓴다
        var c = _w / 2;
        var r = Math.Clamp(_w * 0.3, 50, 190);
        var (_, _, g, f, walk, _, _) = Travel(PlaySet.All, c, r);
        var look = Math.Sin(_t * 7);
        var visor = Frac(_t * 0.7) < 0.55;
        var y = Cl(g - (f * 20), 20, _w - 20);
        var w = Cl(g + (f * 20), 20, _w - 20);
        Kid(
            Who.Yeonju,
            y,
            new Pose
            {
                Facing = f,
                Run = true,
                Walk = walk,
                LookX = look * 1.3,
                LookY = -0.5,
                FrontHand = visor ? new Point(7.8, -31.5) : null,
                Mouth = MouthStyle.Open,
            });
        Kid(
            Who.Woojin,
            w,
            new Pose
            {
                Facing = f,
                Run = true,
                Walk = walk + 0.5,
                LookX = -look * 1.3,
                LookY = -0.5,
                FrontHand = !visor ? new Point(7.8, -31.5) : null,
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
        // 한 바퀴(cycle)마다 훈련 위치와 종류가 바뀐다: 콘 지그재그 / 낮은 자세 풋워크 셔플
        var variant = (_cycle + _f.Seed) & 1;
        var cx = Math.Clamp(Lerp(0.3, 0.7, Hash01(_f.Seed, _cycle)) * _w, 100, Math.Max(100, _w - 90));
        var ang = _u * Math.PI * 4;
        var wx = cx + 34 + ((variant == 0 ? 28 : 18) * Math.Sin(ang));
        var wFace = Math.Cos(ang) >= 0 ? 1 : -1;
        if (variant == 0)
        {
            Cone(cx + 34 - 14);
            Cone(cx + 34 + 14);
        }

        var lineA = cx - 74;
        var lineB = cx - 26;
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

        var by = variant == 0 ? DribbleBallY() : Ground - 4.2 - (6 * Math.Sin(Frac(_t * 4.4) * Math.PI));
        var off = CrossOffset();
        Kid(
            Who.Woojin,
            wx,
            new Pose
            {
                Facing = wFace,
                Run = true,
                Walk = _t * 4.0,
                Crouch = variant == 0 ? 0 : 0.5,
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
