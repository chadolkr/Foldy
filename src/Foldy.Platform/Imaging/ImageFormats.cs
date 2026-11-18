using Microsoft.Win32;

namespace Foldy.Platform.Imaging;

/// <summary>
/// 이미지로 다루는 확장자 목록과 "대부분 이미지" 판정 (설계서 5.12).
/// JPG·PNG·GIF·BMP·TIFF·WebP·ICO는 기본, HEIC·AVIF·카메라 RAW는 Windows에 WIC 코덱이 있을 때만 이미지로 본다.
/// 스레드 안전: 코덱 목록은 처음 쓸 때 한 번 레지스트리에서 읽어 캐시한다.
/// </summary>
public static class ImageFormats
{
    /// <summary>"이미지만 보기" 제안 기준 (70%).</summary>
    public const double MostlyImagesThreshold = 0.7;

    // WIC 디코더 범주 CATID_WICBitmapDecoders
    private const string DecoderInstanceKey = @"CLSID\{7ED96837-96F0-4812-B211-F13C24117ED3}\Instance";

    private static readonly HashSet<string> BaseSet = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".webp", ".ico",
    };

    /// <summary>코덱(확장)이 깔려 있어야 보이는 형식.</summary>
    private static readonly HashSet<string> CodecSet = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".avif",
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".sr2", ".dng", ".orf", ".rw2", ".raf", ".srw", ".pef", ".rwl", ".3fr", ".erf", ".kdc", ".mrw",
    };

    private static readonly Lazy<HashSet<string>> WicExtensions = new(LoadWicExtensions, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>기본 지원 확장자 (점 포함, 소문자).</summary>
    public static IReadOnlyCollection<string> BaseExtensions => BaseSet;

    /// <summary>코덱이 있어야 하는 확장자.</summary>
    public static IReadOnlyCollection<string> CodecExtensions => CodecSet;

    /// <summary>파일 이름이나 확장자(".jpg")로 이미지 여부를 판정한다. 코덱이 없는 HEIC 등은 false.</summary>
    public static bool IsImage(string nameOrExtension)
    {
        var ext = ExtensionOf(nameOrExtension);
        if (ext.Length == 0)
        {
            return false;
        }

        return BaseSet.Contains(ext) || (CodecSet.Contains(ext) && IsCodecAvailable(ext));
    }

    /// <summary>코덱 유무와 관계없이 이미지 형식으로 알려진 확장자인지 ("코덱 필요" 안내용).</summary>
    public static bool IsKnownImageFormat(string nameOrExtension)
    {
        var ext = ExtensionOf(nameOrExtension);
        return BaseSet.Contains(ext) || CodecSet.Contains(ext);
    }

    /// <summary>알려진 형식이지만 이 PC에 코덱이 없어 열 수 없는지.</summary>
    public static bool NeedsCodec(string nameOrExtension)
    {
        var ext = ExtensionOf(nameOrExtension);
        return CodecSet.Contains(ext) && !IsCodecAvailable(ext);
    }

    /// <summary>Windows에 등록된 WIC 디코더가 이 확장자를 다루는지.</summary>
    public static bool IsCodecAvailable(string extension)
    {
        var ext = ExtensionOf(extension);
        return BaseSet.Contains(ext) || WicExtensions.Value.Contains(ext);
    }

    /// <summary>GIF처럼 움직일 수 있는 형식 (격자에서는 첫 프레임만 보여준다).</summary>
    public static bool MayBeAnimated(string nameOrExtension) =>
        ExtensionOf(nameOrExtension) is ".gif" or ".webp";

    /// <summary>
    /// 파일 이름들 중 이미지 비율이 기준 이상인지. 폴더는 호출하는 쪽에서 빼고 넘긴다.
    /// 파일이 <paramref name="minFiles"/>개보다 적으면 제안하지 않는다.
    /// </summary>
    public static bool IsMostlyImages(IEnumerable<string> fileNames, double threshold = MostlyImagesThreshold, int minFiles = 3)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        var (images, total) = CountImages(fileNames);
        return total >= Math.Max(1, minFiles) && images >= threshold * total;
    }

    /// <summary>(이미지 수, 전체 수).</summary>
    public static (int Images, int Total) CountImages(IEnumerable<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        int images = 0, total = 0;
        foreach (var name in fileNames)
        {
            total++;
            if (IsImage(name))
            {
                images++;
            }
        }

        return (images, total);
    }

    private static string ExtensionOf(string nameOrExtension)
    {
        if (string.IsNullOrEmpty(nameOrExtension))
        {
            return "";
        }

        if (nameOrExtension[0] == '.' && nameOrExtension.IndexOf('.', 1) < 0)
        {
            return nameOrExtension;
        }

        return Path.GetExtension(nameOrExtension);
    }

    /// <summary>HKCR\CLSID\{WIC 디코더 범주}\Instance 아래 각 디코더의 FileExtensions 값을 모은다.</summary>
    private static HashSet<string> LoadWicExtensions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var instances = Registry.ClassesRoot.OpenSubKey(DecoderInstanceKey);
            if (instances is null)
            {
                return set;
            }

            foreach (var clsid in instances.GetSubKeyNames())
            {
                using var codec = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid);
                if (codec?.GetValue("FileExtensions") is string exts)
                {
                    foreach (var e in exts.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        set.Add(e.StartsWith('.') ? e : "." + e);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // 레지스트리를 못 읽으면 기본 형식만 쓴다
        }

        return set;
    }
}
