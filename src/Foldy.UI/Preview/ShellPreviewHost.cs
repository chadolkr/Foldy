using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Foldy.Platform.Preview;

namespace Foldy.UI.Preview;

/// <summary>
/// Windows 미리보기 처리기(PDF, Office 등)를 올리는 HwndHost. 처리기 창은 이 호스트의 자식 HWND 안에 그려진다.
/// 모든 COM 호출은 Foldy.Platform의 <see cref="PreviewHandlerSession"/>이 격리한다 (실패 → <see cref="Failed"/>).
/// 스레드 소유권: UI(STA) 스레드. 처리기를 별도 STA 스레드로 옮기는 것은 후속 과제 (docs/preview.md).
/// 주의: HwndHost는 WPF 요소 위에 겹쳐 그려지므로(에어스페이스) 팝업·툴팁이 이 영역 뒤로 갈 수 있다.
/// </summary>
public sealed class ShellPreviewHost : HwndHost
{
    private IntPtr _hwnd;
    private PreviewHandlerSession? _session;
    private string? _pendingPath;

    /// <summary>처리기가 실패해 세션을 닫았다. 인수는 상태 줄에 쓸 문구.</summary>
    public event EventHandler<string>? Failed;

    public bool HasSession => _session is not null;

    /// <summary>파일을 띄운다. 창이 아직 만들어지기 전이면 만들어진 뒤에 띄운다.</summary>
    public void Open(string path)
    {
        Clear();
        _pendingPath = path;
        if (_hwnd != IntPtr.Zero)
        {
            OpenPending();
        }
    }

    /// <summary>처리기를 Unload하고 놓는다.</summary>
    public void Clear()
    {
        _pendingPath = null;
        var s = _session;
        _session = null;
        s?.Dispose();
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var (w, h) = PixelSize();
        _hwnd = PreviewHostWindow.Create(hwndParent.Handle, w, h);
        if (_hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        OpenPending();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Clear();
        PreviewHostWindow.Destroy(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        _session?.SetRect((int)rcBoundingBox.Width, (int)rcBoundingBox.Height);
    }

    protected override void OnGotKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _session?.Focus();
    }

    private void OpenPending()
    {
        if (_pendingPath is not { } path)
        {
            return;
        }

        _pendingPath = null;
        var (w, h) = PixelSize();
        try
        {
            _session = PreviewHandlerSession.Open(path, _hwnd, w, h);
        }
        catch (PreviewHandlerException)
        {
            // 처리기 실패는 상태 줄로 알린다 (세션은 이미 정리됨, 블랙리스트 기록됨).
            _session = null;
            Failed?.Invoke(this, Foldy.Presentation.Preview.PreviewMessages.Failed);
        }
    }

    private (int W, int H) PixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return ((int)Math.Max(1, ActualWidth * dpi.DpiScaleX), (int)Math.Max(1, ActualHeight * dpi.DpiScaleY));
    }
}
