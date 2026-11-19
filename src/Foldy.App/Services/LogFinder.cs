using System.IO;
using Foldy.Core.Logs;
using Foldy.Indexer.Client;

namespace Foldy.App.Services;

/// <summary>드라이브의 *.log 파일 목록. 색인 서비스가 있으면 색인에서 바로, 없으면 낮은 우선순위로 직접 훑는다.</summary>
public static class LogFinder
{
    /// <summary>
    /// 색인에서 찾는다. 색인이 없거나 준비가 안 됐으면 null.
    /// 검색 창이 쓰는 연결과 따로 연결한다 (같은 연결에서 새 검색을 시작하면 사용자가 하던 검색이 끊긴다).
    /// </summary>
    public static async Task<List<LogFile>?> FromIndexAsync(string driveRoot, CancellationToken ct)
    {
        await using var client = await IndexerClient.TryConnectAsync(timeout: TimeSpan.FromSeconds(3), ct: ct);
        if (client is null)
        {
            return null;
        }

        var root = driveRoot.TrimEnd('\\') + "\\";
        var list = new List<LogFile>();
        ulong cursor = 0;
        try
        {
            do
            {
                var r = await client.SearchAsync(".log", 100_000, cursor, ct);
                if (r.Status != SearchStatus.Ok)
                {
                    return null;
                }

                foreach (var h in r.Hits)
                {
                    if (!h.IsDir && h.Path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && h.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(new LogFile(h.Path, (long)h.Size, h.LastWriteTimeUtc));
                    }
                }

                cursor = r.NextCursor;
            }
            while (cursor != 0);
        }
        catch (Exception ex) when (ex is IOException or ProtocolException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }

        return list;
    }

    /// <summary>색인 없이 직접 훑기 (사용자가 눌렀을 때만). Windows 폴더와 접근할 수 없는 곳은 건너뛴다.</summary>
    public static Task<List<LogFile>> ScanAsync(string driveRoot, CancellationToken ct) => Task.Run(() =>
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        var list = new List<LogFile>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var top in SafeDirs(driveRoot))
        {
            if (string.Equals(Path.GetFileName(top), "Windows", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                foreach (var f in new DirectoryInfo(top).EnumerateFiles("*.log", options))
                {
                    ct.ThrowIfCancellationRequested();
                    list.Add(new LogFile(f.FullName, f.Length, f.LastWriteTimeUtc));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 다음 폴더로
            }
        }

        try
        {
            foreach (var f in new DirectoryInfo(driveRoot).EnumerateFiles("*.log"))
            {
                list.Add(new LogFile(f.FullName, f.Length, f.LastWriteTimeUtc));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return list;
    }, ct);

    private static string[] SafeDirs(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
