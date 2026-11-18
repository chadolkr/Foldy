using System.Collections.Concurrent;

namespace Foldy.Git;

/// <summary>
/// 폴더에서 위로 올라가며 .git 을 찾는다. .git 이 파일이면(워크트리/서브모듈) gitdir: 을 따라간다.
/// 경로별 결과(없음 포함)를 캐시하며, 네트워크 경로는 AllowNetwork 가 켜져 있을 때만 건드린다.
/// </summary>
public sealed class GitRepoLocator
{
    private readonly ConcurrentDictionary<string, GitRepoInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, bool> _isNetworkPath;

    public GitRepoLocator(bool allowNetwork = false, Func<string, bool>? isNetworkPath = null)
    {
        AllowNetwork = allowNetwork;
        _isNetworkPath = isNetworkPath ?? IsNetworkPathDefault;
    }

    /// <summary>true 면 UNC/네트워크 드라이브도 탐색한다 (느릴 수 있음). 기본 false.</summary>
    public bool AllowNetwork { get; set; }

    /// <summary>folder 를 포함하는 가장 가까운 저장소. 없거나 네트워크 경로(미허용)면 null.</summary>
    public GitRepoInfo? FindRepo(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        string start;
        try { start = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        if (!AllowNetwork && _isNetworkPath(start)) return null;
        start = TrimSeparator(start);
        if (_cache.TryGetValue(start, out var cached)) return cached;

        var visited = new List<string>();
        GitRepoInfo? found = null;
        for (var dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (_cache.TryGetValue(dir, out var c)) { found = c; break; }
            visited.Add(dir);
            found = TryRead(dir);
            if (found is not null) break;
        }
        // 지나온 모든 폴더가 같은 결과를 가리킨다 (가장 가까운 저장소 기준)
        foreach (var v in visited) _cache[v] = found;
        return found;
    }

    /// <summary>캐시 무효화. git init/clone 직후나 .git 삭제 후 호출.</summary>
    public void Invalidate(string? folder = null)
    {
        if (folder is null) { _cache.Clear(); return; }
        var f = TrimSeparator(Path.GetFullPath(folder));
        foreach (var key in _cache.Keys)
            if (key.StartsWith(f, StringComparison.OrdinalIgnoreCase)) _cache.TryRemove(key, out _);
    }

    private static GitRepoInfo? TryRead(string dir)
    {
        try
        {
            var dotGit = Path.Combine(dir, ".git");
            if (Directory.Exists(dotGit)) return new GitRepoInfo(dir, dotGit, false);
            if (File.Exists(dotGit))
            {
                var gitDir = ParseGitDirFile(File.ReadAllText(dotGit), dir);
                return gitDir is null ? null : new GitRepoInfo(dir, gitDir, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>".git" 파일 내용("gitdir: path")을 해석한다. 상대 경로는 baseDir 기준.</summary>
    internal static string? ParseGitDirFile(string content, string baseDir)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) continue;
            var p = line["gitdir:".Length..].Trim();
            if (p.Length == 0) return null;
            p = p.Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p));
        }
        return null;
    }

    private static string TrimSeparator(string p)
    {
        var root = Path.GetPathRoot(p);
        return p.Length > (root?.Length ?? 0) ? p.TrimEnd('\\', '/') : p;
    }

    private static bool IsNetworkPathDefault(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal)) return true;
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && root.Length >= 2 && root[1] == ':'
                   && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return false; }
    }
}
