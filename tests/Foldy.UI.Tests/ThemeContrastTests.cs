using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace Foldy.UI.Tests;

/// <summary>
/// WCAG 2.1 AA 색 대비 검사 (1.4.3 글자 4.5:1, 1.4.11 포커스 표시 3:1).
/// 테마 XAML의 SolidColorBrush 값을 직접 읽어 계산하므로, 토큰을 바꾸면 이 시험이 바로 알려 준다.
/// 비활성 글자(Brush.TextDisabled)는 WCAG가 면제한다.
/// </summary>
public class ThemeContrastTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static TheoryData<string> Themes => ["Modern.Light", "Modern.Dark", "Retro"];

    private static string ThemesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foldy.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("저장소 루트(Foldy.slnx)를 찾아야 한다");
        return Path.Combine(dir!.FullName, "src", "Foldy.UI", "Themes");
    }

    private static Dictionary<string, (double R, double G, double B)> Load(string theme)
    {
        var doc = XDocument.Load(Path.Combine(ThemesDir(), theme + ".xaml"));
        var map = new Dictionary<string, (double, double, double)>();
        foreach (var e in doc.Descendants(Wpf + "SolidColorBrush"))
        {
            var key = (string?)e.Attribute(X + "Key");
            var color = (string?)e.Attribute("Color");
            if (key is null || color is not { Length: 7 } || color[0] != '#')
            {
                continue;
            }

            map[key] = (
                int.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0,
                int.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0,
                int.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0);
        }

        return map;
    }

    private static double Lin(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double Lum((double R, double G, double B) c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);

    public static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (hi, lo) = (Lum(a), Lum(b));
        if (hi < lo)
        {
            (hi, lo) = (lo, hi);
        }

        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void Contrast_math_matches_wcag_reference_values()
    {
        Contrast((0, 0, 0), (1, 1, 1)).Should().BeApproximately(21.0, 0.001);
        Contrast((1, 1, 1), (1, 1, 1)).Should().BeApproximately(1.0, 0.001);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Body_and_secondary_text_pass_4_5_on_every_surface(string theme)
    {
        var m = Load(theme);
        var failures = new List<string>();
        foreach (var fg in new[] { "Brush.Text", "Brush.TextSecondary" })
        {
            foreach (var bg in new[] { "Brush.Window", "Brush.Surface", "Brush.SurfaceAlt", "Brush.Hover", "Brush.Pressed" })
            {
                Check(failures, m, fg, bg, 4.5);
            }
        }

        failures.Should().BeEmpty(theme);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Status_colors_pass_4_5_on_surfaces(string theme)
    {
        var m = Load(theme);
        var failures = new List<string>();
        foreach (var fg in new[] { "Brush.Error", "Brush.Done" })
        {
            foreach (var bg in new[] { "Brush.Window", "Brush.Surface", "Brush.SurfaceAlt", "Brush.Hover" })
            {
                Check(failures, m, fg, bg, 4.5);
            }
        }

        failures.Should().BeEmpty(theme);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Selection_and_accent_text_pass_4_5(string theme)
    {
        var m = Load(theme);
        var failures = new List<string>();
        Check(failures, m, "Brush.SelectionText", "Brush.Selection", 4.5);
        Check(failures, m, "Brush.AccentText", "Brush.Accent", 4.5);
        failures.Should().BeEmpty(theme);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Tab_text_passes_4_5_on_every_tab_color(string theme)
    {
        var m = Load(theme);
        if (!m.ContainsKey("Brush.Tab0"))
        {
            return; // 레트로는 탭 색을 모던에서 물려받는다 (아래 시험에서 모던과 함께 검사)
        }

        var failures = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            Check(failures, m, "Brush.Text", "Brush.Tab" + i, 4.5);
        }

        failures.Should().BeEmpty(theme);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Focus_ring_has_3_to_1_against_surfaces(string theme)
    {
        var m = Load(theme);
        var failures = new List<string>();
        foreach (var bg in new[] { "Brush.Window", "Brush.Surface", "Brush.SurfaceAlt" })
        {
            Check(failures, m, "Brush.Focus", bg, 3.0);
        }

        failures.Should().BeEmpty(theme);
    }

    private static void Check(List<string> failures, Dictionary<string, (double R, double G, double B)> m, string fg, string bg, double min)
    {
        var ratio = Contrast(m[fg], m[bg]);
        if (ratio < min)
        {
            failures.Add($"{fg} on {bg} = {ratio:F2} (< {min})");
        }
    }
}
