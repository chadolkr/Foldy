namespace Foldy.Platform.Preview;

/// <summary>
/// 미리보기 처리기를 올릴 자식 창(HWND) 만들기·없애기. WPF의 HwndHost가 BuildWindowCore/DestroyWindowCore에서 부른다
/// (Win32 호출은 Foldy.Platform 안에서만).
/// </summary>
public static class PreviewHostWindow
{
    /// <summary>부모 창 안에 빈 자식 창을 만든다. 실패하면 <see cref="IntPtr.Zero"/>.</summary>
    public static IntPtr Create(IntPtr parent, int width, int height) =>
        PreviewNative.CreateWindowExW(
            0, "static", null,
            PreviewNative.WS_CHILD | PreviewNative.WS_VISIBLE | PreviewNative.WS_CLIPCHILDREN | PreviewNative.WS_CLIPSIBLINGS,
            0, 0, Math.Max(1, width), Math.Max(1, height), parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    public static void Destroy(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && PreviewNative.IsWindow(hwnd))
        {
            PreviewNative.DestroyWindow(hwnd);
        }
    }
}
