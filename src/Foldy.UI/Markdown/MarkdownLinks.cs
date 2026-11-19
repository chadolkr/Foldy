using System.IO;

namespace Foldy.UI.Markdown;

/// <summary>문서 링크의 종류. 종류마다 처리 방법이 다르다 (MarkdownView 참고).</summary>
public enum MarkdownLinkKind
{
    /// <summary>같은 문서 안의 앵커 (#제목, 각주).</summary>
    Anchor,

    /// <summary>다른 Markdown 파일 → 같은 탭에서 연다 (MarkdownTabViewModel.OpenLinked).</summary>
    Markdown,

    /// <summary>폴더 → 호스트(패널)가 폴더 탭으로 연다.</summary>
    Folder,

    /// <summary>그 밖의 로컬 파일 → 셸로 연다.</summary>
    File,

    /// <summary>http(s), mailto → 기본 브라우저·메일 앱.</summary>
    Web,

    /// <summary>javascript: 등 처리하지 않는 링크.</summary>
    Blocked,
}

/// <summary>분류된 링크. Path는 로컬 경로(전체 경로) 또는 URL, Fragment는 '#' 뒤 앵커(없으면 null).</summary>
public sealed record MarkdownLinkTarget(MarkdownLinkKind Kind, string Path, string? Fragment);

/// <summary>Markdown 링크 분류기. 파일 시스템은 존재 확인(폴더인지)에만 쓴다.</summary>
public static class MarkdownLinks
{
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd", ".mkdn",
    };

    // 문서 링크로 바로 실행되면 위험한 확장자. 열기 전에 사용자에게 한 번 묻는다.
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msp", ".scr", ".pif", ".lnk", ".url", ".hta", ".cpl", ".reg", ".jar", ".appref-ms",
    };

    public static bool IsMarkdownPath(string path) => MarkdownExtensions.Contains(System.IO.Path.GetExtension(path));

    public static bool IsExecutable(string path) => ExecutableExtensions.Contains(System.IO.Path.GetExtension(path));

    // 문서 링크로 묻지 않고 바로 열어도 되는 확장자 (문서·그림·소리·영상·텍스트). 나머지는 모두 한 번 묻는다.
    // 위험 목록만으로는 .iso .img .chm .msc .settingcontent-ms .library-ms .docm 같은 것을 다 막을 수 없다.
    private static readonly HashSet<string> SafeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".tsv", ".json", ".yaml", ".yml", ".toml", ".ini", ".md", ".markdown",
        ".pdf", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".hwp", ".hwpx", ".rtf",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic",
        ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".mp4", ".mkv", ".mov", ".avi", ".webm",
        ".zip", ".7z",
    };

    /// <summary>열기 전에 사용자에게 물어야 하는지: 안전한 확장자 목록에 없으면 묻는다.</summary>
    public static bool NeedsConfirmation(string path) => !SafeExtensions.Contains(System.IO.Path.GetExtension(path));

    /// <summary>
    /// 다른 컴퓨터를 가리키는 경로(\\서버\공유, //서버/공유). 열거나 있는지 확인만 해도 Windows가 그 서버에
    /// 로그인 정보(NTLM)를 보내므로, 사용자가 허락하기 전에는 건드리지 않는다.
    /// </summary>
    public static bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    /// <summary>
    /// 링크를 분류한다. 상대 경로는 baseFolder(문서가 있는 폴더) 기준. 퍼센트 인코딩(%20)은 풀고,
    /// 경로가 실제 폴더이거나 '/'로 끝나면 Folder.
    /// </summary>
    public static MarkdownLinkTarget Classify(string url, string baseFolder, Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        url = url.Trim();
        if (url.Length == 0)
        {
            return new MarkdownLinkTarget(MarkdownLinkKind.Blocked, url, null);
        }

        if (url[0] == '#')
        {
            return new MarkdownLinkTarget(MarkdownLinkKind.Anchor, "", Unescape(url[1..]));
        }

        if (TryGetScheme(url, out var scheme))
        {
            switch (scheme)
            {
                case "http":
                case "https":
                case "mailto":
                    return new MarkdownLinkTarget(MarkdownLinkKind.Web, url, null);
                case "file":
                    if (Uri.TryCreate(url, UriKind.Absolute, out var fileUri) && fileUri.IsFile)
                    {
                        var frag = fileUri.Fragment.Length > 1 ? Unescape(fileUri.Fragment[1..]) : null;
                        if (fileUri.IsUnc)
                        {
                            return Network(@"\\" + fileUri.Host + Unescape(fileUri.AbsolutePath).Replace('/', '\\'), frag, url.EndsWith('/'));
                        }

                        return ClassifyLocal(fileUri.LocalPath, frag, url.EndsWith('/'), directoryExists);
                    }

                    return new MarkdownLinkTarget(MarkdownLinkKind.Blocked, url, null);
                default:
                    return new MarkdownLinkTarget(MarkdownLinkKind.Blocked, url, null);
            }
        }

        // 장치 경로(\\?\…, \\.\…)는 열지 않는다. '?'를 쿼리로 떼기 전에 본다.
        var unescaped = Unescape(url).Replace('/', '\\');
        if (unescaped.StartsWith(@"\\?\", StringComparison.Ordinal) || unescaped.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return new MarkdownLinkTarget(MarkdownLinkKind.Blocked, url, null);
        }

        // 상대·절대 로컬 경로. '?'(쿼리)와 '#'(앵커)를 떼어 낸다.
        string? fragment = null;
        var hash = url.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            fragment = Unescape(url[(hash + 1)..]);
            url = url[..hash];
        }

        var query = url.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            url = url[..query];
        }

        if (url.Length == 0)
        {
            // "?x#y" 처럼 경로가 비면 같은 문서 앵커로 본다.
            return new MarkdownLinkTarget(MarkdownLinkKind.Anchor, "", fragment ?? "");
        }

        var raw = Unescape(url);
        if (IsNetworkPath(raw))
        {
            return Network(raw.Replace('/', '\\'), fragment, url.EndsWith('/') || url.EndsWith('\\'));
        }

        var rel = raw.Replace('/', System.IO.Path.DirectorySeparatorChar);
        string full;
        try
        {
            // 완전한 경로(C:\, \\서버)는 그대로, '/'로 시작하는 경로는 문서 폴더 기준으로 본다.
            full = System.IO.Path.GetFullPath(System.IO.Path.IsPathFullyQualified(rel)
                ? rel
                : System.IO.Path.Combine(baseFolder, rel.TrimStart('\\')));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new MarkdownLinkTarget(MarkdownLinkKind.Blocked, url, null);
        }

        var trailingSlash = url.EndsWith('/') || url.EndsWith('\\');
        return ClassifyLocal(full, fragment, trailingSlash, directoryExists);
    }

    // 네트워크 경로는 폴더인지 확인하지 않는다 (확인만 해도 서버에 접속한다)
    private static MarkdownLinkTarget Network(string path, string? fragment, bool trailingSlash) =>
        trailingSlash ? new MarkdownLinkTarget(MarkdownLinkKind.Folder, path.TrimEnd('\\'), null)
        : IsMarkdownPath(path) ? new MarkdownLinkTarget(MarkdownLinkKind.Markdown, path, fragment)
        : new MarkdownLinkTarget(MarkdownLinkKind.File, path, null);

    private static MarkdownLinkTarget ClassifyLocal(string full, string? fragment, bool trailingSlash, Func<string, bool> directoryExists)
    {
        if (trailingSlash || directoryExists(full))
        {
            return new MarkdownLinkTarget(MarkdownLinkKind.Folder, System.IO.Path.TrimEndingDirectorySeparator(full), null);
        }

        return IsMarkdownPath(full)
            ? new MarkdownLinkTarget(MarkdownLinkKind.Markdown, full, fragment)
            : new MarkdownLinkTarget(MarkdownLinkKind.File, full, null);
    }

    /// <summary>"scheme:" 접두를 찾는다. "C:\x" 같은 드라이브 문자는 스킴이 아니다.</summary>
    private static bool TryGetScheme(string url, out string scheme)
    {
        scheme = "";
        var colon = url.IndexOf(':', StringComparison.Ordinal);
        if (colon < 2)
        {
            return false;
        }

        for (var i = 0; i < colon; i++)
        {
            var c = url[i];
            var ok = char.IsAsciiLetter(c) || (i > 0 && (char.IsAsciiDigit(c) || c is '+' or '-' or '.'));
            if (!ok)
            {
                return false;
            }
        }

        scheme = url[..colon].ToLowerInvariant();
        return true;
    }

    private static string Unescape(string s)
    {
        try
        {
            return Uri.UnescapeDataString(s);
        }
        catch (UriFormatException)
        {
            return s;
        }
    }
}
