namespace Foldy.Analysis;

/// <summary>색인 없는 드라이브를 모아 둔 목록의 한 줄 (앱의 SlowCrawler 목록과 같은 내용).</summary>
public readonly record struct CrawledItem(string Path, bool IsFolder, long Size, DateTime ModifiedUtc);

/// <summary>
/// 다 모은 목록 하나. <paramref name="Read"/>는 목록 파일을 처음부터 읽어 준다(디스크가 아니라 내 PC의 목록 파일).
/// <paramref name="CompletedUtc"/>는 그 목록을 다 모은 때.
/// </summary>
public sealed record CrawledList(DateTime CompletedUtc, Func<CancellationToken, IEnumerable<CrawledItem>> Read);

/// <summary>
/// 색인 서비스(MFT)가 다루지 않는 드라이브(네트워크·회사 보안 드라이브, FAT/exFAT, USB)를 분석할 때,
/// 앱이 이미 살살 모아 둔 목록이 있으면 드라이브를 다시 읽지 않고 그 목록으로 스냅샷을 만든다
/// (사용자 2026-11-23: "K 드라이브 분석을 시켰더니 폴더 읽는 중으로 나온다. 색인은 완료되었다는데…").
/// </summary>
public static class CrawledListSnapshot
{
    /// <summary>
    /// 목록에서 <paramref name="root"/> 아래만 골라 트리로 만든다. 목록의 순서와 상관없이, 부모 폴더 줄이 아직 없으면 먼저 만들어 둔다.
    /// 할당 크기는 <paramref name="cluster"/> 단위로 올림한다.
    /// </summary>
    public static SnapshotInput Build(string root, IEnumerable<CrawledItem> items, DateTime listTimeUtc, long cluster, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(items);
        if (cluster <= 0)
        {
            cluster = FallbackScanner.DefaultCluster;
        }

        var rootPath = Normalize(root);
        var prefix = rootPath.EndsWith('\\') ? rootPath : rootPath + "\\";
        var input = new SnapshotInput
        {
            RootPath = rootPath,
            Volume = AnalysisPaths.VolumeOf(rootPath),
            StartedAt = DateTime.SpecifyKind(listTimeUtc, DateTimeKind.Utc).ToFileTimeUtc(),
        };
        input.Dirs.Add(new InputDir(1, -1, rootPath, 0, (int)FileAttributes.Directory));
        var dirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [rootPath.TrimEnd('\\')] = 0 };
        long nextId = 2;

        int EnsureDir(string path)
        {
            // 부모를 위로 따라가며 아직 없는 폴더를 만든다 (재귀 대신 쌓아서, 깊은 트리에서도 스택이 넘치지 않게)
            if (dirs.TryGetValue(path, out var found))
            {
                return found;
            }

            var chain = new Stack<string>();
            var p = path;
            int idx;
            while (!dirs.TryGetValue(p, out idx))
            {
                chain.Push(p);
                var cut = p.LastIndexOf('\\');
                if (cut < 0)
                {
                    return 0;
                }

                p = p[..cut];
            }

            while (chain.Count > 0)
            {
                var d = chain.Pop();
                var child = input.Dirs.Count;
                input.Dirs.Add(new InputDir(nextId++, idx, d[(d.LastIndexOf('\\') + 1)..], 0, (int)FileAttributes.Directory));
                dirs[d] = child;
                idx = child;
            }

            return idx;
        }

        var n = 0;
        foreach (var it in items)
        {
            if ((++n & 4095) == 0)
            {
                ct.ThrowIfCancellationRequested();
                if ((n & 0xFFFFF) == 0)
                {
                    progress?.Report(new AnalysisProgress(0.3, $"모아 둔 목록 읽는 중… {n:N0}개"));
                }
            }

            if (string.IsNullOrEmpty(it.Path) || !it.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = it.Path.TrimEnd('\\');
            var cut = path.LastIndexOf('\\');
            if (cut < prefix.Length - 1)
            {
                continue;
            }

            var parent = EnsureDir(path[..cut]);
            var name = path[(cut + 1)..];
            var mtime = SafeFileTime(it.ModifiedUtc);
            if (it.IsFolder)
            {
                if (dirs.TryGetValue(path, out var existing))
                {
                    input.Dirs[existing] = input.Dirs[existing] with { Mtime = mtime }; // 먼저 만들어 둔 폴더에 시각만 채운다
                }
                else
                {
                    dirs[path] = input.Dirs.Count;
                    input.Dirs.Add(new InputDir(nextId++, parent, name, mtime, (int)FileAttributes.Directory));
                }
            }
            else
            {
                var size = Math.Max(0, it.Size);
                var alloc = (size + cluster - 1) / cluster * cluster;
                input.Files.Add(new InputFile(nextId++, parent, name, size, alloc, mtime, (int)FileAttributes.Normal));
            }
        }

        progress?.Report(new AnalysisProgress(0.5, $"모아 둔 목록에서 파일 {input.Files.Count:N0}개를 찾았습니다"));
        return input;
    }

    private static string Normalize(string root)
    {
        var r = root.Trim().Replace('/', '\\');
        if (r.Length == 2 && r[1] == ':')
        {
            return r + "\\";
        }

        return r.Length > 3 ? r.TrimEnd('\\') : r;
    }

    private static long SafeFileTime(DateTime utc)
    {
        try
        {
            return utc.Year < 1602 ? 0 : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }
}
