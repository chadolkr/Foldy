using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Foldy.Platform.Imaging;

namespace Foldy.UI.Imaging;

/// <summary>미리보기 아래 정보 줄 (해상도·파일 크기·촬영일·카메라).</summary>
public static class ImageInfoText
{
    public static string Format(ImageItemInfo item, int width, int height, ExifInfo? exif)
    {
        var parts = new List<string>(6);
        if (exif is { Width: > 0, Height: > 0 } && width <= 0)
        {
            (width, height) = exif.SwapsAxes ? (exif.Height, exif.Width) : (exif.Width, exif.Height);
        }

        if (width > 0 && height > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{width} × {height}"));
        }

        if (item.Length > 0)
        {
            parts.Add(FormatBytes(item.Length));
        }

        if (exif?.DateTaken is { } taken)
        {
            parts.Add(taken.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
        }

        if (exif?.Camera is { } camera)
        {
            parts.Add(camera);
        }

        if (exif is not null)
        {
            var shot = new List<string>(4);
            if (exif.FNumber is { } f && f > 0)
            {
                shot.Add(string.Create(CultureInfo.InvariantCulture, $"f/{f:0.#}"));
            }

            if (exif.ExposureTime is { } t && t > 0)
            {
                shot.Add(t >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{t:0.#}s") : string.Create(CultureInfo.InvariantCulture, $"1/{Math.Round(1 / t)}s"));
            }

            if (exif.Iso is { } iso && iso > 0)
            {
                shot.Add(string.Create(CultureInfo.InvariantCulture, $"ISO {iso}"));
            }

            if (exif.FocalLength is { } fl && fl > 0)
            {
                shot.Add(string.Create(CultureInfo.InvariantCulture, $"{fl:0.#}mm"));
            }

            if (shot.Count > 0)
            {
                parts.Add(string.Join(' ', shot));
            }
        }

        return string.Join("  ·  ", parts);
    }

    /// <summary>1.2 MB 같은 사람이 읽는 크기.</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0
            ? bytes.ToString("N0", CultureInfo.CurrentCulture) + " B"
            : v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[u];
    }
}

/// <summary>
/// 미리보기 창과 빠른 보기가 함께 쓰는 불러오기 흐름:
/// 캐시된 썸네일을 즉시 보여 주고 → 화면 크기로 디코딩한 이미지로 바꾸고 → 100%·확대로 더 큰 이미지가 필요하면 원본을 다시 불러온다.
/// 새 항목을 보이면 이전 요청은 취소한다. UI 스레드 전용.
/// </summary>
internal sealed class ImagePresenter(ZoomImageView view, Action<string> setInfo)
{
    private CancellationTokenSource? _cts;
    private ImageItemInfo? _item;
    private int _loadedEdge;
    private int _sourceW;
    private int _sourceH;
    private ExifInfo? _exif;

    public ThumbnailLoader? Loader { get; set; }

    public bool AllowContentDecode { get; set; } = true;

    public ImageItemInfo? Current => _item;

    private ThumbnailLoader ActiveLoader => Loader ?? ThumbnailLoader.Shared;

    public void Clear()
    {
        _cts?.Cancel();
        _item = null;
        view.Reset(null, 0, 0);
        setInfo("");
    }

    public async Task ShowAsync(ImageItemInfo item, int maxEdge)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _item = item;
        _exif = null;
        _sourceW = _sourceH = 0;
        _loadedEdge = 0;

        // 1. 격자에서 이미 만든 썸네일 (크기 무관)
        var dpi = VisualTreeHelper.GetDpi(view).PixelsPerDip;
        var thumb = ActiveLoader.Memory.FindAnySize(item.KeyFor(-1), ImageGridView.SizePresets.Reverse().Select(s => (int)Math.Round(s * dpi)));
        view.Reset(thumb, 0, 0);
        setInfo(ImageInfoText.Format(item, 0, 0, null));

        try
        {
            // 2. EXIF(클라우드 전용이면 읽지 않음)와 화면 크기 디코딩을 함께
            var exifTask = AllowContentDecode ? Task.Run(() => ExifReader.TryReadFile(item.Path), cts.Token) : Task.FromResult<ExifInfo?>(null);
            await LoadAsync(item, maxEdge, cts.Token);
            _exif = await exifTask;
            if (!cts.IsCancellationRequested)
            {
                setInfo(ImageInfoText.Format(item, _sourceW, _sourceH, _exif));
            }
        }
        catch (OperationCanceledException)
        {
            // 다른 항목으로 넘어갔다
        }
    }

    /// <summary>배율이 올라 지금 이미지로는 흐릿하면 원본 크기로 다시 불러온다.</summary>
    public async Task EnsureResolutionAsync()
    {
        if (_item is not { } item || _cts is null || _loadedEdge == 0)
        {
            return;
        }

        var needed = Math.Max(_sourceW, _sourceH) * view.EffectiveZoom;
        if (needed <= _loadedEdge * 1.05)
        {
            return;
        }

        try
        {
            await LoadAsync(item, 0, _cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadAsync(ImageItemInfo item, int maxEdge, CancellationToken ct)
    {
        var (image, sw, sh) = await ActiveLoader.GetPreviewAsync(item, maxEdge, AllowContentDecode, ct);
        ct.ThrowIfCancellationRequested();
        if (image is null)
        {
            if (view.Source is null)
            {
                setInfo(ImageFormats.NeedsCodec(item.Name) ? Foldy.Core.Localization.Loc.T("img.codec") : Foldy.Core.Localization.Loc.T("img.cantPreview"));
            }

            _loadedEdge = 0;
            return;
        }

        _sourceW = sw > 0 ? sw : image.PixelWidth;
        _sourceH = sh > 0 ? sh : image.PixelHeight;
        _loadedEdge = maxEdge == 0 ? int.MaxValue : Math.Max(image.PixelWidth, image.PixelHeight);
        if (Math.Max(image.PixelWidth, image.PixelHeight) >= Math.Max(_sourceW, _sourceH))
        {
            _loadedEdge = int.MaxValue; // 원본 그대로 받았다
        }

        view.NaturalPixelWidth = _sourceW;
        view.NaturalPixelHeight = _sourceH;
        view.Upgrade(image);
    }

    /// <summary>지금 그리고 있는 이미지.</summary>
    public BitmapSource? CurrentImage => view.Source;
}
