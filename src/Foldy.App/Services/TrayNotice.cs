using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Foldy.App.Services;

/// <summary>
/// Windows 알림 한 번 (알림 영역 풍선 → Windows 10·11에서는 오른쪽 아래 알림으로 보인다).
/// 뒤에서 하던 일이 끝났을 때만 쓴다. 알림을 누르면 <c>onClick</c>, 사라지면 알림 영역 아이콘도 지운다.
/// 설치 방식(MSI·Store·포터블)과 상관없이 동작하도록 Shell_NotifyIcon만 쓴다.
/// </summary>
internal sealed class TrayNotice : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 0x4F;
    private const int NinBalloonHide = 0x0403;
    private const int NinBalloonTimeout = 0x0404;
    private const int NinBalloonUserClick = 0x0405;
    private const int WmLButtonUp = 0x0202;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    private const uint NiifInfo = 0x1;

    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private readonly Action? _onClick;
    private bool _disposed;

    private TrayNotice(Action? onClick)
    {
        _onClick = onClick;
        _window = new HwndSource(new HwndSourceParameters("FoldyNotice") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(Hook);
        _icon = LoadAppIcon();
    }

    /// <summary>알림을 띄운다. 실패하면(알림이 꺼져 있는 등) 조용히 넘어간다 — 앱 안 작업 창에도 늘 같은 안내가 있다.</summary>
    public static void Show(string title, string text, Action? onClick)
    {
        TrayNotice? n = null;
        try
        {
            n = new TrayNotice(onClick);
            if (!n.Add(title, text))
            {
                n.Dispose();
                return;
            }

            // 알림이 닫히지 않고 남아 있어도 1분 뒤에는 아이콘을 지운다
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                n.Dispose();
            };
            timer.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ExternalException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            n?.Dispose();
        }
    }

    private bool Add(string title, string text)
    {
        var data = NewData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.szTip = Clip(title, 127);
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            return false;
        }

        data = NewData();
        data.uFlags = NifInfo;
        data.szInfoTitle = Clip(title, 63);
        data.szInfo = Clip(text, 255);
        data.dwInfoFlags = NiifInfo;
        return ShellNotifyIcon(NimModify, ref data);
    }

    private NotifyIconData NewData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = 1,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != CallbackMessage)
        {
            return IntPtr.Zero;
        }

        switch ((int)((long)lParam & 0xFFFF))
        {
            case NinBalloonUserClick:
            case WmLButtonUp:
                handled = true;
                _onClick?.Invoke();
                DisposeLater();
                break;
            case NinBalloonTimeout:
            case NinBalloonHide:
                handled = true;
                DisposeLater();
                break;
        }

        return IntPtr.Zero;
    }

    // 창 메시지를 처리하는 도중에는 그 창을 없애지 않는다
    private void DisposeLater() => _window.Dispatcher.BeginInvoke(() => Dispose());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            var data = NewData();
            ShellNotifyIcon(NimDelete, ref data);
        }
        finally
        {
            if (_icon != IntPtr.Zero)
            {
                DestroyIcon(_icon);
            }

            _window.RemoveHook(Hook);
            _window.Dispose();
        }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static IntPtr LoadAppIcon()
    {
        var small = IntPtr.Zero;
        if (Environment.ProcessPath is { } exe)
        {
            _ = ExtractIconEx(exe, 0, IntPtr.Zero, ref small, 1);
        }

        return small;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr large, ref IntPtr small, uint icons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
