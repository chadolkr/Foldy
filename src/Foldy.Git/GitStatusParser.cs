using Foldy.Core.FileSystem;

namespace Foldy.Git;

/// <summary>`git status --porcelain=v2 -z --branch` 출력 파서 (순수 함수).</summary>
public static class GitStatusParser
{
    public static RepoStatus Parse(string output)
    {
        string? branch = null, oid = null, upstream = null;
        var detached = false;
        int ahead = 0, behind = 0;
        var files = new List<GitFileEntry>();

        var tokens = output.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.Length == 0) continue;

            switch (t[0])
            {
                case '#':
                    ParseHeader(t, ref branch, ref oid, ref upstream, ref detached, ref ahead, ref behind);
                    break;
                case '1': // 1 XY sub mH mI mW hH hI path
                {
                    var p = t.Split(' ', 9);
                    if (p.Length == 9) files.Add(new GitFileEntry(p[8], null, Map(p[1][0]), Map(p[1][1])));
                    break;
                }
                case '2': // 2 XY sub mH mI mW hH hI Xscore path \0 origPath
                {
                    var p = t.Split(' ', 10);
                    if (p.Length == 10)
                    {
                        var orig = i + 1 < tokens.Length ? tokens[++i] : null;
                        files.Add(new GitFileEntry(p[9], orig, Map(p[1][0]), Map(p[1][1])));
                    }
                    break;
                }
                case 'u': // u XY sub m1 m2 m3 mW h1 h2 h3 path
                {
                    var p = t.Split(' ', 11);
                    if (p.Length == 11) files.Add(new GitFileEntry(p[10], null, GitChange.Conflicted, GitChange.Conflicted));
                    break;
                }
                case '?':
                    if (t.Length > 2) files.Add(new GitFileEntry(t[2..], null, GitChange.None, GitChange.Untracked));
                    break;
                case '!':
                    if (t.Length > 2) files.Add(new GitFileEntry(t[2..], null, GitChange.None, GitChange.Ignored));
                    break;
            }
        }
        return new RepoStatus(branch, oid, detached, upstream, ahead, behind, files);
    }

    private static void ParseHeader(string t, ref string? branch, ref string? oid, ref string? upstream,
        ref bool detached, ref int ahead, ref int behind)
    {
        const string head = "# branch.head ", oidH = "# branch.oid ", up = "# branch.upstream ", ab = "# branch.ab ";
        if (t.StartsWith(head, StringComparison.Ordinal))
        {
            var v = t[head.Length..];
            if (v == "(detached)") detached = true; else branch = v;
        }
        else if (t.StartsWith(oidH, StringComparison.Ordinal))
        {
            var v = t[oidH.Length..];
            oid = v == "(initial)" ? null : v;
        }
        else if (t.StartsWith(up, StringComparison.Ordinal)) upstream = t[up.Length..];
        else if (t.StartsWith(ab, StringComparison.Ordinal))
        {
            // "+2 -1"
            foreach (var part in t[ab.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length > 1 && int.TryParse(part.AsSpan(1), out var n))
                {
                    if (part[0] == '+') ahead = n; else if (part[0] == '-') behind = n;
                }
            }
        }
    }

    private static GitChange Map(char c) => c switch
    {
        'M' => GitChange.Modified,
        'A' => GitChange.Added,
        'D' => GitChange.Deleted,
        'R' => GitChange.Renamed,
        'C' => GitChange.Copied,
        'T' => GitChange.TypeChanged,
        'U' => GitChange.Conflicted,
        _ => GitChange.None,
    };
}

/// <summary>
/// 목록 배지 조회용 맵. 전체 경로 → GitFileStatus. 폴더에는 하위 변경을 Modified 로 전파하고,
/// 미추적/무시 폴더 안의 항목은 조상 상태를 상속한다.
/// </summary>
public sealed class GitBadgeMap
{
    private readonly Dictionary<string, GitFileStatus> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;

    public GitBadgeMap(string repoRoot, RepoStatus status)
    {
        _root = Normalize(repoRoot);
        foreach (var f in status.Files)
        {
            var badge = f.ToBadge();
            var rel = f.Path.TrimEnd('/');
            var full = Normalize(Path.Combine(_root, rel.Replace('/', '\\')));
            _map[full] = badge;
            if (badge == GitFileStatus.Ignored) continue; // 무시 항목은 상위로 전파하지 않는다

            // 상위 폴더로 전파: 더 중요한 상태(충돌 > 그 외)만 덮어쓴다
            var parent = Path.GetDirectoryName(full);
            while (parent is not null && parent.Length > _root.Length)
            {
                var propagated = badge == GitFileStatus.Conflicted ? GitFileStatus.Conflicted : GitFileStatus.Modified;
                if (_map.TryGetValue(parent, out var existing))
                {
                    // 충돌은 항상 우선, 미추적/무시 폴더 항목 자체는 유지
                    if (existing == GitFileStatus.Conflicted || existing is GitFileStatus.Untracked or GitFileStatus.Ignored) break;
                }
                _map[parent] = propagated;
                parent = Path.GetDirectoryName(parent);
            }
        }
    }

    public int Count => _map.Count;

    /// <summary>전체 경로의 배지. 직접 항목이 없으면 미추적/무시된 조상 폴더의 상태를 상속, 아니면 None.</summary>
    public GitFileStatus Get(string fullPath)
    {
        var p = Normalize(fullPath);
        if (_map.TryGetValue(p, out var s)) return s;
        for (var parent = Path.GetDirectoryName(p); parent is not null && parent.Length > _root.Length; parent = Path.GetDirectoryName(parent))
        {
            if (_map.TryGetValue(parent, out var ps) && ps is GitFileStatus.Untracked or GitFileStatus.Ignored) return ps;
        }
        return GitFileStatus.None;
    }

    private static string Normalize(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');
}
