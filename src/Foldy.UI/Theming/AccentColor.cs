using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Foldy.UI.Theming;

/// <summary>
/// Windows 강조색(설정 &gt; 개인 설정 &gt; 색)을 읽고, Brush.Accent/Focus/Progress/Selection 덮어쓰기 사전을 만든다.
/// WinRT(UISettings) 없이 레지스트리만 쓴다. 읽지 못하면 null → 호출자가 Foldy 오렌지로 둔다.
/// </summary>
public static class AccentColor
{
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    /// <summary>Foldy 기본 강조색 (Modern.*.xaml의 Brush.Accent와 같다).</summary>
    public static readonly Color FoldyOrange = Color.FromRgb(0xF2, 0x8C, 0x1B);

    /// <summary>현재 Windows 강조색. 레지스트리를 읽지 못하면 null.</summary>
    public static Color? ReadWindowsAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DwmKey);
            if (key?.GetValue("AccentColor") is int abgr)
            {
                return FromAbgr(unchecked((uint)abgr));
            }

            if (key?.GetValue("ColorizationColor") is int argb)
            {
                var v = unchecked((uint)argb);
                return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // 읽을 수 없으면 기본 강조색 사용
        }

        return null;
    }

    /// <summary>DWM AccentColor 값(0xAABBGGRR)을 색으로. 알파는 무시한다.</summary>
    public static Color FromAbgr(uint abgr) =>
        Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));

    /// <summary>accent 위에 올릴 글자색: 밝은 강조색이면 검정, 어두우면 흰색.</summary>
    public static Color TextOn(Color accent)
    {
        // WCAG 대비가 더 큰 쪽(흰색 / 거의 검정)을 고른다 (예전 밝기 0.6 기준은 주황 같은 중간 밝기에서 흰 글자가 2.5:1이었다).
        var dark = Color.FromRgb(0x1C, 0x1C, 0x1F);
        return Contrast(accent, dark) >= Contrast(accent, Colors.White) ? dark : Colors.White;
    }

    /// <summary>WCAG 2.1 상대 휘도 대비 (1~21).</summary>
    public static double Contrast(Color a, Color b)
    {
        var (x, y) = (RelativeLuminance(a), RelativeLuminance(b));
        if (x < y)
        {
            (x, y) = (y, x);
        }

        return (x + 0.05) / (y + 0.05);
    }

    /// <summary>fg를 bg와 min:1 이상 대비가 나올 때까지 어둡게(밝은 배경) 또는 밝게(어두운 배경) 만든다. 포커스 링(3:1)용.</summary>
    public static Color EnsureContrast(Color fg, Color bg, double min)
    {
        var target = RelativeLuminance(bg) > 0.5 ? Colors.Black : Colors.White;
        var c = fg;
        for (var t = 0.0; t <= 1.0 && Contrast(c, bg) < min; t += 0.05)
        {
            c = Mix(fg, target, t);
        }

        return c;
    }

    private static double RelativeLuminance(Color c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Lin(c.R)) + (0.7152 * Lin(c.G)) + (0.0722 * Lin(c.B));
    }

    /// <summary>선택 배경: 강조색을 창 배경에 옅게 섞은 색.</summary>
    public static Color SelectionTint(Color accent, bool dark) =>
        dark ? Mix(Color.FromRgb(0x20, 0x20, 0x24), accent, 0.35) : Mix(Colors.White, accent, 0.18);

    /// <summary>
    /// Brush.Accent, Brush.AccentText, Brush.Focus, Brush.Progress, Brush.Selection을 덮어쓰는 사전.
    /// 병합 사전 목록 맨 뒤에 두면 토큰을 DynamicResource로 쓰는 화면이 바로 따라온다.
    /// </summary>
    public static ResourceDictionary CreateOverrides(Color accent, bool dark)
    {
        var rd = new ResourceDictionary();
        rd["Brush.Accent"] = Frozen(accent);
        rd["Brush.AccentText"] = Frozen(TextOn(accent));
        // 포커스 링은 창 배경과 3:1 이상(WCAG 1.4.11)이 되도록 필요하면 어둡게/밝게 보정한다.
        rd["Brush.Focus"] = Frozen(EnsureContrast(accent, dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White, 3.0));
        rd["Brush.Progress"] = Frozen(accent);
        rd["Brush.Selection"] = Frozen(SelectionTint(accent, dark));
        return rd;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + ((b.R - a.R) * t)),
        (byte)Math.Round(a.G + ((b.G - a.G) * t)),
        (byte)Math.Round(a.B + ((b.B - a.B) * t)));
}
