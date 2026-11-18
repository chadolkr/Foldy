using System.Runtime.InteropServices;

namespace Foldy.Platform.Imaging;

// WIC·셸 썸네일 COM 선언. vtable 순서가 중요하므로 쓰지 않는 메서드도 자리만 남긴다.
// 이 파일 밖으로는 노출하지 않는다 (Win32 상호 운용은 Foldy.Platform 안에서만).
#pragma warning disable SYSLIB1096 // 고전 COM 상호 운용: 전용 STA 스레드에서만 쓴다

[ComImport]
[Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICImagingFactory
{
    [PreserveSig]
    int CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string fileName, IntPtr vendor, uint desiredAccess, int metadataOptions, out IWICBitmapDecoder decoder);

    [PreserveSig]
    int CreateDecoderFromStream(IntPtr stream, IntPtr vendor, int metadataOptions, out IWICBitmapDecoder decoder);

    [PreserveSig]
    int CreateDecoderFromFileHandle(IntPtr fileHandle, IntPtr vendor, int metadataOptions, out IWICBitmapDecoder decoder);

    void CreateComponentInfo();

    void CreateDecoder();

    void CreateEncoder();

    void CreatePalette();

    [PreserveSig]
    int CreateFormatConverter(out IWICFormatConverter converter);

    [PreserveSig]
    int CreateBitmapScaler(out IWICBitmapScaler scaler);

    void CreateBitmapClipper();

    [PreserveSig]
    int CreateBitmapFlipRotator(out IWICBitmapFlipRotator rotator);
}

[ComImport]
[Guid("9EDDE9E7-8DEE-47ea-99DF-E6FAF2ED44BF")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapDecoder
{
    void QueryCapability();

    void Initialize();

    void GetContainerFormat();

    void GetDecoderInfo();

    void CopyPalette();

    void GetMetadataQueryReader();

    void GetPreview();

    void GetColorContexts();

    void GetThumbnail();

    [PreserveSig]
    int GetFrameCount(out uint count);

    [PreserveSig]
    int GetFrame(uint index, out IWICBitmapSource frame);
}

[ComImport]
[Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapSource
{
    [PreserveSig]
    int GetSize(out uint width, out uint height);

    void GetPixelFormat();

    void GetResolution();

    void CopyPalette();

    [PreserveSig]
    int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
}

[ComImport]
[Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapScaler
{
    [PreserveSig]
    int GetSize(out uint width, out uint height);

    void GetPixelFormat();

    void GetResolution();

    void CopyPalette();

    [PreserveSig]
    int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);

    [PreserveSig]
    int Initialize(IWICBitmapSource source, uint width, uint height, int interpolationMode);
}

[ComImport]
[Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICFormatConverter
{
    [PreserveSig]
    int GetSize(out uint width, out uint height);

    void GetPixelFormat();

    void GetResolution();

    void CopyPalette();

    [PreserveSig]
    int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);

    [PreserveSig]
    int Initialize(IWICBitmapSource source, in Guid dstFormat, int dither, IntPtr palette, double alphaThreshold, int paletteTranslate);
}

[ComImport]
[Guid("5009834F-2D6A-41ce-9E1B-17C5AFF7A782")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapFlipRotator
{
    [PreserveSig]
    int GetSize(out uint width, out uint height);

    void GetPixelFormat();

    void GetResolution();

    void CopyPalette();

    [PreserveSig]
    int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);

    [PreserveSig]
    int Initialize(IWICBitmapSource source, int options);
}

[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(NativeSize size, int flags, out IntPtr hbitmap);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSize
{
    public int Cx;
    public int Cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeBitmap
{
    public int Type;
    public int Width;
    public int Height;
    public int WidthBytes;
    public ushort Planes;
    public ushort BitsPixel;
    public IntPtr Bits;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader
{
    public uint Size;
    public int Width;
    public int Height;
    public ushort Planes;
    public ushort BitCount;
    public uint Compression;
    public uint SizeImage;
    public int XPelsPerMeter;
    public int YPelsPerMeter;
    public uint ClrUsed;
    public uint ClrImportant;
}

#pragma warning restore SYSLIB1096

internal static partial class ImagingNative
{
    public const int SiigbfResizeToFit = 0x0;
    public const int SiigbfThumbnailOnly = 0x8;
    public const int SiigbfInCacheOnly = 0x10;

    public const int WicDecodeMetadataCacheOnDemand = 0;
    public const int WicInterpolationFant = 3;
    public const int WicDitherNone = 0;
    public const int WicPaletteCustom = 0;

    public static readonly Guid ClsidWicImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    public static readonly Guid Pbgra32 = new("6fddc324-4e03-4bfe-b185-3d77768dc910");
    public static readonly Guid IidShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateItemFromParsingName(string path, IntPtr bindCtx, in Guid riid, out IntPtr ppv);

    [LibraryImport("shlwapi.dll", EntryPoint = "SHCreateMemStream")]
    public static unsafe partial IntPtr SHCreateMemStream(byte* init, uint size);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static partial int GetObject(IntPtr h, int size, out NativeBitmap bitmap);

    [LibraryImport("gdi32.dll")]
    public static unsafe partial int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte* bits, ref BitmapInfoHeader info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr h);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr hwnd, IntPtr hdc);
}
