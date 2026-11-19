using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Foldy.Platform.Shell;

namespace Foldy.UI.Shell;

/// <summary>
/// 터미널 버튼에 Windows 원래 아이콘을 쓴다: cmd.exe·PowerShell 실행 파일의 아이콘, 관리자는 표준 UAC 방패를 겹친다.
/// 아이콘을 못 얻으면 null (호출자는 기존 글꼴 아이콘을 그대로 둔다).
/// </summary>
public static class SystemToolIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint Size;
        public IntPtr Icon;
        public int SysImageIndex;
        public int IconIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Path;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetStockIconInfo(uint siid, uint flags, ref StockIconInfo info);

    private const uint SiidShield = 77;
    private const uint ShgsiIcon = 0x100;
    private const uint ShgsiSmallIcon = 0x1;

    public static ImageSource? Exe(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            return null;
        }

        return FromHIcon(ShellInfo.FileIcon(exePath));
    }

    public static ImageSource? Shield()
    {
        var info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>() };
        return SHGetStockIconInfo(SiidShield, ShgsiIcon | ShgsiSmallIcon, ref info) == 0 ? FromHIcon(info.Icon) : null;
    }

    /// <summary>버튼 내용: 프로그램 아이콘(16px), 관리자면 오른쪽 아래에 작은 방패.</summary>
    public static UIElement? Content(string? exePath, bool admin)
    {
        if (Exe(exePath) is not { } icon)
        {
            return null;
        }

        var grid = new Grid { Width = 18, Height = 18 };
        grid.Children.Add(new Image { Source = icon, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (admin && Shield() is { } shield)
        {
            grid.Children.Add(new Image { Source = shield, Width = 10, Height = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
        }

        RenderOptions.SetBitmapScalingMode(grid, BitmapScalingMode.HighQuality);
        return grid;
    }

    private static BitmapSource? FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            ShellInfo.DestroyIcon(hIcon);
        }
    }
}
