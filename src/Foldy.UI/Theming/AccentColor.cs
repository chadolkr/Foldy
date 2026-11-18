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
    public static Color TextOn(Color accent) => Luminance(accent) > 0.6 ? Color.FromRgb(0x1C, 0x1C, 0x1F) : Colors.White;

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
        rd["Brush.Focus"] = Frozen(accent);
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

    private static double Luminance(Color c) => ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255.0;

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + ((b.R - a.R) * t)),
        (byte)Math.Round(a.G + ((b.G - a.G) * t)),
        (byte)Math.Round(a.B + ((b.B - a.B) * t)));
}
