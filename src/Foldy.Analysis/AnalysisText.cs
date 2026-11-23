using System.Globalization;

namespace Foldy.Analysis;

/// <summary>분석 화면 문자열 도우미.</summary>
public static class AnalysisText
{
    public const long FileTimeUnixEpoch = 116_444_736_000_000_000;

    /// <summary>확장자: 마지막 점 뒤(점이 맨 앞·맨 끝이면 없음), 소문자, 16자 넘으면 없음. Rust extension()과 같다.</summary>
    public static string Extension(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var i = name.LastIndexOf('.');
        if (i <= 0 || i + 1 >= name.Length)
        {
            return "";
        }

        var e = name[(i + 1)..];
        return e.Length > 16 ? "" : e.ToLowerInvariant();
    }

    /// <summary>1.5 GB, 320 MB, 12 KB 같은 크기 문자열 (1024 단위).</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = Math.Abs(bytes);
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        var sign = bytes < 0 ? "-" : "";
        var num = u == 0 || v >= 100 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);
        return sign + num + " " + units[u];
    }

    public static DateTime FromFileTime(long ft) => ft > 0 ? DateTime.FromFileTimeUtc(ft) : DateTime.MinValue;

    public static long ToFileTime(DateTime utc) => utc.ToUniversalTime().ToFileTimeUtc();

    /// <summary>"방금", "12분 전", "3시간 전", "2일 전" (진행 글은 한국어, 다른 언어에서는 진행률만 보인다).</summary>
    public static string Ago(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "방금"
        : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes}분 전"
        : age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours}시간 전"
        : $"{(int)age.TotalDays}일 전";
}

/// <summary>확장자 → 종류 묶음 (설계서 7.3 "동영상, 압축, 설치 파일 등 그룹은 앱에서 매핑").</summary>
public static class ExtensionGroups
{
    private static readonly Dictionary<string, string> Map = Build();

    public static string GroupOf(string ext) => Map.TryGetValue(ext, out var g) ? g : ext.Length == 0 ? "확장자 없음" : "기타";

    private static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string group, params string[] exts)
        {
            foreach (var e in exts)
            {
                d[e] = group;
            }
        }

        Add("동영상", "mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "flv", "ts", "mts");
        Add("이미지", "jpg", "jpeg", "png", "gif", "bmp", "webp", "heic", "heif", "tif", "tiff", "raw", "cr2", "nef", "arw", "dng", "psd", "svg");
        Add("음악", "mp3", "flac", "wav", "aac", "m4a", "ogg", "wma", "opus");
        Add("압축", "zip", "7z", "rar", "tar", "gz", "bz2", "xz", "zst", "cab");
        Add("설치 파일", "exe", "msi", "msix", "appx", "iso", "img", "dmg");
        Add("문서", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "hwp", "hwpx", "txt", "md", "rtf", "odt", "csv");
        Add("코드", "cs", "rs", "js", "ts", "tsx", "jsx", "py", "java", "c", "cpp", "h", "hpp", "go", "kt", "swift", "json", "xml", "yaml", "yml");
        Add("프로그램 파일", "dll", "sys", "pdb", "lib", "obj", "so", "a", "o", "pyc", "class", "jar", "node");
        Add("디스크 이미지", "vhd", "vhdx", "vmdk", "vdi", "qcow2");
        Add("로그·덤프", "log", "etl", "dmp", "hdmp", "mdmp");
        return d;
    }
}
