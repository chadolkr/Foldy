namespace Foldy.Core.Organize;

/// <summary>"종류별" 정리의 확장자 그룹 (설계서 5.11: 문서, 이미지, 영상, 음악, 압축, 설치파일, 코드, 기타).</summary>
public static class FileTypeGroups
{
    public const string Documents = "문서";
    public const string Images = "이미지";
    public const string Videos = "영상";
    public const string Music = "음악";
    public const string Archives = "압축";
    public const string Installers = "설치파일";
    public const string Code = "코드";
    public const string Other = "기타";

    public static readonly IReadOnlyList<string> All = [Documents, Images, Videos, Music, Archives, Installers, Code, Other];

    private static readonly Dictionary<string, string> Map = Build();

    private static Dictionary<string, string> Build()
    {
        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string group, params string[] exts)
        {
            foreach (var e in exts)
            {
                m["." + e] = group;
            }
        }

        Add(Documents, "pdf", "doc", "docx", "dot", "dotx", "hwp", "hwpx", "xls", "xlsx", "xlsm", "csv", "ppt", "pptx", "odt", "ods", "odp",
            "rtf", "txt", "md", "markdown", "pages", "numbers", "key", "epub", "xps");
        Add(Images, "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "avif", "svg", "ico", "psd", "ai",
            "raw", "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "raf");
        Add(Videos, "mp4", "mov", "avi", "mkv", "wmv", "webm", "m4v", "mts", "m2ts", "3gp", "flv", "mpg", "mpeg");
        Add(Music, "mp3", "wav", "flac", "aac", "m4a", "ogg", "opus", "wma", "aiff", "mid", "midi");
        Add(Archives, "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "zst", "cab", "iso", "alz", "egg");
        Add(Installers, "exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "dmg", "pkg", "deb", "rpm", "apk");
        Add(Code, "cs", "csproj", "sln", "slnx", "rs", "toml", "py", "js", "mjs", "ts", "tsx", "jsx", "java", "kt", "go", "c", "h", "cpp", "hpp",
            "json", "xml", "yaml", "yml", "html", "htm", "css", "scss", "sql", "sh", "ps1", "bat", "cmd", "rb", "php", "swift", "lua", "ipynb");
        return m;
    }

    public static string GroupOf(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return Map.TryGetValue(Path.GetExtension(fileName), out var g) ? g : Other;
    }

    /// <summary>EXIF 촬영 시각을 볼 만한 파일 (사진·영상).</summary>
    public static bool IsMedia(string fileName)
    {
        var g = GroupOf(fileName);
        return g is Images or Videos;
    }
}
