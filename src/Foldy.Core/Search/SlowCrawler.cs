using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;

namespace Foldy.Core.Search;

/// <summary>모은 목록의 한 줄.</summary>
public readonly record struct CrawlEntry(string Path, bool IsFolder, long Size, DateTime ModifiedUtc);

/// <summary>
/// 색인이 없는 드라이브(네트워크·회사 보안 드라이브, Google Drive, NTFS가 아닌 드라이브)의 파일 목록을 모아 둔 파일.
/// 한 줄에 하나: 경로 \t 폴더(1/0) \t 크기 \t 수정시각(UTC ticks). 찾을 때는 파일을 앞에서부터 읽으며 이름을 맞춘다
/// (백만 줄도 1초 안). 다 모으면 새 파일로 바꿔 넣어, 모으는 도중에도 예전 목록으로 찾을 수 있다.
/// </summary>
public sealed class CrawlStore(string file)
{
    public string FilePath { get; } = file;

    private readonly Dictionary<string, CrawlEntry> _added = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _delta = new();

    /// <summary>Windows가 알려 준 변화: 새로 생기거나 이름이 바뀐 것 (다음에 다 모을 때까지 메모리에).</summary>
    public void NoteAdded(CrawlEntry e)
    {
        lock (_delta)
        {
            _added[e.Path] = e;
            _removed.Remove(e.Path);
        }
    }

    /// <summary>Windows가 알려 준 변화: 지워지거나 다른 이름으로 바뀐 것.</summary>
    public void NoteRemoved(string path)
    {
        lock (_delta)
        {
            _added.Remove(path);
            _removed.Add(path);
        }
    }

    /// <summary>다 모은 목록으로 바꿔 넣었으면 메모리의 변화는 버린다 (새 목록에 이미 들어 있다).</summary>
    internal void ClearDelta()
    {
        lock (_delta)
        {
            _added.Clear();
            _removed.Clear();
        }
    }

    public long Count { get; private set; } = -1;

    public bool Exists => File.Exists(FilePath);

    public DateTime? CompletedUtc => Exists ? File.GetLastWriteTimeUtc(FilePath) : null;

    /// <summary>
    /// 이름(마지막 부분)에 <paramref name="query"/>가 들어 있는 항목. 띄어 쓴 낱말은 모두 들어 있어야 한다.
    /// 다 모은 목록과, 지금 모으는 중인 목록(.part)을 함께 찾는다 (색인 중에도 모은 데까지는 찾는다, 사용자 2026-11-21).
    /// </summary>
    public IEnumerable<CrawlEntry> Search(string query, int max, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = 0;
        List<CrawlEntry> added;
        HashSet<string> removed;
        lock (_delta)
        {
            added = [.. _added.Values];
            removed = [.. _removed];
        }

        var words = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var e in added.Where(e => words.All(w => System.IO.Path.GetFileName(e.Path).Contains(w, StringComparison.OrdinalIgnoreCase))))
        {
            if (seen.Add(e.Path))
            {
                yield return e;
                if (++found >= max)
                {
                    yield break;
                }
            }
        }

        seen.UnionWith(removed); // 지워진 것은 목록에 있어도 보이지 않게
        foreach (var f in new[] { FilePath + ".part", FilePath })
        {
            if (!File.Exists(f))
            {
                continue;
            }

            foreach (var e in SearchFile(f, query, ct))
            {
                if (seen.Add(e.Path))
                {
                    yield return e;
                    if (++found >= max)
                    {
                        yield break;
                    }
                }
            }
        }
    }

    private static IEnumerable<CrawlEntry> SearchFile(string file, string query, CancellationToken ct)
    {
        var words = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        FileStream fs;
        try
        {
            fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        var n = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            if ((++n & 4095) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0)
            {
                continue;
            }

            var slash = line.LastIndexOf('\\', tab - 1);
            var name = line.AsSpan(slash + 1, tab - slash - 1);
            var ok = true;
            foreach (var w in words)
            {
                if (!name.Contains(w, StringComparison.OrdinalIgnoreCase))
                {
                    ok = false;
                    break;
                }
            }

            if (ok && Parse(line) is { } e)
            {
                yield return e;
            }
        }
    }

    public long CountLines()
    {
        if (!Exists)
        {
            Count = 0;
            return 0;
        }

        long c = 0;
        using var reader = new StreamReader(new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is not null)
        {
            c++;
        }

        Count = c;
        return c;
    }

    internal static string Format(CrawlEntry e) =>
        string.Concat(e.Path, "\t", e.IsFolder ? "1" : "0", "\t", e.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), "\t", e.ModifiedUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal static CrawlEntry? Parse(string line)
    {
        var p = line.Split('\t');
        if (p.Length < 4 || !long.TryParse(p[2], out var size) || !long.TryParse(p[3], out var ticks))
        {
            return null;
        }

        return new CrawlEntry(p[0], p[1] == "1", size, new DateTime(ticks, DateTimeKind.Utc));
    }

    /// <summary>다 모은 목록을 한 번에 바꿔 넣는다 (찾는 도중이어도 안전하게).</summary>
    internal void Replace(string tempFile, long count)
    {
        File.Move(tempFile, FilePath, overwrite: true);
        Count = count;
        ClearDelta();
    }
}

/// <summary>모으는 상태 (사이드바 한 줄).</summary>
public sealed record CrawlStatus(string Drive, bool Running, long Found, bool Complete, DateTime? CompletedUtc, string? Error = null);

/// <summary>
/// 색인 없는 드라이브를 "살살" 모은다 (사용자 2026-11-21: "회사 드라이브도 부하 주지 않게 살살 수집해 줘").
/// 폴더를 하나 열 때마다, 여는 데 걸린 시간의 몇 배(최소 <see cref="MinPause"/>)를 쉰다 → 느린 드라이브일수록 저절로 천천히.
/// <see cref="ShouldPause"/>가 참이면(복사 같은 작업 중) 기다린다. 숨김·시스템 폴더, 링크(정션·심볼릭 링크) 안은 들어가지 않는다.
/// 다 모으면 <see cref="RescanAfter"/> 뒤에 다시 모은다.
/// </summary>
public sealed class SlowCrawler : IDisposable
{
    public static TimeSpan MinPause { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>다 모은 뒤 다시 모으기까지 (다시 모을 때는 바뀐 폴더만 읽어서 가볍다).</summary>
    public static TimeSpan RescanAfter { get; set; } = TimeSpan.FromHours(1);

    /// <summary>처음 쉬는 비율: 폴더를 여는 데 걸린 시간의 몇 배를 쉬나. 모으는 동안 드라이브 속도에 맞춰 0.5~8배로 바뀐다.</summary>
    public static double PauseFactor { get; set; } = 2;

    private readonly string _dir;
    private readonly Func<IReadOnlyList<string>> _drives;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, CrawlStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private Thread? _thread;

    /// <param name="dir">목록을 둘 폴더.</param>
    /// <param name="drives">모을 드라이브 뿌리(예: "K:\")를 돌려준다. 바뀌면 다음 차례부터 따른다.</param>
    public SlowCrawler(string dir, Func<IReadOnlyList<string>> drives)
    {
        _dir = dir;
        _drives = drives;
        Directory.CreateDirectory(dir);
    }

    /// <summary>참이면 잠시 쉰다 (복사·이동 같은 작업 중).</summary>
    public Func<bool>? ShouldPause { get; set; }

    public event Action<CrawlStatus>? Changed;

    private readonly Dictionary<string, CrawlStore> _stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    // 변경 알림이 넘치거나 끊긴 드라이브: 1시간을 기다리지 않고 곧 다시 모은다 (바뀐 폴더만 읽어서 가볍다)
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly AutoResetEvent _kick = new(false);

    /// <summary>변경 알림이 넘쳤을 때처럼, 이 드라이브를 곧 다시 모으라고 표시한다 (시험에서도 쓴다).</summary>
    public void MarkDirty(string drive)
    {
        lock (_gate)
        {
            _dirty.Add(Root(drive));
        }

        _kick.Set();
    }

    /// <summary>곧 다시 모아야 하는 드라이브인가 (시험·진단용).</summary>
    public bool IsDirty(string drive)
    {
        lock (_gate)
        {
            return _dirty.Contains(Root(drive));
        }
    }

    public CrawlStore StoreFor(string drive)
    {
        lock (_gate)
        {
            var root = Root(drive);
            if (!_stores.TryGetValue(root, out var s))
            {
                _stores[root] = s = new CrawlStore(Path.Combine(_dir, KeyOf(drive) + ".txt"));
            }

            return s;
        }
    }

    /// <summary>
    /// Windows의 변경 알림(만들기·지우기·이름 바꾸기)을 받아 바로 반영한다 (사용자 2026-11-21: "생성·삭제 이벤트를 받을 수 있나?").
    /// 네트워크 공유·Google Drive는 알림이 빠지거나 늦을 수 있어, 주기적으로 다시 살살 모으는 것으로 메운다.
    /// </summary>
    private void Watch(string root)
    {
        lock (_gate)
        {
            if (_watchers.ContainsKey(root))
            {
                return;
            }
        }

        try
        {
            var store = StoreFor(root);
            var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
            void Added(string path)
            {
                try
                {
                    var info = new FileInfo(path);
                    var isDir = (info.Attributes & FileAttributes.Directory) != 0;
                    store.NoteAdded(new CrawlEntry(path, isDir, isDir ? 0 : info.Length, info.LastWriteTimeUtc));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            w.Created += (_, e) => Added(e.FullPath);
            w.Deleted += (_, e) => store.NoteRemoved(e.FullPath);
            w.Renamed += (_, e) =>
            {
                store.NoteRemoved(e.OldFullPath);
                Added(e.FullPath);
            };
            w.Error += (_, _) => MarkDirty(root); // 알림이 넘치거나 끊기면(SMB·Google Drive에서 생길 수 있다) 곧 다시 모아 메운다
            w.EnableRaisingEvents = true;
            lock (_gate)
            {
                _watchers[root] = w;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            // 알림을 지원하지 않는 드라이브: 다시 모으기만으로
        }
    }

    public CrawlStatus? StatusOf(string drive)
    {
        lock (_gate)
        {
            return _status.TryGetValue(Root(drive), out var s) ? s : null;
        }
    }

    public void Start()
    {
        _thread ??= new Thread(Loop) { IsBackground = true, Name = "SlowCrawler", Priority = ThreadPriority.BelowNormal };
        if (!_thread.IsAlive)
        {
            _thread.Start();
        }
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            var did = false;
            foreach (var d in _drives())
            {
                if (_cts.IsCancellationRequested)
                {
                    return;
                }

                var store = StoreFor(d);
                Watch(Root(d));
                bool dirty;
                lock (_gate)
                {
                    dirty = _dirty.Remove(Root(d));
                }

                if (!dirty && store.CompletedUtc is { } done && DateTime.UtcNow - done < RescanAfter)
                {
                    if (StatusOf(d) is null)
                    {
                        Report(new CrawlStatus(Root(d), false, store.Count >= 0 ? store.Count : store.CountLines(), true, done));
                    }

                    continue;
                }

                CrawlOnce(d, _cts.Token);
                did = true;
            }

            if (WaitHandle.WaitAny([_cts.Token.WaitHandle, _kick], did ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1)) == 0)
            {
                return;
            }
        }
    }

    /// <summary>폴더 하나의 기록: 그때의 수정 시각, 목록 파일 안 위치, 연속으로 안 바뀐 횟수.</summary>
    private sealed record DirMark(long MTime, long Offset, int Length, int Unchanged);

    /// <summary>지난번 모은 것과 비교한 이번 결과 (시험·진단용).</summary>
    public sealed record PassStats(long Found, int Listed, int Reused, int Skipped);

    /// <summary>마지막으로 모은 결과 (시험·진단용).</summary>
    public PassStats? LastPass { get; private set; }

    /// <summary>
    /// 드라이브 하나를 끝까지 모은다 (시험에서도 쓴다). 취소되면 모으던 것은 버리고 예전 목록을 둔다.
    /// <para>부하는 적게, 그래도 빨리 (사용자 2026-11-21):</para>
    /// <list type="bullet">
    /// <item>폴더 목록은 한 번에 크게(64KB) 받아 오고 파일을 하나씩 열지 않는다. 요청은 한 번에 하나만.</item>
    /// <item>속도 조절(TCP처럼): 폴더를 읽은 시간을 평소와 비교해, 평소처럼 빠르면 쉬는 비율을 조금씩 줄이고(빨리), 느려지면 바로 두 배로(살살).</item>
    /// <item>다시 모을 때는 폴더의 수정 시각만 먼저 묻고, 같으면 지난 목록을 그대로 쓴다(파일이 생기거나 지워지거나 이름이 바뀌면 그 폴더의 시각이 바뀐다).</item>
    /// <item>오래 안 바뀐 폴더는 확인 간격을 늘린다(1·2·4·8·16번에 한 번).</item>
    /// </list>
    /// </summary>
    public long CrawlOnce(string drive, CancellationToken ct)
    {
        var root = Root(drive);
        var store = StoreFor(root);
        var temp = store.FilePath + ".part";
        var idxPath = store.FilePath + ".idx";
        var idxTemp = idxPath + ".part";
        long found = 0;
        int listed = 0, reused = 0, skipped = 0;
        var (pass, old) = LoadIndex(idxPath);
        pass++;
        Report(new CrawlStatus(root, true, 0, false, store.CompletedUtc));
        double factor = PauseFactor, baseline = -1;

        void Breathe(TimeSpan took)
        {
            // 속도 조절: 평소보다 두 배 넘게 느리면 쉬는 비율을 두 배로, 아니면 조금씩 줄인다
            var ms = took.TotalMilliseconds;
            baseline = baseline < 0 ? ms : (0.9 * baseline) + (0.1 * ms);
            factor = ms > 2 * Math.Max(baseline, 1) ? Math.Min(8, factor * 2) : Math.Max(0.5, factor * 0.95);
            var pause = TimeSpan.FromTicks(Math.Max(MinPause.Ticks, (long)(took.Ticks * factor)));
            if (pause > TimeSpan.Zero && ct.WaitHandle.WaitOne(pause))
            {
                ct.ThrowIfCancellationRequested();
            }
        }

        try
        {
            FileStream? oldData = old.Count > 0 && store.Exists ? new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : null;
            try
            {
                var marks = new Dictionary<string, DirMark>(StringComparer.OrdinalIgnoreCase);
                using (var data = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    var stack = new Stack<string>();
                    stack.Push(root);
                    var lastReport = Stopwatch.StartNew();
                    while (stack.Count > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        while (ShouldPause?.Invoke() == true)
                        {
                            if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
                            {
                                ct.ThrowIfCancellationRequested();
                            }
                        }

                        var dir = stack.Pop();
                        var before = old.TryGetValue(dir, out var o) ? o : null;
                        byte[]? block = null;
                        var unchanged = 0;
                        long mtime = 0;
                        if (before is not null && oldData is not null)
                        {
                            // 오래 안 바뀐 폴더는 가끔만 확인한다 (unchanged 2 → 2번에 한 번, 3 → 4번에 한 번 … 최대 16번에 한 번)
                            var every = 1 << Math.Clamp(before.Unchanged - 1, 0, 4);
                            if (before.Unchanged >= 2 && pass % every != 0)
                            {
                                block = ReadBlock(oldData, before);
                                unchanged = before.Unchanged;
                                mtime = before.MTime;
                                skipped++;
                            }
                            else
                            {
                                var sw = Stopwatch.StartNew();
                                mtime = DirTime(dir);
                                Breathe(sw.Elapsed);
                                if (mtime != 0 && mtime == before.MTime)
                                {
                                    block = ReadBlock(oldData, before);
                                    unchanged = before.Unchanged + 1;
                                    reused++;
                                }
                            }
                        }

                        var offset = data.Position;
                        if (block is not null)
                        {
                            data.Write(block);
                            foreach (var line in Encoding.UTF8.GetString(block).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                            {
                                found++;
                                if (CrawlStore.Parse(line.TrimEnd('\r')) is { IsFolder: true } e)
                                {
                                    stack.Push(e.Path);
                                }
                            }
                        }
                        else
                        {
                            var sw = Stopwatch.StartNew();
                            List<CrawlEntry> entries;
                            try
                            {
                                if (mtime == 0)
                                {
                                    mtime = DirTime(dir);
                                }

                                entries = [.. List(dir)];
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                entries = [];
                            }

                            listed++;
                            var sb = new StringBuilder();
                            foreach (var e in entries)
                            {
                                sb.Append(CrawlStore.Format(e)).Append('\n');
                                found++;
                                if (e.IsFolder)
                                {
                                    stack.Push(e.Path);
                                }
                            }

                            data.Write(Encoding.UTF8.GetBytes(sb.ToString()));
                            Breathe(sw.Elapsed);
                        }

                        marks[dir] = new DirMark(mtime, offset, (int)(data.Position - offset), unchanged);
                        if (lastReport.Elapsed > TimeSpan.FromSeconds(1))
                        {
                            data.Flush(); // 모으는 중에도 찾을 수 있게
                            Report(new CrawlStatus(root, true, found, false, store.CompletedUtc));
                            lastReport.Restart();
                        }
                    }
                }

                oldData?.Dispose();
                oldData = null;
                SaveIndex(idxTemp, pass, marks);
                store.Replace(temp, found);
                File.Move(idxTemp, idxPath, overwrite: true);
            }
            finally
            {
                oldData?.Dispose();
            }

            LastPass = new PassStats(found, listed, reused, skipped);
            Report(new CrawlStatus(root, false, found, true, DateTime.UtcNow));
            return found;
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            TryDelete(idxTemp);
            Report(new CrawlStatus(root, false, store.Count, store.Exists, store.CompletedUtc));
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            TryDelete(idxTemp);
            Report(new CrawlStatus(root, false, store.Count, store.Exists, store.CompletedUtc, ex.Message));
            return found;
        }
    }

    private static long DirTime(string dir)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(dir).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private static byte[] ReadBlock(FileStream f, DirMark m)
    {
        var buf = new byte[m.Length];
        f.Seek(m.Offset, SeekOrigin.Begin);
        f.ReadExactly(buf);
        return buf;
    }

    private static (int Pass, Dictionary<string, DirMark> Marks) LoadIndex(string path)
    {
        var marks = new Dictionary<string, DirMark>(StringComparer.OrdinalIgnoreCase);
        var pass = 0;
        try
        {
            if (!File.Exists(path))
            {
                return (0, marks);
            }

            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (line.StartsWith("#pass ", StringComparison.Ordinal))
                {
                    _ = int.TryParse(line.AsSpan(6), out pass);
                    continue;
                }

                var p = line.Split('\t');
                if (p.Length == 5 && long.TryParse(p[1], out var mt) && long.TryParse(p[2], out var off) && int.TryParse(p[3], out var len) && int.TryParse(p[4], out var un))
                {
                    marks[p[0]] = new DirMark(mt, off, len, un);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            marks.Clear();
        }

        return (pass, marks);
    }

    private static void SaveIndex(string path, int pass, Dictionary<string, DirMark> marks)
    {
        using var w = new StreamWriter(path, append: false, new UTF8Encoding(false));
        w.Write("#pass ");
        w.WriteLine(pass.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (dir, m) in marks)
        {
            w.WriteLine(string.Join('\t', dir, m.MTime.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                m.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Unchanged.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    // 목록은 한 번에 크게 받는다 (네트워크 왕복을 줄인다)
    private static readonly EnumerationOptions Flat = new() { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, ReturnSpecialDirectories = false, BufferSize = 64 * 1024 };

    private static IEnumerable<CrawlEntry> List(string dir) =>
        new FileSystemEnumerable<CrawlEntry?>(dir, (ref FileSystemEntry e) =>
            (e.Attributes & FileAttributes.ReparsePoint) != 0 && e.IsDirectory
                ? null // 링크 안은 들어가지 않는다 (다른 곳을 두 번 세거나 끝없이 돌지 않게)
                : new CrawlEntry(e.ToFullPath(), e.IsDirectory, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcDateTime), Flat)
            .OfType<CrawlEntry>();

    private void Report(CrawlStatus s)
    {
        lock (_gate)
        {
            _status[s.Drive] = s;
        }

        Changed?.Invoke(s);
    }

    private static string Root(string drive) => drive.Length == 2 && drive[1] == ':' ? drive + "\\" : drive;

    private static string KeyOf(string drive)
    {
        var r = Root(drive).TrimEnd('\\').Replace(':', '_').Replace('\\', '_');
        string label;
        try
        {
            label = new DriveInfo(Root(drive)).VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            label = "";
        }

        // 같은 글자에 다른 드라이브가 연결되면 다른 목록이 되게 이름표도 넣는다
        var safe = string.Concat(label.Where(char.IsLetterOrDigit));
        return safe.Length > 0 ? $"{r}-{safe}" : r;
    }

    private static void TryDelete(string f)
    {
        try
        {
            File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var w in _watchers.Values)
            {
                w.Dispose();
            }

            _watchers.Clear();
        }

        _cts.Cancel();
        _thread?.Join(1000);
        _cts.Dispose();
        _kick.Dispose();
    }
}
