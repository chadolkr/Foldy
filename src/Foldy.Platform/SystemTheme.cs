using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Foldy.Platform;

/// <summary>Windows 앱 테마(다크/라이트)와 창 제목 표시줄 색 연동.</summary>
public static partial class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaSystemBackdropType = 38;

    /// <summary>Windows 설정 &gt; 개인 설정 &gt; 색 &gt; "앱 모드"가 다크인지. 값을 읽지 못하면 라이트.</summary>
    public static bool AppsUseDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>고대비 모드에서는 Foldy 테마 대신 시스템 색을 존중해야 한다.</summary>
    public static bool HighContrast => SystemParametersHighContrast();

    private static bool SystemParametersHighContrast()
    {
        var hc = new HighContrastInfo { Size = (uint)Marshal.SizeOf<HighContrastInfo>() };
        return SystemParametersInfoW(0x0042 /* SPI_GETHIGHCONTRAST */, hc.Size, ref hc, 0) && (hc.Flags & 1) != 0;
    }

    /// <summary>제목 표시줄을 다크/라이트로 맞춘다. 지원하지 않는 Windows에서는 조용히 무시.</summary>
    public static void ApplyTitleBar(IntPtr hwnd, bool dark, uint? captionColorBgr = null)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var v = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref v, sizeof(int));
        if (captionColorBgr is { } c)
        {
            var color = unchecked((int)c);
            _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref color, sizeof(int));
        }
        else
        {
            var defaultColor = unchecked((int)0xFFFFFFFF); // DWMWA_COLOR_DEFAULT
            _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref defaultColor, sizeof(int));
        }
    }

    /// <summary>Windows 11 배경 효과 (1 = 없음, 2 = Mica). 레트로 테마에서는 끈다.</summary>
    public static void ApplyBackdrop(IntPtr hwnd, int type)
    {
        if (hwnd != IntPtr.Zero)
        {
            _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref type, sizeof(int));
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint param, ref HighContrastInfo info, uint winIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }
}
