using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Foldy.Platform.Imaging;

namespace Foldy.UI.Imaging;

/// <summary>
/// 썸네일 공급 순서를 묶는다: 메모리 캐시 → 디스크 캐시 → <see cref="ThumbnailService"/>(셸 캐시 → EXIF → WIC).
/// 결과는 Freeze한 BitmapSource라 UI 스레드로 그대로 넘긴다. 스레드 안전.
/// </summary>
public sealed class ThumbnailLoader : IDisposable
{
    private static readonly Lazy<ThumbnailLoader> SharedInstance = new(() =>
        new ThumbnailLoader(ThumbnailService.Shared, new ThumbnailMemoryCache(), DiskThumbnailCache.TryOpen(), ownsService: false));

    private readonly bool _ownsService;

    public ThumbnailLoader(ThumbnailService service, ThumbnailMemoryCache memory, DiskThumbnailCache? disk = null, bool ownsService = false)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Disk = disk;
        _ownsService = ownsService;
    }

    /// <summary>앱 전체 공용 (디스크 캐시는 %LOCALAPPDATA%\Foldy\thumbs.cache, 열 수 없으면 없이).</summary>
    public static ThumbnailLoader Shared => SharedInstance.Value;

    public ThumbnailService Service { get; }

    public ThumbnailMemoryCache Memory { get; }

    public DiskThumbnailCache? Disk { get; }

    /// <summary>메모리 캐시만 즉시 확인 (UI 스레드에서 그릴 때).</summary>
    public BitmapSource? TryGetCached(ThumbKey key) => Memory.TryGet(key, out var img) ? img : null;

    /// <summary>
    /// 썸네일을 불러온다. 만들 수 없으면 null. <paramref name="preliminary"/>는 EXIF 썸네일처럼 먼저 보여 줄 작은 이미지
    /// (아무 스레드에서나 불린다).
    /// </summary>
    public async Task<BitmapSource?> GetThumbnailAsync(ImageItemInfo item, int pixelSize, bool allowContentRead, Action<BitmapSource>? preliminary, CancellationToken ct)
    {
        var key = item.KeyFor(pixelSize);
        if (Memory.TryGet(key, out var hit))
        {
            return hit;
        }

        if (Disk is { } disk && disk.Contains(key))
        {
            var fromDisk = await Task.Run(() => disk.TryGet(key), ct).ConfigureAwait(false);
            if (fromDisk is not null)
            {
                Memory.Add(key, fromDisk);
                return fromDisk;
            }
        }

        ct.ThrowIfCancellationRequested();
        var options = new ImageLoadOptions
        {
            AllowContentRead = allowContentRead,
            Purpose = ImageLoadPurpose.Thumbnail,
            Preliminary = preliminary is null ? null : p => preliminary(ToBitmap(p)),
        };

        var px = await Service.LoadAsync(item.Path, pixelSize, options, ct).ConfigureAwait(false);
        if (px is null)
        {
            return null;
        }

        var bmp = ToBitmap(px);
        Memory.Add(key, bmp);
        if (Disk is { } d && px.Source != ThumbnailSource.ShellCache)
        {
            // 셸 캐시에서 온 것은 Windows가 이미 보관한다. 직접 만든 것만 저장.
            _ = Task.Run(() => SafePut(d, key, bmp), CancellationToken.None);
        }

        return bmp;
    }

    /// <summary>미리보기용 큰 이미지 (긴 변 maxEdge, 0 = 원본). 캐시하지 않는다.</summary>
    public async Task<(BitmapSource? Image, int SourceWidth, int SourceHeight)> GetPreviewAsync(ImageItemInfo item, int maxEdge, bool allowContentRead, CancellationToken ct)
    {
        var options = new ImageLoadOptions { AllowContentRead = allowContentRead, Purpose = ImageLoadPurpose.Preview, HighPriority = true };
        var px = await Service.LoadAsync(item.Path, maxEdge, options, ct).ConfigureAwait(false);
        return px is null ? (null, 0, 0) : (ToBitmap(px), px.SourceWidth, px.SourceHeight);
    }

    /// <summary>파일이 바뀌거나 지워졌을 때 메모리 캐시에서 뺀다 (디스크는 키에 수정 시각이 있어 저절로 무효).</summary>
    public void Invalidate(string path) => Memory.Invalidate(path);

    /// <summary>Platform의 PBGRA 픽셀 → Freeze한 BitmapSource.</summary>
    public static BitmapSource ToBitmap(PixelImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null, image.Pixels, image.Stride);
        bmp.Freeze();
        return bmp;
    }

    public void Dispose()
    {
        Disk?.Dispose();
        if (_ownsService)
        {
            Service.Dispose();
        }
    }

    private static void SafePut(DiskThumbnailCache disk, ThumbKey key, BitmapSource bmp)
    {
        try
        {
            disk.Put(key, bmp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ObjectDisposedException)
        {
            // 디스크 캐시는 있으면 좋은 것: 실패해도 무시
        }
    }
}
