using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Foldy.Core.FileSystem;

/// <summary>지우기 전에 센 파일 수. <see cref="Capped"/>이면 시간 안에 다 못 세서 "이보다 많음".</summary>
public readonly record struct DeleteCount(long Files, long Bytes, bool Capped);

/// <summary>빨리 지우기 결과. 실패한 항목은 <see cref="Failed"/>에 몇 개만 (전체 수는 <see cref="FailedCount"/>).</summary>
public sealed record FastDeleteResult(long DeletedFiles, long FailedCount, IReadOnlyList<string> Failed, bool Canceled);

/// <summary>
/// 휴지통을 거치지 않고 병렬로 바로 지운다 (파일이 아주 많은 폴더용, 사용자가 고른 뒤에만).
/// - 폴더마다 안의 파일을 여러 스레드로 지우고, 비면 가장 깊은 폴더부터 지운다.
/// - 읽기 전용·숨김·시스템 속성은 풀고 지운다.
/// - 링크(정션·심볼릭 링크)는 링크만 지우고 안으로 들어가지 않는다 (다른 곳의 원본을 지우지 않게).
/// - 긴 경로(260자 넘음)는 .NET이 \\?\ 형식으로 처리한다.
/// - 취소하면 거기서 멈춘다. 이미 지운 것은 되살릴 수 없다.
/// </summary>
public static class FastDelete
{
    /// <summary>이만큼 넘는 파일을 지우려 하면 휴지통/빨리 지우기를 묻는다 (DECISIONS.md 2026-11-20).</summary>
    public const long AskThreshold = 5_000;

    private const int MaxFailedShown = 20;

    /// <summary>
    /// 지울 파일 수를 센다 (링크 안은 세지 않음). <paramref name="budget"/> 안에 못 끝내면 거기까지 세고 Capped.
    /// </summary>
    public static DeleteCount Count(IEnumerable<string> paths, TimeSpan budget, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + budget;
        long files = 0, bytes = 0;
        var stack = new Stack<string>();
        foreach (var p in paths)
        {
            var info = TryInfo(p);
            if (info is null)
            {
                continue;
            }

            if (info is FileInfo fi)
            {
                files++;
                bytes += fi.Length;
            }
            else if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                stack.Push(p);
            }
        }

        var n = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if ((++n & 31) == 0 && DateTime.UtcNow > until)
            {
                return new DeleteCount(files, bytes, true);
            }

            foreach (var (path, attr, len) in Entries(stack.Pop()))
            {
                if ((attr & FileAttributes.Directory) == 0)
                {
                    files++;
                    bytes += len;
                }
                else if ((attr & FileAttributes.ReparsePoint) == 0)
                {
                    stack.Push(path);
                }
            }
        }

        return new DeleteCount(files, bytes, false);
    }

    /// <summary>
    /// 지우면 안 되는 곳인지 (드라이브 맨 위, Windows·Program Files, 사용자 폴더 자체). 빨리 지우기는 이런 곳을 거절한다.
    /// </summary>
    public static bool IsProtected(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var f in new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments,
        })
        {
            var s = Environment.GetFolderPath(f);
            if (s.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(s), full, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return users is not null && string.Equals(Path.TrimEndingDirectorySeparator(users), full, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>네트워크 드라이브는 동시에 4개만, 로컬은 CPU 수의 2배(최대 16).</summary>
    public static int DefaultParallelism(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return 4;
            }

            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is not null && new DriveInfo(root).DriveType == DriveType.Network)
            {
                return 4;
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 4;
        }

        return Math.Clamp(Environment.ProcessorCount * 2, 2, 16);
    }

    /// <summary>
    /// 지운다. <paramref name="progress"/>는 (지운 파일 수, 지금 폴더)로 자주 불린다(스레드에서).
    /// 보호된 경로가 섞여 있으면 아무것도 지우지 않고 예외.
    /// </summary>
    public static FastDeleteResult Delete(IReadOnlyList<string> paths, Action<long, string?>? progress = null, int parallelism = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var bad = paths.FirstOrDefault(IsProtected);
        if (bad is not null)
        {
            throw new InvalidOperationException($"이 위치는 빨리 지우기로 지울 수 없어요: {bad}");
        }

        if (parallelism <= 0)
        {
            parallelism = paths.Count == 0 ? 1 : DefaultParallelism(paths[0]);
        }

        long deleted = 0, failedCount = 0;
        var failed = new ConcurrentQueue<string>();
        void Fail(string p)
        {
            Interlocked.Increment(ref failedCount);
            if (failed.Count < MaxFailedShown)
            {
                failed.Enqueue(p);
            }
        }

        var po = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct };
        var levels = new List<List<string>>();
        var level = new List<string>();
        try
        {
            foreach (var p in paths)
            {
                var info = TryInfo(p);
                if (info is null)
                {
                    continue;
                }

                if (info is FileInfo || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (DeleteEntry(p, info.Attributes))
                    {
                        if (info is FileInfo)
                        {
                            deleted++;
                        }
                    }
                    else
                    {
                        Fail(p);
                    }
                }
                else
                {
                    level.Add(p);
                }
            }

            // 1) 위에서 아래로: 폴더마다 파일을 지우고 하위 폴더를 다음 층으로
            while (level.Count > 0)
            {
                levels.Add(level);
                var next = new ConcurrentBag<string>();
                Parallel.ForEach(level, po, dir =>
                {
                    progress?.Invoke(Interlocked.Read(ref deleted), dir);
                    List<(string Path, FileAttributes Attr, long Len)> entries;
                    try
                    {
                        entries = [.. Entries(dir, throwOnError: true)];
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        Fail(dir);
                        return;
                    }

                    var files = new List<(string, FileAttributes)>();
                    foreach (var (path, attr, _) in entries)
                    {
                        if ((attr & FileAttributes.Directory) != 0 && (attr & FileAttributes.ReparsePoint) == 0)
                        {
                            next.Add(path);
                        }
                        else
                        {
                            files.Add((path, attr));
                        }
                    }

                    // 파일이 많은 폴더는 그 안에서도 나눠 지운다
                    if (files.Count > 256)
                    {
                        Parallel.ForEach(files, po, f => DeleteFileCounted(f.Item1, f.Item2));
                    }
                    else
                    {
                        foreach (var (f, a) in files)
                        {
                            po.CancellationToken.ThrowIfCancellationRequested();
                            DeleteFileCounted(f, a);
                        }
                    }
                });
                level = [.. next];
            }

            // 2) 아래에서 위로: 빈 폴더 지우기
            for (var i = levels.Count - 1; i >= 0; i--)
            {
                Parallel.ForEach(levels[i], po, dir =>
                {
                    if (!DeleteEntry(dir, FileAttributes.Directory) && Directory.Exists(dir))
                    {
                        Fail(dir);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            return new FastDeleteResult(deleted, failedCount, [.. failed], true);
        }

        progress?.Invoke(deleted, null);
        return new FastDeleteResult(deleted, failedCount, [.. failed], false);

        void DeleteFileCounted(string path, FileAttributes attr)
        {
            if (DeleteEntry(path, attr))
            {
                var d = Interlocked.Increment(ref deleted);
                if ((d & 255) == 0)
                {
                    progress?.Invoke(d, null);
                }
            }
            else
            {
                Fail(path);
            }
        }
    }

    /// <summary>파일 하나, 링크 하나, 빈 폴더 하나를 지운다 (속성은 풀고). 이미 없으면 성공.</summary>
    private static bool DeleteEntry(string path, FileAttributes attr)
    {
        try
        {
            if ((attr & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
            {
                File.SetAttributes(path, (attr & FileAttributes.Directory) != 0 ? FileAttributes.Directory : FileAttributes.Normal);
            }

            if ((attr & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path, recursive: false); // 링크면 링크만 지워진다
            }
            else
            {
                File.Delete(path);
            }

            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static FileSystemInfo? TryInfo(string p)
    {
        try
        {
            FileSystemInfo d = new DirectoryInfo(p);
            if (d.Exists)
            {
                return d;
            }

            var f = new FileInfo(p);
            return f.Exists ? f : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static readonly EnumerationOptions Flat = new() { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };

    private static IEnumerable<(string Path, FileAttributes Attr, long Len)> Entries(string dir, bool throwOnError = false)
    {
        IEnumerable<(string, FileAttributes, long)> e = new FileSystemEnumerable<(string, FileAttributes, long)>(
            dir, (ref FileSystemEntry x) => (x.ToFullPath(), x.Attributes, x.Length), Flat);
        if (throwOnError)
        {
            return e;
        }

        try
        {
            return [.. e];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
