using System.Diagnostics;
using System.Security.Cryptography;

namespace Foldy.Core.Downloads;

public enum DownloadKind : byte
{
    /// <summary>이미 설치된 프로그램의 설치 파일 (.exe 제품 이름이 설치된 프로그램 목록과 맞을 때만).</summary>
    Installer,

    /// <summary>같은 내용의 파일이 이미 있다 ("보고서 (1).pdf" 같은). 하나는 남긴다.</summary>
    Duplicate,

    /// <summary>옆에 같은 이름의 폴더가 있는 zip (이미 풀었음).</summary>
    Unpacked,

    /// <summary>90일 넘게 열거나 바꾸지 않은 파일.</summary>
    Old,
}

public sealed record DownloadItem(string Path, long Size, DownloadKind Kind, string Reason);

/// <summary>
/// 다운로드 폴더 정리 제안. 아무것도 지우지 않는다: 고른 항목을 호출한 쪽이 휴지통으로 보내거나 보관 폴더로 옮긴다.
/// 다운로드 폴더 바로 아래의 파일만 본다 (하위 폴더는 사용자가 정리해 둔 것으로 본다).
/// </summary>
public static class DownloadsPlanner
{
    public const int OldDays = 90;
    public const long NoticeBytes = 1L << 30;
    /// <summary>예전(한국어) 이름. 다운로드에 이미 있으면 그 폴더를 계속 쓴다.</summary>
    public const string ArchiveFolderNameKo = "보관";

    /// <summary>지금 앱 언어로 만드는 이름.</summary>
    public static string ArchiveFolderName => Foldy.Core.Localization.Loc.T("folder.downloadsArchive");

    /// <param name="installedName">.exe의 제품 이름이 설치된 프로그램이면 그 프로그램 이름, 아니면 null.</param>
    /// <param name="findDuplicates">같은 크기 파일끼리 내용(SHA-256)을 비교한다. 백그라운드 확인에서는 끈다.</param>
    public static IReadOnlyList<DownloadItem> Plan(string folder, Func<string, string?> installedName, DateTime nowUtc, bool findDuplicates, CancellationToken ct = default)
    {
        var files = new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint })
            .Where(f => f.Length > 0 && !IsPartial(f.Name))
            .ToList();
        var dirs = new HashSet<string>(
            new DirectoryInfo(folder).EnumerateDirectories().Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        var items = new Dictionary<string, DownloadItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in files.Where(f => f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            if (ProductName(f.FullName) is { } product && installedName(product) is { } app)
            {
                items[f.FullName] = new DownloadItem(f.FullName, f.Length, DownloadKind.Installer, Foldy.Core.Localization.Loc.F("dlc.installer", app));
            }
        }

        if (findDuplicates)
        {
            foreach (var same in files.GroupBy(f => f.Length).Where(g => g.Count() > 1))
            {
                foreach (var byHash in same.GroupBy(f => HashOrNull(f.FullName, ct)).Where(g => g.Key is not null && g.Count() > 1))
                {
                    // 이름이 가장 짧은(보통 원본) 것, 같으면 가장 먼저 받은 것을 남긴다
                    var keep = byHash.OrderBy(f => f.Name.Length).ThenBy(f => f.CreationTimeUtc).First();
                    foreach (var f in byHash.Where(f => f != keep && !items.ContainsKey(f.FullName)))
                    {
                        items[f.FullName] = new DownloadItem(f.FullName, f.Length, DownloadKind.Duplicate, Foldy.Core.Localization.Loc.F("dlc.duplicate", keep.Name));
                    }
                }
            }
        }

        foreach (var f in files.Where(f => f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) && !items.ContainsKey(f.FullName)))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(f.Name);
            if (dirs.Contains(stem))
            {
                items[f.FullName] = new DownloadItem(f.FullName, f.Length, DownloadKind.Unpacked, Foldy.Core.Localization.Loc.F("dlc.unpacked", stem));
            }
        }

        foreach (var f in files.Where(f => !items.ContainsKey(f.FullName)))
        {
            var last = new[] { f.LastWriteTimeUtc, f.LastAccessTimeUtc, f.CreationTimeUtc }.Max();
            var days = (int)(nowUtc - last).TotalDays;
            if (days >= OldDays)
            {
                items[f.FullName] = new DownloadItem(f.FullName, f.Length, DownloadKind.Old, Foldy.Core.Localization.Loc.F("dlc.old", days));
            }
        }

        return items.Values.OrderBy(i => i.Kind).ThenByDescending(i => i.Size).ToList();
    }

    /// <summary>보관 폴더: 다운로드\보관\연-월.</summary>
    public static string ArchiveFolder(string downloads, DateTime nowLocal) =>
        System.IO.Path.Combine(downloads, Foldy.Core.Localization.FolderNames.Pick(downloads, ArchiveFolderNameKo, ArchiveFolderName), nowLocal.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>보관 폴더로 옮긴다 (이름이 겹치면 " (2)"를 붙인다). 옮긴 개수를 돌려준다.</summary>
    public static int MoveToArchive(IEnumerable<string> paths, string archiveFolder)
    {
        Directory.CreateDirectory(archiveFolder);
        var moved = 0;
        foreach (var p in paths)
        {
            var name = System.IO.Path.GetFileName(p);
            var dst = System.IO.Path.Combine(archiveFolder, name);
            for (var i = 2; File.Exists(dst) || Directory.Exists(dst); i++)
            {
                dst = System.IO.Path.Combine(archiveFolder, $"{System.IO.Path.GetFileNameWithoutExtension(name)} ({i}){System.IO.Path.GetExtension(name)}");
            }

            try
            {
                File.Move(p, dst);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 사용 중이면 그대로 둔다
            }
        }

        return moved;
    }

    // 받는 중인 파일 (브라우저 임시 확장자)
    private static bool IsPartial(string name) =>
        name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private static string? ProductName(string exe)
    {
        try
        {
            var n = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim();
            return string.IsNullOrEmpty(n) ? null : n;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? HashOrNull(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(s));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 설치 파일의 제품 이름이 설치된 프로그램 이름과 맞는지. 확실할 때만 맞다고 한다:
    /// 제품 이름(4자 이상)이 설치된 프로그램 이름과 같거나, 그 이름이 제품 이름으로 시작할 때 (예: "Git" → "Git version 2.47").
    /// "Setup", "Installer" 같은 흔한 이름은 맞추지 않는다.
    /// </summary>
    public static string? MatchInstalled(string product, IEnumerable<string> installed)
    {
        var p = Normalize(product);
        if (p.Length < 3 || Generic.Contains(p))
        {
            return null;
        }

        foreach (var name in installed)
        {
            var n = Normalize(name);
            if (n == p || (n.StartsWith(p + " ", StringComparison.Ordinal) && p.Length >= 3))
            {
                return name;
            }
        }

        return null;
    }

    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal) { "setup", "installer", "install", "update", "updater", "launcher", "microsoft windows operating system", "windows" };

    private static string Normalize(string s) =>
        string.Join(' ', s.ToLowerInvariant().Replace('®', ' ').Replace('™', ' ').Split([' ', '\t', '-', '_'], StringSplitOptions.RemoveEmptyEntries));
}
