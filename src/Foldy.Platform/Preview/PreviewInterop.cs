using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Foldy.Platform.Preview;

// Windows 미리보기 처리기 COM 선언 (shobjidl_core.h, propsys.h). vtable 순서가 SDK와 같아야 한다.
// 이 파일 밖으로는 노출하지 않는다 (Win32 상호 운용은 Foldy.Platform 안에서만, 설계서 4장).
#pragma warning disable SYSLIB1096 // 고전 COM 상호 운용: UI(STA) 스레드에서만 쓴다
#pragma warning disable SYSLIB1054 // DllImport → LibraryImport 권고: 기존 셸 상호 운용 파일과 같은 방식 유지
#pragma warning disable CA1707, CA1712, IDE1006

[StructLayout(LayoutKind.Sequential)]
internal struct PreviewRect(int left, int top, int right, int bottom)
{
    public int Left = left;
    public int Top = top;
    public int Right = right;
    public int Bottom = bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PreviewMsg
{
    public IntPtr hwnd;
    public uint message;
    public UIntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public int ptX;
    public int ptY;
}

[ComImport]
[Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandler
{
    void SetWindow(IntPtr hwnd, ref PreviewRect prc);

    void SetRect(ref PreviewRect prc);

    void DoPreview();

    void Unload();

    void SetFocus();

    void QueryFocus(out IntPtr phwnd);

    [PreserveSig]
    int TranslateAccelerator(ref PreviewMsg pmsg);
}

[ComImport]
[Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithFile
{
    void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
}

[ComImport]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithStream
{
    void Initialize(IStream pstream, uint grfMode);
}

internal static class PreviewNative
{
    /// <summary>HKCR\.ext\ShellEx\{이 GUID} = 미리보기 처리기 CLSID.</summary>
    public const string PreviewHandlerShellExKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    public const uint ASSOCSTR_SHELLEXTENSION = 16;

    public const uint CLSCTX_INPROC_SERVER = 0x1;
    public const uint CLSCTX_LOCAL_SERVER = 0x4;

    public const uint STGM_READ = 0x0;
    public const uint STGM_SHARE_DENY_NONE = 0x40;

    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_CLIPCHILDREN = 0x02000000;

    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateStreamOnFileEx(
        string pszFile, uint grfMode, uint dwAttributes, [MarshalAs(UnmanagedType.Bool)] bool fCreate,
        IStream? pstmTemplate, out IStream ppstm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hwnd);
}
