using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Foldy.Presentation.Mascot;

namespace Foldy.UI.Mascot;

internal enum EyeStyle { Open, Happy, Worried, Closed }

internal enum MouthStyle { Smile, Flat, Open, Frown }

internal enum Who { Yeonju, Woojin }

/// <summary>아이 한 명의 자세. 좌표는 몸 기준(발 중앙이 원점, 위쪽이 -y, 앞쪽이 +x), 단위는 축소 전.</summary>
internal sealed class Pose
{
    public double Facing { get; init; } = 1;

    /// <summary>걷기 위상. NaN이면 서 있음.</summary>
    public double Walk { get; init; } = double.NaN;

    public double Tilt { get; init; }

    /// <summary>뛰어오른 높이(px, 화면 단위).</summary>
    public double Lift { get; init; }

    /// <summary>0~1 무릎 굽힘.</summary>
    public double Crouch { get; init; }

    public EyeStyle Eyes { get; init; } = EyeStyle.Open;

    public MouthStyle Mouth { get; init; } = MouthStyle.Smile;

    public double LookX { get; init; }

    public double LookY { get; init; }

    public Point? FrontHand { get; init; }

    public Point? BackHand { get; init; }

    /// <summary>말풍선이 이 아이 머리 옆에 뜬다.</summary>
    public bool Bubble { get; init; }

    public bool Sweat { get; init; }
}

/// <summary>
/// 마스코트 장면을 DrawingContext로 그린다. 모던(벡터)과 레트로(저해상도 렌더 후 확대)가 같은 코드를 쓴다.
/// 디자인 좌표계: 높이 48, 땅 y=38, 그 밑 10px는 이름표 자리. 폭은 최대 MaxWorld까지 쓰고 남으면 가운데 정렬.
/// </summary>
public sealed partial class MascotPainter
{
    public const double DesignHeight = 48;
    public const double Ground = 36.5;

    private const double KidScale = 0.8;

    private readonly Dictionary<Color, SolidColorBrush> _brushes = [];
    private readonly Dictionary<(Color, double), Pen> _pens = [];
    private readonly List<(string Name, double X)> _labels = [];
    private readonly MascotPalette _p;
    private readonly double _minStroke;
    private readonly double _maxWorld;

    private DrawingContext _dc = null!;
    private MascotFrame _f;
    private double _u;
    private double _t;
    private double _w;
    private double _ox;
    private Point _anchor;

    /// <param name="palette">색.</param>
    /// <param name="minStroke">최소 선 굵기(디자인 px). 레트로는 픽셀 한 칸 크기로 올려 선이 사라지지 않게 한다.</param>
    /// <param name="maxWorld">장면이 쓰는 최대 폭.</param>
    public MascotPainter(MascotPalette palette, double minStroke = 0, double maxWorld = 420)
    {
        _p = palette;
        _minStroke = minStroke;
        _maxWorld = maxWorld;
    }

    /// <summary>true면 이름표를 장면과 같은 패스에서 그린다. 레트로는 끄고 <see cref="PaintLabels"/>로 확대 후에 또렷하게 얹는다.</summary>
    public bool DrawLabelsInline { get; set; } = true;

    public double PixelsPerDip { get; set; } = 1.0;

    /// <summary>
    /// 한 장면을 그린다. u = 장면 반복 위상(0~1), t = 장면 시작 후 누적 초(속도 반영; 눈 깜빡임 등 연속 동작용).
    /// </summary>
    public void Paint(DrawingContext dc, double width, MascotFrame frame, double u, double t)
    {
        _labels.Clear();
        if (!frame.Visible || width < 20)
        {
            return;
        }

        _dc = dc;
        _f = frame;
        _u = Math.Clamp(u, 0, 1);
        _t = t;
        _w = Math.Min(width, _maxWorld);
        _ox = (width - _w) / 2;
        _anchor = new Point(_w / 2, 12);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, DesignHeight)));
        dc.PushTransform(new TranslateTransform(_ox, 0));
        Line(_p.Border, 2, 6, Ground + 1.2, _w - 6, Ground + 1.2);
        DrawScene();
        DrawBubble();
        if (DrawLabelsInline)
        {
            DrawLabelTexts();
        }

        dc.Pop();
        dc.Pop();
    }

    /// <summary>레트로용: 직전 Paint에서 기록한 이름표를 확대된 비트맵 위에 벡터 글자로 얹는다.</summary>
    public void PaintLabels(DrawingContext dc)
    {
        if (_labels.Count == 0)
        {
            return;
        }

        _dc = dc;
        dc.PushTransform(new TranslateTransform(_ox, 0));
        DrawLabelTexts();
        dc.Pop();
    }

    private void DrawLabelTexts()
    {
        foreach (var (name, x) in _labels)
        {
            var ft = new FormattedText(
                name,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Malgun Gothic, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                8.5,
                B(_p.Text),
                PixelsPerDip);
            _dc.DrawText(ft, new Point(x - (ft.Width / 2), 43.6 - (ft.Height / 2)));
        }
    }

    // ───────────────────────── 기본 도구 ─────────────────────────

    private SolidColorBrush B(Color c, double alpha = 1)
    {
        if (alpha < 1)
        {
            c = Color.FromArgb((byte)(c.A * alpha), c.R, c.G, c.B);
        }

        if (!_brushes.TryGetValue(c, out var b))
        {
            b = new SolidColorBrush(c);
            b.Freeze();
            _brushes[c] = b;
        }

        return b;
    }

    private Pen Pn(Color c, double thickness)
    {
        thickness = Math.Max(thickness, _minStroke);
        var key = (c, thickness);
        if (!_pens.TryGetValue(key, out var pen))
        {
            pen = new Pen(B(c), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            _pens[key] = pen;
        }

        return pen;
    }

    private void RR(Color fill, double x, double y, double w, double h, double r, Pen? pen = null) =>
        _dc.DrawRoundedRectangle(B(fill), pen, new Rect(x, y, Math.Max(w, 0), Math.Max(h, 0)), r, r);

    private void Oval(Color fill, double cx, double cy, double rx, double ry, Pen? pen = null) =>
        _dc.DrawEllipse(B(fill), pen, new Point(cx, cy), rx, ry);

    private void Ring(Color c, double thickness, double cx, double cy, double rx, double ry) =>
        _dc.DrawEllipse(null, Pn(c, thickness), new Point(cx, cy), rx, ry);

    private void Line(Color c, double thickness, double x1, double y1, double x2, double y2) =>
        _dc.DrawLine(Pn(c, thickness), new Point(x1, y1), new Point(x2, y2));

    private void Poly(Color fill, Pen? pen, params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], true, true);
            c.PolyLineTo(pts.AsSpan(1).ToArray(), pen is not null, true);
        }

        g.Freeze();
        _dc.DrawGeometry(B(fill), pen, g);
    }

    private void Curve(Pen pen, Point a, Point ctrl, Point b)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(a, false, false);
            c.QuadraticBezierTo(ctrl, b, true, true);
        }

        g.Freeze();
        _dc.DrawGeometry(null, pen, g);
    }

    private void Push(double tx, double ty, double angle = 0, double sx = 1, double sy = 1, double ox = 0, double oy = 0)
    {
        var g = new TransformGroup();
        g.Children.Add(new ScaleTransform(sx, sy, ox, oy));
        g.Children.Add(new RotateTransform(angle, ox, oy));
        g.Children.Add(new TranslateTransform(tx, ty));
        _dc.PushTransform(g);
    }

    private void Pop() => _dc.Pop();

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    private static double Seg(double u, double a, double b) => Math.Clamp((u - a) / (b - a), 0, 1);

    private static double Smooth(double t) => t * t * (3 - (2 * t));

    private static double Frac(double v) => v - Math.Floor(v);

    private static double Tri(double u) => 1 - Math.Abs((2 * Frac(u)) - 1);

    private static double Sin01(double u) => 0.5 + (0.5 * Math.Sin(u * Math.PI * 2));

    private static double Bump(double u, double a, double b) => Math.Sin(Seg(u, a, b) * Math.PI);

    private static Point Arc(Point a, Point b, double s, double height) =>
        new(Lerp(a.X, b.X, s), Lerp(a.Y, b.Y, s) - (Math.Sin(s * Math.PI) * height));

    // ───────────────────────── 아이들 ─────────────────────────

    /// <summary>연주(Yeonju: 여, 포니테일)와 우진(Woojin: 남, 짧은 머리). 발이 (x, 땅)에 선다. 이름표는 발 밑에 따라온다.</summary>
    private void Kid(Who who, double x, Pose p)
    {
        var girl = who == Who.Yeonju;
        var jersey = girl ? _p.Body : _p.Jersey2;
        var jerseyDark = girl ? _p.BodyDark : _p.Jersey2Dark;
        var hair = girl ? _p.HairGirl : _p.HairBoy;
        var walking = !double.IsNaN(p.Walk);
        var bob = walking ? Math.Abs(Math.Sin(p.Walk * Math.PI)) * 1.3 : 0;
        var hipY = -9 + (3.2 * p.Crouch) - bob;
        var by = Ground - p.Lift;
        _labels.Add((girl ? "연주" : "우진", x));

        Push(x, by, p.Tilt * p.Facing, p.Facing * KidScale, KidScale, 0, 0);
        if (p.Bubble)
        {
            _anchor = new Point(x + (p.Facing * 0), by + ((hipY - 28) * KidScale));
        }

        // 다리와 신발
        for (var i = -1; i <= 1; i += 2)
        {
            var ph = (p.Walk + (i < 0 ? 0 : 0.5)) * Math.PI * 2;
            var swing = walking ? Math.Sin(ph) * 3.2 : 0;
            var lift = walking ? Math.Max(0, Math.Cos(ph)) * 2.2 : 0;
            var lx = (i * 2.7) + swing + (p.Crouch * i * 0.8);
            RR(_p.Skin, lx - 1.35, hipY, 2.7, -hipY - lift - 1.2, 1.2);
            RR(girl ? _p.BodyDark : _p.Jersey2Dark, lx - 2.3, -2.6 - lift, 5, 2.9, 1.3);
            RR(Colors.White, lx - 2.1, -0.6 - lift, 4.6, 1.0, 0.5);
        }

        // 몸통 이하는 hipY에 맞춰 내려앉는다
        Push(0, hipY + 9, 0);

        var swingArm = walking ? Math.Sin(p.Walk * Math.PI * 2) * 2.2 : 0;
        var back = p.BackHand ?? new Point(-7.4 - (swingArm * 0.4), -13.2);
        var front = p.FrontHand ?? new Point(7.4 + (swingArm * 0.4), -13.2);
        Arm(new Point(-5, -22), back, hair, true);

        // 여자아이 포니테일은 머리 뒤
        if (girl)
        {
            var sw = Math.Sin(_t * 6) * 1.1;
            Oval(hair, -6.6 + sw, -29.5, 2.5, 2.5);
            Oval(hair, -7.8 + (sw * 1.6), -26.4, 1.7, 3.4);
        }

        // 반바지 · 상의
        RR(jerseyDark, -5.4, -14.2, 10.8, 6.6, 1.8);
        RR(jersey, -5.4, -24.2, 10.8, 11.2, 2.6);
        RR(Colors.White, -5.4, -14.6, 10.8, 1.1, 0.5);
        Poly(_p.Skin, null, new Point(-1.8, -24.2), new Point(1.8, -24.2), new Point(0, -21.6));
        Line(Colors.White, 0.9, -5.2, -19.4, 5.2, -19.4);

        // 머리: 머리카락 → 얼굴 → 앞머리
        Oval(hair, 0, -31.2, 5.9, 5.0);
        Oval(_p.Skin, 0, -28.6, 5.1, 4.9);
        Oval(hair, girl ? 1.8 : 1.4, -33.0, 4.3, 1.9);
        if (!girl)
        {
            Oval(hair, -3.4, -31.2, 2.0, 2.2);
        }

        DrawFace(p);
        Arm(new Point(5, -22), front, hair, false);
        Pop();
        Pop();
    }

    private void Arm(Point shoulder, Point hand, Color hair, bool back)
    {
        _ = hair;
        var c = back ? MascotPalette.Scale(_p.Skin, 0.92) : _p.Skin;
        Line(c, 2.5, shoulder.X, shoulder.Y, hand.X, hand.Y);
        Oval(c, hand.X, hand.Y, 1.5, 1.5);
    }

    private void DrawFace(Pose p)
    {
        var ey = -28.6 + p.LookY;
        var blink = BlinkAmount();
        for (var i = -1; i <= 1; i += 2)
        {
            var ex = (i * 2.1) + 1.5 + p.LookX;
            switch (p.Eyes)
            {
                case EyeStyle.Happy:
                    Curve(Pn(_p.Eye, 1.1), new Point(ex - 1.3, ey + 0.8), new Point(ex, ey - 1.8), new Point(ex + 1.3, ey + 0.8));
                    break;
                case EyeStyle.Closed:
                    Curve(Pn(_p.Eye, 1.1), new Point(ex - 1.3, ey - 0.2), new Point(ex, ey + 1.5), new Point(ex + 1.3, ey - 0.2));
                    break;
                default:
                    Oval(_p.Eye, ex, ey, 0.95, Math.Max(0.25, 1.25 * (1 - blink)));
                    if (p.Eyes == EyeStyle.Worried)
                    {
                        Line(_p.Eye, 0.8, ex - (i * 1.5), ey - 2.0, ex + (i * 1.2), ey - 2.7);
                    }

                    break;
            }
        }

        Oval(_p.Cheek, -3.5 + (p.LookX * 0.4), -26.9, 1.1, 0.75);
        Oval(_p.Cheek, 3.9 + (p.LookX * 0.4), -26.9, 1.1, 0.75);
        var mx = 1.4 + (p.LookX * 0.7);
        switch (p.Mouth)
        {
            case MouthStyle.Smile:
                Curve(Pn(_p.Eye, 0.8), new Point(mx - 1.2, -26.6), new Point(mx, -25.1), new Point(mx + 1.2, -26.6));
                break;
            case MouthStyle.Open:
                Oval(_p.Eye, mx, -25.9, 0.9, 1.1);
                break;
            case MouthStyle.Frown:
                Curve(Pn(_p.Eye, 0.8), new Point(mx - 1.2, -25.4), new Point(mx, -26.8), new Point(mx + 1.2, -25.4));
                break;
            default:
                Line(_p.Eye, 0.8, mx - 1, -26, mx + 1, -26);
                break;
        }

        if (p.Sweat)
        {
            var f = Frac(_t * 0.9);
            Oval(Color.FromRgb(0x7F, 0xC8, 0xF5), 5.6, -32 + (f * 4), 0.9, 1.3);
        }
    }

    private double BlinkAmount()
    {
        var c = Frac(_t / 3.3);
        return c is > 0.93 and < 0.99 ? Math.Sin((c - 0.93) / 0.06 * Math.PI) : 0;
    }

    // ───────────────────────── 소품 ─────────────────────────

    private void Basketball(double cx, double cy, double rot = 0, double r = 4.2)
    {
        Push(cx, cy, rot);
        if (_minStroke > 0)
        {
            // 레트로: 해상도가 낮아 이음선을 다 그리면 공이 시커멓게 뭉개진다. 한 줄만.
            Oval(_p.Ball, 0, 0, r + 0.6, r + 0.6);
            Line(_p.BallSeam, 1, -r, 0, r, 0);
            Pop();
            return;
        }

        Oval(_p.Ball, 0, 0, r, r, Pn(_p.BallSeam, 0.7));
        Line(_p.BallSeam, 0.7, -r, 0, r, 0);
        Line(_p.BallSeam, 0.7, 0, -r, 0, r);
        Curve(Pn(_p.BallSeam, 0.7), new Point(-r * 0.62, -r * 0.78), new Point(-r * 0.12, 0), new Point(-r * 0.62, r * 0.78));
        Curve(Pn(_p.BallSeam, 0.7), new Point(r * 0.62, -r * 0.78), new Point(r * 0.12, 0), new Point(r * 0.62, r * 0.78));
        Pop();
    }

    private double HoopX => _w - 18;

    private double RimX => HoopX - 9;

    private const double RimY = 17.5;

    private void HoopBack()
    {
        Line(_p.MetalDark, 2, HoopX + 3.4, 10, HoopX + 3.4, Ground);
        RR(_p.Surface, HoopX - 1, 4.5, 2.8, 17, 1, Pn(_p.MetalDark, 0.9));
        RR(_p.Rim, HoopX - 1, 14.4, 2.8, 2.2, 0.6);
        Ring(_p.Rim, 1.6, RimX, RimY, 6.8, 1.6);
    }

    private void HoopFront(double wiggle)
    {
        var netCol = MascotPalette.Lerp(_p.Metal, _p.Surface, 0.15);
        for (var i = -3; i <= 3; i++)
        {
            Line(netCol, 0.8, RimX + (i * 2.1), RimY + 1, RimX + (i * 1.2) + (wiggle * (Math.Abs(i) + 1) * 0.15), RimY + 10);
        }

        Line(_p.Metal, 0.8, RimX - 5, RimY + 4.5, RimX + 5, RimY + 4.5);
        Line(_p.Metal, 0.8, RimX - 4, RimY + 7.5 + (wiggle * 0.1), RimX + 4, RimY + 7.5 + (wiggle * 0.1));
        Curve(Pn(_p.Rim, 1.8), new Point(RimX - 6.8, RimY), new Point(RimX, RimY + 3.6), new Point(RimX + 6.8, RimY));
    }

    private void Bin(double cx, double rock, Point? inside)
    {
        Push(0, 0, rock, 1, 1, cx, Ground);
        Oval(_p.MetalDark, cx, Ground - 17, 8.5, 2.3);
        if (inside is { } ip)
        {
            Basketball(ip.X, ip.Y, 30, 3.8);
        }

        Poly(_p.Metal, null, new Point(cx - 8.5, Ground - 17), new Point(cx + 8.5, Ground - 17), new Point(cx + 6.4, Ground), new Point(cx - 6.4, Ground));
        for (var i = -1; i <= 1; i++)
        {
            Line(_p.MetalDark, 0.9, cx + (i * 3.8), Ground - 14.5, cx + (i * 2.8), Ground - 2.5);
        }

        Line(_p.MetalDark, 1.8, cx - 8.5, Ground - 17, cx + 8.5, Ground - 17);
        Pop();
    }

    private void Sparkle(double cx, double cy, double r, Color c)
    {
        Poly(c, null, new Point(cx, cy - r), new Point(cx + (r * 0.28), cy - (r * 0.28)), new Point(cx + r, cy), new Point(cx + (r * 0.28), cy + (r * 0.28)), new Point(cx, cy + r), new Point(cx - (r * 0.28), cy + (r * 0.28)), new Point(cx - r, cy), new Point(cx - (r * 0.28), cy - (r * 0.28)));
    }

    private static readonly Color Gold = Color.FromRgb(0xF2, 0xC2, 0x33);

    // ───────────────────────── 말풍선 ─────────────────────────

    /// <summary>글꼴 없이 선으로 그리는 말풍선 글리프 (레트로에서도 또렷하도록).</summary>
    private void Glyph(char ch, double cx, double cy, double size, Color c)
    {
        var k = size / 12.0;
        var pen = Pn(c, Math.Max(1.9 * k, 1.6));
        Point P(double x, double y) => new(cx + (x * k), cy + (y * k));
        switch (ch)
        {
            case '?':
                {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(P(-3.2, -2.6), false, false);
                        ctx.PolyLineTo([P(-2.6, -4.9), P(0, -5.9), P(2.6, -4.8), P(3.1, -2.6), P(1.6, -0.5), P(0.1, 0.9), P(0.1, 2.0)], true, true);
                    }

                    g.Freeze();
                    _dc.DrawGeometry(null, pen, g);
                    Oval(c, cx + (0.1 * k), cy + (4.7 * k), Math.Max(1.15 * k, 0.8), Math.Max(1.15 * k, 0.8));
                    break;
                }

            case '!':
                Line(c, Math.Max(2.4 * k, 1.8), cx, cy - (5.8 * k), cx, cy + (1.2 * k));
                Oval(c, cx, cy + (4.7 * k), Math.Max(1.25 * k, 0.9), Math.Max(1.25 * k, 0.9));
                break;
            default:
                {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(P(-3, -3), false, false);
                        ctx.PolyLineTo([P(3, -3), P(-3, 3), P(3, 3)], true, true);
                    }

                    g.Freeze();
                    _dc.DrawGeometry(null, pen, g);
                    break;
                }
        }
    }

    private void DrawBubble()
    {
        switch (_f.Bubble)
        {
            case MascotBubble.Question:
            case MascotBubble.Exclaim:
                {
                    var bounce = Math.Sin(_t * 6) * 0.8;
                    var cx = Math.Clamp(_anchor.X + 13, 11, _w - 11);
                    var cy = Math.Max(_anchor.Y, 11) + bounce - 1;
                    var isQ = _f.Bubble == MascotBubble.Question;
                    var col = isQ ? _p.Accent : _p.Error;
                    Poly(_p.Surface, null, new Point(cx - 6, cy + 4), new Point(cx - 2, cy + 6), new Point(cx - 7, cy + 9));
                    RR(_p.Surface, cx - 8, cy - 7.5, 16, 14, 5.5, Pn(col, 1.3));
                    Poly(_p.Surface, null, new Point(cx - 6.2, cy + 4.5), new Point(cx - 2.4, cy + 5.6), new Point(cx - 6.4, cy + 8));
                    Glyph(isQ ? '?' : '!', cx, cy - 0.4, 12, col);
                    break;
                }

            case MascotBubble.Zzz:
                for (var i = 0; i < 3; i++)
                {
                    var ph = Frac((_t * 0.42) + (i / 3.0));
                    var a = Math.Sin(ph * Math.PI);
                    Glyph('z', _anchor.X + 9 + (ph * 10), _anchor.Y - (ph * 8), 7 + (ph * 5), Color.FromArgb((byte)(255 * Math.Clamp(a * 1.5, 0, 1)), _p.TextSecondary.R, _p.TextSecondary.G, _p.TextSecondary.B));
                }

                break;
            default:
                break;
        }
    }
}
