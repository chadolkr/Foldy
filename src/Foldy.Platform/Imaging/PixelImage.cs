namespace Foldy.Platform.Imaging;

/// <summary>썸네일·미리보기를 어디서 얻었는지.</summary>
public enum ThumbnailSource : byte
{
    /// <summary>Windows 셸 썸네일 캐시 (IShellItemImageFactory).</summary>
    ShellCache,

    /// <summary>JPEG EXIF에 내장된 작은 썸네일.</summary>
    ExifEmbedded,

    /// <summary>WIC로 원본을 필요한 크기로 축소 디코딩.</summary>
    WicDecode,
}

/// <summary>무엇을 위해 불러오는지. 미리보기는 큰 이미지가 필요하고 EXIF 썸네일을 쓰지 않는다.</summary>
public enum ImageLoadPurpose : byte
{
    Thumbnail,
    Preview,
}

/// <summary>
/// 디코딩 결과: 32bpp 미리 곱한 BGRA(PBGRA) 픽셀, 위에서 아래 순서.
/// WPF 쪽에서 BitmapSource.Create(..., PixelFormats.Pbgra32, ...)로 바로 감싼다 (Platform은 WPF를 모른다).
/// </summary>
public sealed class PixelImage
{
    public PixelImage(int width, int height, byte[] pixels, ThumbnailSource source, int sourceWidth, int sourceHeight)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4)
        {
            throw new ArgumentException("픽셀 버퍼 크기가 맞지 않습니다.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
        Source = source;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

#pragma warning disable CA1819 // 복사 없이 BitmapSource.Create에 넘기기 위해 배열을 그대로 노출
    public byte[] Pixels { get; }
#pragma warning restore CA1819

    public ThumbnailSource Source { get; }

    /// <summary>원본 해상도 (방향 적용 후). 모르면 0 (셸 캐시).</summary>
    public int SourceWidth { get; }

    public int SourceHeight { get; }

    /// <summary>메모리 캐시 계산용 바이트 수.</summary>
    public long ByteSize => (long)Width * Height * 4;
}

/// <summary>썸네일 요청 옵션.</summary>
public readonly record struct ImageLoadOptions
{
    /// <summary>
    /// false면 셸 캐시만 쓴다 (네트워크 드라이브: 설계서 5.12 "자동 생성 끔").
    /// 클라우드 전용 파일은 이 값과 관계없이 항상 셸 캐시만.
    /// </summary>
    public bool AllowContentRead { get; init; }

    public ImageLoadPurpose Purpose { get; init; }

    /// <summary>미리보기처럼 사용자가 기다리는 요청은 썸네일보다 먼저 처리한다.</summary>
    public bool HighPriority { get; init; }

    /// <summary>EXIF 썸네일을 먼저 보여 줄 때 부른다 (작업 스레드에서). 최종 결과는 따로 돌아온다.</summary>
    public Action<PixelImage>? Preliminary { get; init; }

    public static ImageLoadOptions Default => new() { AllowContentRead = true };
}
