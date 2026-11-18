using System.Windows;
using System.Windows.Media;

namespace Foldy.UI.Mascot;

/// <summary>
/// 마스코트가 쓰는 색 모음. 테마 토큰(Brush.Accent 등)에서 읽고, 없으면 밝은 테마 값으로 대체한다.
/// 연주의 유니폼은 앱 강조색(모던) 또는 고정 주황(레트로, Accent가 남색이라), 우진은 파랑.
/// 캐릭터 색(피부·머리·공)은 어느 테마에서도 같다.
/// </summary>
public sealed class MascotPalette
{
    public static readonly Color FoldyOrange = Color.FromRgb(0xF2, 0x8C, 0x1B);

    /// <summary>연주 유니폼.</summary>
    public Color Body { get; init; } = FoldyOrange;

    public Color BodyDark { get; init; } = Color.FromRgb(0xC9, 0x6A, 0x0B);

    /// <summary>우진 유니폼.</summary>
    public Color Jersey2 { get; init; } = Color.FromRgb(0x3F, 0x7F, 0xE0);

    public Color Jersey2Dark { get; init; } = Color.FromRgb(0x2B, 0x5C, 0xAA);

    public Color Skin { get; init; } = Color.FromRgb(0xF7, 0xD2, 0xB0);

    public Color HairGirl { get; init; } = Color.FromRgb(0x5A, 0x34, 0x24);

    public Color HairBoy { get; init; } = Color.FromRgb(0x2A, 0x25, 0x2E);

    public Color Eye { get; init; } = Color.FromRgb(0x2B, 0x1B, 0x12);

    public Color Cheek { get; init; } = Color.FromArgb(0x90, 0xFF, 0x7A, 0x7A);

    public Color Ball { get; init; } = Color.FromRgb(0xE9, 0x73, 0x1F);

    public Color BallSeam { get; init; } = Color.FromRgb(0x3A, 0x24, 0x12);

    public Color Rim { get; init; } = Color.FromRgb(0xE2, 0x56, 0x2B);

    public Color Metal { get; init; } = Color.FromRgb(0x8E, 0x95, 0xA3);

    public Color MetalDark { get; init; } = Color.FromRgb(0x5B, 0x62, 0x70);

    public Color Accent { get; init; } = FoldyOrange;

    public Color Text { get; init; } = Color.FromRgb(0x1C, 0x1C, 0x1F);

    public Color TextSecondary { get; init; } = Color.FromRgb(0x6B, 0x6B, 0x73);

    public Color Surface { get; init; } = Color.FromRgb(0xFF, 0xFF, 0xFF);

    public Color Border { get; init; } = Color.FromRgb(0xCF, 0xCF, 0xD4);

    public Color Error { get; init; } = Color.FromRgb(0xC4, 0x2B, 0x1C);

    public Color Done { get; init; } = Color.FromRgb(0x0F, 0x7B, 0x0F);

    /// <summary>밝은 테마 기본값.</summary>
    public static MascotPalette Default { get; } = new();

    /// <summary>리소스 범위(보통 뷰 자신)에서 토큰을 읽어 팔레트를 만든다.</summary>
    public static MascotPalette Resolve(FrameworkElement? scope, bool retro)
    {
        if (scope is null)
        {
            return Default;
        }

        var accent = Read(scope, "Brush.Accent", FoldyOrange);
        var body = retro ? FoldyOrange : accent;
        return new MascotPalette
        {
            Body = body,
            BodyDark = Scale(body, 0.78),
            Accent = accent,
            Text = Read(scope, "Brush.Text", Default.Text),
            TextSecondary = Read(scope, "Brush.TextSecondary", Default.TextSecondary),
            Surface = Read(scope, "Brush.SurfaceAlt", Default.Surface),
            Border = Read(scope, "Brush.BorderStrong", Default.Border),
            Error = Read(scope, "Brush.Error", Default.Error),
            Done = Read(scope, "Brush.Done", Default.Done),
        };
    }

    private static Color Read(FrameworkElement scope, string key, Color fallback) =>
        scope.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    public static Color Scale(Color c, double k) =>
        Color.FromArgb(c.A, (byte)Math.Clamp(c.R * k, 0, 255), (byte)Math.Clamp(c.G * k, 0, 255), (byte)Math.Clamp(c.B * k, 0, 255));

    public static Color Lerp(Color a, Color b, double t) =>
        Color.FromArgb(
            (byte)(a.A + ((b.A - a.A) * t)),
            (byte)(a.R + ((b.R - a.R) * t)),
            (byte)(a.G + ((b.G - a.G) * t)),
            (byte)(a.B + ((b.B - a.B) * t)));
}
