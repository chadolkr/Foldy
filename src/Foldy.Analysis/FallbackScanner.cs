using Microsoft.Data.Sqlite;

namespace Foldy.Analysis;

/// <summary>분석 진행률. Fraction은 0~1.</summary>
public sealed record AnalysisProgress(double Fraction, string Message);

/// <summary>번들 SQLite(SQLitePCLRaw e_sqlite3) 초기화.</summary>
public static class AnalysisSqlite
{
    private static int _init;

    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _init, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }
    }

    /// <summary>스냅샷을 읽기 전용(mode=ro)으로 연다. 스냅샷은 만든 뒤 바뀌지 않으므로 풀링하지 않는다.</summary>
    public static SqliteConnection OpenReadOnly(string path)
    {
        EnsureInitialized();
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        return c;
    }
}

/// <summary>
/// 서비스(MFT 색인)가 없는 드라이브(USB, 네트워크, FAT/exFAT)나 서비스가 꺼져 있을 때 C#이 직접 열거해
/// 같은 스키마의 DB를 %LOCALAPPDATA%\Foldy\Analysis\에 만든다 (설계서 7.3).
/// 전용 스레드 하나를 낮은 우선순위(Lowest)로 돌려 사용자 작업을 방해하지 않는다. 심볼릭 링크·정션은 따라가지 않는다.
/// </summary>
public static class FallbackScanner
{
    /// <summary>할당 단위를 모를 때 쓰는 클러스터 크기.</summary>
    public const long DefaultCluster = 4096;

    public static Task<(string Path, SnapshotStats Stats)> ScanAsync(
        string root, string outDirectory, CleanupRules rules, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        var tcs = new TaskCompletionSource<(string, SnapshotStats)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try
            {
                var input = Enumerate(root, progress, ct);
                tcs.TrySetResult(SnapshotBuilder.BuildIntoDirectory(input, outDirectory, AnalysisPaths.VolumeKey(root), rules, progress, ct));
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "Foldy analysis scan",
        };
        t.Start();
        return tcs.Task;
    }

    /// <summary>폴더를 깊이 우선으로 열거한다. 권한 없는 폴더는 건너뛴다.</summary>
    public static SnapshotInput Enumerate(string root, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        var info = new DirectoryInfo(root);
        if (!info.Exists)
        {
            throw new DirectoryNotFoundException(root);
        }

        var rootPath = info.FullName;
        var input = new SnapshotInput { RootPath = rootPath, Volume = AnalysisPaths.VolumeOf(rootPath), StartedAt = DateTime.UtcNow.ToFileTimeUtc() };
        input.Dirs.Add(new InputDir(1, -1, rootPath, SafeTime(info), (int)FileAttributes.Directory));
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        long nextId = 2;
        var stack = new Stack<(DirectoryInfo Dir, int Index)>();
        stack.Push((info, 0));
        var lastReport = Environment.TickCount64;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, idx) = stack.Pop();
            IEnumerable<FileSystemInfo> items;
            try
            {
                items = dir.EnumerateFileSystemInfos("*", opts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            try
            {
                foreach (var fsi in items)
                {
                    var attrs = fsi.Attributes;
                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        var child = input.Dirs.Count;
                        input.Dirs.Add(new InputDir(nextId++, idx, fsi.Name, SafeTime(fsi), (int)attrs));
                        if ((attrs & FileAttributes.ReparsePoint) == 0)
                        {
                            stack.Push(((DirectoryInfo)fsi, child));
                        }
                    }
                    else
                    {
                        var size = ((FileInfo)fsi).Length;
                        var alloc = (size + DefaultCluster - 1) / DefaultCluster * DefaultCluster;
                        input.Files.Add(new InputFile(nextId++, idx, fsi.Name, size, alloc, SafeTime(fsi), (int)attrs));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 열거 중에 사라진 폴더 등: 지금까지 읽은 것만 둔다.
            }

            if (Environment.TickCount64 - lastReport > 250)
            {
                lastReport = Environment.TickCount64;
                progress?.Report(new AnalysisProgress(0.05, $"폴더 읽는 중… 파일 {input.Files.Count:N0}개"));
            }
        }

        progress?.Report(new AnalysisProgress(0.5, $"파일 {input.Files.Count:N0}개를 읽었습니다"));
        return input;
    }

    private static long SafeTime(FileSystemInfo f)
    {
        try
        {
            var t = f.LastWriteTimeUtc;
            return t.Year < 1602 ? 0 : t.ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }
}
