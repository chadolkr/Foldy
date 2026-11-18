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
                        return ClassifyLocal(fileUri.LocalPath, frag, url.EndsWith('/'), directoryExists);
                    }

                    return new MarkdownLinkTarget(MarkdownLinkKind.Blocked, url, null);
                default:
                    return new MarkdownLinkTarget(MarkdownLinkKind.Blocked, url, null);
            }
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

        var rel = Unescape(url).Replace('/', System.IO.Path.DirectorySeparatorChar);
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
