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

    /// <summary>
    /// 다 모은 목록 전부 + Windows가 알려 준 변화(새로 생긴 것은 더하고, 지워진 것과 그 안은 뺀다).
    /// 디스크 분석이 드라이브를 다시 읽지 않고 쓴다 (사용자 2026-11-23: "K 드라이브 분석이 폴더 읽는 중으로 나온다").
    /// 모으는 중인 목록(.part)은 쓰지 않는다 (아직 반쪽이라 크기가 작게 보인다).
    /// </summary>
    public IEnumerable<CrawlEntry> ReadAll(CancellationToken ct = default)
    {
        List<CrawlEntry> added;
        string[] removed;
        lock (_delta)
        {
            added = [.. _added.Values];
            removed = [.. _removed];
        }

        bool Gone(string path)
        {
            foreach (var r in removed)
            {
                if (path.StartsWith(r, StringComparison.OrdinalIgnoreCase) && (path.Length == r.Length || path[r.Length] == '\\'))
                {
                    return true;
                }
            }

            return false;
        }

        var addedPaths = new HashSet<string>(added.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
        FileStream fs;
        try
        {
            fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (var reader = new StreamReader(fs, Encoding.UTF8))
        {
            string? line;
            var n = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                if ((++n & 4095) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                if (Parse(line) is { } e && !addedPaths.Contains(e.Path) && (removed.Length == 0 || !Gone(e.Path)))
                {
                    yield return e;
                }
            }
        }

        foreach (var a in added)
        {
            if (removed.Length == 0 || !Gone(a.Path))
            {
                yield return a;
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
public sealed record CrawlStatus(string Drive, bool Running, long Found, bool Complete, DateTime? CompletedUtc, string? Error = null)
{
    /// <summary>복사 같은 작업이 끝나기를 기다리며 쉬는 중.</summary>
    public bool Paused { get; init; }

    /// <summary>모으는 중의 빠르기 (드라이브에 마우스를 올리면 보인다: 느린 까닭을 찾으려고).</summary>
    public CrawlSpeed? Speed { get; init; }
}

/// <summary>모으는 빠르기: 이번에 1분에 몇 개, 읽은 폴더 수, 폴더 하나 답에 걸린 시간, 같이 읽는 수, 쉰 시간의 몫(0~1), 같이 읽는 수를 재 본 결과.</summary>
public sealed record CrawlSpeed(double PerMinute, long Folders, double AskMs, int Width, double RestShare, string? Tried = null);

/// <summary>
/// 색인 없는 드라이브를 "살살" 모은다 (사용자 2026-11-21: "회사 드라이브도 부하 주지 않게 살살 수집해 줘").
/// 가까운 드라이브는 다른 프로그램에 비켜 주며(<see cref="GiveWay"/>) 쉬지 않고 읽고, 먼 드라이브는 짧게 쉬며 읽는다(<see cref="CrawlPace"/>).
/// <see cref="ShouldPause"/>가 참이면(복사 같은 작업 중) 기다린다. Windows 폴더(휴지통·시스템 볼륨 정보)와 숨김+시스템 파일, 링크(정션·심볼릭 링크) 안은 들어가지 않는다.
/// 다 모으면 <see cref="RescanEvery"/> 뒤에 다시 모으고, <see cref="RequestNow"/>로 지금 모으게 할 수도 있다.
/// </summary>
public sealed class SlowCrawler : IDisposable
{
    /// <summary>클라우드 드라이브에서 폴더마다 적어도 이만큼 쉰다 (모아서 한 번에).</summary>
    public static TimeSpan MinPause { get; set; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// 네트워크 드라이브에서 폴더마다 적어도 쉬는 시간. 클라우드 드라이브는 이 PC에서 도는 프로그램이 답하지만,
    /// 네트워크 드라이브는 여럿이 쓰는 서버라 더 쉰다 (PC 여러 대가 함께 모아도 몰리지 않게).
    /// </summary>
    public static TimeSpan MinNetworkPause { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>다 모은 뒤 다시 모으기까지 (다시 모을 때는 바뀐 폴더만 읽어서 가볍다).</summary>
    public static TimeSpan RescanAfter { get; set; } = TimeSpan.FromHours(1);

    /// <summary>한 드라이브에서 같이 읽는 폴더 수의 최대 (먼 드라이브에서 4·8·16개를 재 보고 가장 빠른 수로, <see cref="CrawlPace.Tries"/>). 가까운 드라이브는 하나씩.</summary>
    public static int MaxWidth => CrawlPace.Tries[^1];

    /// <summary>먼저 모을 폴더 (자주 쓰는 폴더·최근 폴더). 그 폴더로 가는 길을 먼저 읽어 찾기에 빨리 나온다.</summary>
    public Func<IReadOnlyList<string>>? PreferFirst { get; set; }

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
        IsNear = LooksNear;
        Directory.CreateDirectory(dir);
    }

    /// <summary>참이면 잠시 쉰다 (복사·이동 같은 작업 중).</summary>
    public Func<bool>? ShouldPause { get; set; }

    /// <summary>
    /// 참이면 그 드라이브는 다시 모을 때도 늘 모든 폴더를 읽는다. 안에 파일이 생겨도 폴더 수정 시각이 안 바뀌는 드라이브(Google Drive)는
    /// 시각만 보면 변화를 놓친다 (2026-11-23 노트북에서 재 봄: 나중에 뭔가 생긴 폴더 19개 중 11개가 안 바뀜).
    /// </summary>
    public Func<string, bool>? AlwaysReadAll { get; set; }

    /// <summary>
    /// 이 PC에 바로 꽂힌 드라이브(디스크·USB·SD)인가. 네트워크와, Windows가 "고정 드라이브"라고 하는 클라우드 드라이브(Google Drive)는 아니다.
    /// 시험에서 바꾼다.
    /// </summary>
    internal Func<string, bool> IsNear { get; set; }

    /// <summary>여럿이 쓰는 서버의 드라이브인가 (네트워크 드라이브, \\서버\공유). 시험에서 바꾼다.</summary>
    internal Func<string, bool> IsNetwork { get; set; } = root =>
    {
        try
        {
            return root.StartsWith(@"\\", StringComparison.Ordinal)
                || (Path.GetPathRoot(root) is { Length: > 0 } drive && new DriveInfo(drive).DriveType == DriveType.Network);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    };

    private bool LooksNear(string root)
    {
        try
        {
            return !root.StartsWith(@"\\", StringComparison.Ordinal)
                && Path.GetPathRoot(root) is { Length: > 0 } drive
                && new DriveInfo(drive).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram
                && AlwaysReadAll?.Invoke(root) != true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public event Action<CrawlStatus>? Changed;

    private readonly Dictionary<string, CrawlStore> _stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    // 변경 알림이 넘치거나 끊긴 드라이브: 1시간을 기다리지 않고 곧 다시 모은다 (바뀐 폴더만 읽어서 가볍다)
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);

    // 싼 신호로 "바뀐 것 없음"을 마지막으로 확인한 때 (정기 색인 주기를 여기서부터 센다)
    private readonly Dictionary<string, DateTime> _quiet = new(StringComparer.OrdinalIgnoreCase);
    private readonly AutoResetEvent _kick = new(false);

    // 사용자가 [지금 색인하기]를 누른 드라이브 (누른 순서대로)
    private readonly List<string> _requested = [];
    private HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private TimeSpan? _rescanEvery = RescanAfter;

    /// <summary>
    /// 정기 색인 주기: 마지막으로 다 모은 뒤 이만큼 지나면 다시 모은다. null이면 저절로 모으지 않고 <see cref="RequestNow"/>로만 모은다.
    /// 다 모은 시각은 파일에 남으므로 Foldy를 껐다 켜도 이어서 센다.
    /// </summary>
    public TimeSpan? RescanEvery
    {
        get
        {
            lock (_gate)
            {
                return _rescanEvery;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_rescanEvery == value)
                {
                    return;
                }

                _rescanEvery = value;
            }

            _kick.Set(); // 바꾼 주기로 바로 다시 본다
        }
    }

    /// <summary>
    /// 지금 모은다 (사용자 2026-11-23: "수동으로 색인 시작하는 기능"). 다른 드라이브를 모으는 중이면 끊지 않고 그다음 차례다.
    /// 이때는 지난 목록을 믿지 않고 모든 폴더를 다시 읽는다 (폴더 시각으로 알 수 없는 크기 변화까지 맞춘다).
    /// 이미 그 드라이브를 모으는 중이거나 차례를 기다리면 false.
    /// </summary>
    public bool RequestNow(string drive)
    {
        var root = Root(drive);
        lock (_gate)
        {
            if (_requested.Contains(root, StringComparer.OrdinalIgnoreCase) || (_status.TryGetValue(root, out var s) && s.Running))
            {
                return false;
            }

            _requested.Add(root);
        }

        _kick.Set();
        return true;
    }

    /// <summary>[지금 색인하기]를 눌러 차례를 기다리는 드라이브인가.</summary>
    public bool IsRequested(string drive)
    {
        lock (_gate)
        {
            return _requested.Contains(Root(drive), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>모으는 대상인 드라이브 (마지막으로 살펴본 때). 화면 스레드에서 물어도 드라이브를 건드리지 않는다.</summary>
    public IReadOnlyList<string> KnownDrives
    {
        get
        {
            lock (_gate)
            {
                return [.. _known];
            }
        }
    }

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

    /// <summary>지금 모으고 있는 드라이브 (없으면 null). 한 번에 하나씩 모은다.</summary>
    public CrawlStatus? Current()
    {
        lock (_gate)
        {
            return _status.Values.FirstOrDefault(s => s.Running);
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

    private int _idlePasses;

    /// <summary>
    /// 모을 것이 없어 쉬고 지나간 횟수 (모든 드라이브를 살펴봤는데 시작한 것도, 모으는 중인 것도 없음).
    /// 창 없이 뜬 Foldy(--gather)는 이 값이 늘면 할 일을 다 한 것으로 보고 끝난다.
    /// </summary>
    public int IdlePasses => Volatile.Read(ref _idlePasses);

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            var did = false;
            var drives = RefreshDrives();
            did |= CrawlRequested(drives);
            foreach (var d in drives)
            {
                if (_cts.IsCancellationRequested)
                {
                    return;
                }

                var store = StoreFor(d);
                var root = Root(d);
                Watch(root);
                if (IsCrawling(root))
                {
                    continue; // 이 드라이브는 지금 모으는 중
                }

                bool due, onSchedule = false;
                var done = store.CompletedUtc;
                var unfinished = HasUnfinished(root); // 모으다 끊긴 것은 주기를 기다리지 않고 이어서 한다 (직접 누를 때만 설정이어도: 사용자가 시작한 것)
                var retryEmpty = false;
                lock (_gate)
                {
                    retryEmpty = !_retriedEmpty.Contains(root);
                }

                retryEmpty = retryEmpty && LooksWronglyEmpty(root, store);
                lock (_gate)
                {
                    if (retryEmpty)
                    {
                        _retriedEmpty.Add(root);
                    }

                    due = unfinished || retryEmpty;
                    // 저절로 모으지 않는 설정이면 알림이 넘친 것도 표시만 해 둔다 (주기를 다시 켜면 그때 모은다)
                    if (!due && _rescanEvery is { } every)
                    {
                        var last = done is { } dd && _quiet.TryGetValue(root, out var q) && q > dd ? q : done;
                        if (_dirty.Remove(root) || done is null)
                        {
                            due = true;
                        }
                        else if (DateTime.UtcNow - last!.Value >= every)
                        {
                            due = onSchedule = true;
                        }
                    }
                }

                if (onSchedule && LooksQuiet(root, done!.Value))
                {
                    // 남은 공간·맨 위 폴더 시각이 그대로: 이번 정기 색인은 건너뛴다
                    lock (_gate)
                    {
                        _quiet[root] = DateTime.UtcNow;
                    }

                    due = false;
                }

                if (!due)
                {
                    if (StatusOf(d) is null)
                    {
                        Report(new CrawlStatus(Root(d), false, done is null ? 0 : store.Count >= 0 ? store.Count : store.CountLines(), done is not null, done));
                    }

                    continue;
                }

                did |= TryStart(d, full: false);
            }

            if (!did && Current() is null)
            {
                Interlocked.Increment(ref _idlePasses);
            }

            if (WaitHandle.WaitAny([_cts.Token.WaitHandle, _kick], did ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1)) == 0)
            {
                return;
            }
        }
    }

    private IReadOnlyList<string> RefreshDrives()
    {
        var drives = _drives();
        var now = new HashSet<string>(drives.Select(Root), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (!now.SetEquals(_known))
            {
                _known = now;
            }
        }

        return drives;
    }

    /// <summary>
    /// [지금 색인하기]를 누른 드라이브를 누른 순서대로 시작한다. 그사이 뽑힌 드라이브는 넘어간다.
    /// 그 드라이브를 이미 모으는 중이거나 한꺼번에 모으는 드라이브 수가 꽉 찼으면 차례를 기다린다.
    /// </summary>
    private bool CrawlRequested(IReadOnlyList<string> drives)
    {
        var did = false;
        List<string> queue;
        lock (_gate)
        {
            queue = [.. _requested];
        }

        foreach (var next in queue)
        {
            if (_cts.IsCancellationRequested)
            {
                break;
            }

            var ready = drives.Any(d => string.Equals(Root(d), next, StringComparison.OrdinalIgnoreCase));
            if (ready && (IsCrawling(next) || CrawlingCount() >= MaxDrivesAtOnce))
            {
                continue; // 차례를 기다린다
            }

            if (ready)
            {
                Watch(next);
                // 차례 기다림 → 모으는 중: 사이에 빈틈이 없게 상태를 먼저 바꾼 뒤 목록에서 뺀다
                Report(new CrawlStatus(next, true, 0, false, StoreFor(next).CompletedUtc));
            }

            lock (_gate)
            {
                _requested.Remove(next);
                _dirty.Remove(next);
            }

            if (ready)
            {
                did |= TryStart(next, full: true);
            }
        }

        return did;
    }

    /// <summary>
    /// 한꺼번에 모으는 드라이브 수의 최대. 드라이브끼리는 서로 다른 장치·서버라 같이 모아도 한 곳에 몰리지 않는다
    /// (사용자 2026-11-24: K:를 모으는 동안 M:은 "K: 다음"으로 기다리기만 했다).
    /// </summary>
    public const int MaxDrivesAtOnce = 3;

    private readonly HashSet<string> _crawling = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Thread> _workers = [];

    private bool IsCrawling(string root)
    {
        lock (_gate)
        {
            return _crawling.Contains(Root(root));
        }
    }

    private int CrawlingCount()
    {
        lock (_gate)
        {
            return _crawling.Count;
        }
    }

    /// <summary>드라이브 하나를 따로 스레드에서 모으기 시작한다. 이미 모으는 중이거나 꽉 찼으면 false.</summary>
    private bool TryStart(string drive, bool full)
    {
        var root = Root(drive);
        Thread t;
        lock (_gate)
        {
            if (_crawling.Contains(root) || _crawling.Count >= MaxDrivesAtOnce || _cts.IsCancellationRequested)
            {
                return false;
            }

            _crawling.Add(root);
            t = new Thread(() =>
            {
                try
                {
                    CrawlOnce(drive, _cts.Token, full);
                }
                finally
                {
                    lock (_gate)
                    {
                        _crawling.Remove(root);
                    }

                    try
                    {
                        _kick.Set(); // 기다리던 드라이브가 있으면 바로 시작
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            })
            { IsBackground = true, Name = "SlowCrawler " + root, Priority = ThreadPriority.BelowNormal };
            _workers.RemoveAll(w => !w.IsAlive);
            _workers.Add(t);
        }

        t.Start();
        return true;
    }

    /// <summary>폴더 하나의 기록: 그때의 수정 시각, 목록 파일 안 위치, 연속으로 안 바뀐 횟수.</summary>
    private sealed record DirMark(long MTime, long Offset, int Length, int Unchanged);

    /// <summary>
    /// 지난번 모은 것과 비교한 이번 결과 (시험·진단용).
    /// Listed: 목록을 새로 읽은 폴더, Reused: 지난 목록을 그대로 쓴 폴더, Skipped: 그중 드라이브에 아무것도 묻지 않은 폴더(위 폴더 목록에 딸려 온 시각으로 판단),
    /// Resumed: 끊긴 곳부터 이어서 했나, TrustTimes: 이 드라이브의 폴더 시각을 믿었나.
    /// </summary>
    public sealed record PassStats(long Found, int Listed, int Reused, int Skipped, bool Resumed = false, bool TrustTimes = true);

    /// <summary>마지막으로 모은 결과 (시험·진단용).</summary>
    public PassStats? LastPass { get; private set; }

    /// <summary>모으던 중 끊긴 곳을 이만큼 지나기 전이면 이어서 한다. 더 오래되면 처음부터.</summary>
    public static TimeSpan ResumeWithin { get; set; } = TimeSpan.FromDays(7);

    /// <summary>모으는 동안 끊긴 곳을 이 간격마다 남긴다 (Foldy가 갑자기 꺼져도 여기서부터).</summary>
    public static TimeSpan CheckpointEvery { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 남은 공간·맨 위 폴더 시각이 그대로면 정기 색인을 건너뛰지만, 마지막으로 다 모은 뒤 이만큼 지나면 그래도 모은다
    /// (이름만 바뀐 것은 이 신호로 알 수 없어서).
    /// </summary>
    public static TimeSpan QuietAtMost { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// 다 읽었는데 하나도 없을 때 알리는 오류 (<see cref="CrawlStatus.Error"/>). 화면은 이것을 보고 "읽지 못했어요"라고 쉬운 말로 알린다.
    /// </summary>
    public const string NothingReadable = "nothing-readable";

    /// <summary>드라이브에서 쓰고 있는 공간이 이보다 많은데 하나도 못 읽으면 문제로 본다.</summary>
    internal const long NothingFoundBytes = 64L * 1024 * 1024;

    /// <summary>드라이브에서 쓰고 있는 공간 (시험에서 바꾼다). 알 수 없으면 0.</summary>
    internal Func<string, long> UsedSpaceOf { get; set; } = DefaultUsedSpace;

    private static long DefaultUsedSpace(string root)
    {
        try
        {
            var d = new DriveInfo(root);
            return d.TotalSize - d.TotalFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>드라이브 전체(예: "K:\\")인가 (시험에서 바꾼다: 시험은 임시 폴더를 드라이브처럼 쓴다).</summary>
    internal Func<string, bool> IsWholeDrive { get; set; } = root => string.Equals(Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase);

    // 목록이 비어 있는데 드라이브에는 파일이 있는 경우 한 번만 곧바로 다시 모아 본다 (계속 실패해도 드라이브를 두드리지 않게)
    private readonly HashSet<string> _retriedEmpty = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>다 모은 목록이 0개인데 드라이브에는 파일이 있다 (1.8.0까지 회사 클라우드 드라이브 같은 클라우드 드라이브에서 생겼다).</summary>
    private bool LooksWronglyEmpty(string root, CrawlStore store) =>
        store.Exists && (store.Count >= 0 ? store.Count : store.CountLines()) == 0 && IsWholeDrive(root) && UsedSpaceOf(root) > NothingFoundBytes;

    /// <summary>드라이브의 남은 공간 (시험에서 바꾼다). null이면 알 수 없음 → 건너뛰지 않는다.</summary>
    internal Func<string, long?> FreeSpaceOf { get; set; } = DefaultFreeSpace;

    /// <summary>
    /// 이 드라이브의 폴더 시각을 믿을 수 있나 (스스로 시험한 결과).
    /// 다시 모을 때 하위 폴더가 있는 폴더는 늘 목록을 새로 읽으므로, 그때 "시각은 그대로인데 안 목록이 바뀐" 폴더를 센다.
    /// 그런 폴더가 3개 넘고 10%를 넘으면 믿지 않고 전부 읽는다 (FAT의 2초 시각 단위 같은 우연은 넘어가게).
    /// </summary>
    internal static bool TimesTrusted(long checks, long misses) => misses < 3 || misses * 10 <= checks;

    /// <summary>
    /// 드라이브 하나를 끝까지 모은다 (시험에서도 쓴다). 취소되거나 드라이브가 끊기면 모은 데까지 남겨 두고, 다음에 거기서부터 이어서 한다
    /// (사용자 2026-11-23: K:처럼 크고 느린 드라이브는 Foldy를 끄거나 업데이트하면 처음부터 다시 해서 끝을 못 봤다). 그동안 찾기는 예전 목록 + 모은 데까지로.
    /// <para>부하는 적게, 그래도 빨리 (사용자 2026-11-21, 2026-11-23):</para>
    /// <list type="bullet">
    /// <item>폴더 목록은 한 번에 크게(64KB) 받아 오고 파일을 하나씩 열지 않는다. 요청은 한 번에 하나만.</item>
    /// <item>속도 조절(TCP처럼): 폴더를 읽은 시간을 평소와 비교해, 평소처럼 빠르면 쉬는 비율을 조금씩 줄이고(빨리), 느려지면 바로 두 배로(살살).</item>
    /// <item>다시 모을 때, 하위 폴더가 있는 폴더는 목록을 새로 읽는다.
    /// 하위 폴더가 없는 끝 폴더는 그 폴더의 수정 시각만 묻고(목록보다 훨씬 가볍다), 지난번과 같으면 지난 목록을 쓴다.
    /// 위 폴더 목록에 딸려 오는 시각은 쓰지 않는다: NTFS(네트워크 드라이브 뒤의 서버 포함)는 위 폴더에 적힌 하위 폴더 시각을 늦게 고쳐서 바뀐 것을 놓친다(CI 실측)
    /// (폴더 시각은 바로 안의 것이 생기거나 지워지거나 이름이 바뀔 때만 바뀌므로, 하위 폴더 통째 건너뛰기는 하지 않는다).
    /// 노트북 SD카드 실측: 폴더 21,130개 중 하위 폴더가 있는 9,947개만 읽으면 된다.</item>
    /// <item>폴더 시각을 믿을 수 없는 드라이브(<see cref="TimesTrusted"/>, <see cref="AlwaysReadAll"/>)는 늘 모든 폴더를 읽는다.</item>
    /// <item>최근에 바뀐 폴더부터 읽어, 바뀐 것이 찾기에 먼저 나온다.</item>
    /// </list>
    /// </summary>
    /// <param name="full">지난 목록을 쓰지 않고 모든 폴더를 다시 읽는다.</param>
    public long CrawlOnce(string drive, CancellationToken ct, bool full = false)
    {
        if (!IsNear(Root(drive)))
        {
            return Crawl(drive, full, near: false, ct);
        }

        long found = 0;
        GiveWay.Run(() => found = Crawl(drive, full, near: true, ct));
        return found;
    }

    private long Crawl(string drive, bool full, bool near, CancellationToken ct)
    {
        var root = Root(drive);
        var store = StoreFor(root);
        var temp = store.FilePath + ".part";
        var idxPath = store.FilePath + ".idx";
        var idxTemp = idxPath + ".part";
        var marksPath = store.FilePath + ".marks";
        var resumePath = store.FilePath + ".resume";
        var ix = LoadIndex(idxPath);
        var old = ix.Marks;
        var trusted = TimesTrusted(ix.Checks, ix.Misses);
        var readAll = full || old.Count == 0 || !trusted || AlwaysReadAll?.Invoke(root) == true;
        var ck = LoadCheckpoint(resumePath);
        if (ck is not null && !CanResume(ck, readAll, temp, marksPath))
        {
            ck = null;
        }

        if (ck is not null)
        {
            readAll = ck.ReadAll;
        }

        var pass = ck?.Pass ?? ix.Pass + 1;
        long found = ck?.Found ?? 0, checks = ck?.Checks ?? 0, misses = ck?.Misses ?? 0;
        int listed = ck?.Listed ?? 0, reused = ck?.Reused ?? 0, skipped = ck?.Skipped ?? 0;

        Report(new CrawlStatus(root, true, found, false, store.CompletedUtc));
        var pace = new CrawlPace(near, IsNetwork(root) ? MinNetworkPause : MinPause);
        var preferred = PreferFirst?.Invoke() ?? [];

        var running = Stopwatch.StartNew();
        var rested = TimeSpan.Zero;
        var foundAtStart = found;
        var foldersAtStart = listed + reused;

        void Breathe(TimeSpan batch, int folders, TimeSpan perAsk)
        {
            var pause = pace.After(batch, folders, perAsk);
            if (pause > TimeSpan.Zero)
            {
                var slept = Stopwatch.StartNew();
                try
                {
                    if (ct.WaitHandle.WaitOne(pause))
                    {
                        ct.ThrowIfCancellationRequested();
                    }
                }
                finally
                {
                    rested += slept.Elapsed;
                }

                pace.Slept(slept.Elapsed);
            }
        }

        CrawlSpeed SpeedNow()
        {
            var min = Math.Max(running.Elapsed.TotalMinutes, 1.0 / 60);
            return new CrawlSpeed((found - foundAtStart) / min, listed + reused - foldersAtStart, pace.AskMs, pace.Width, rested / running.Elapsed, pace.Tried);
        }

        try
        {
            FileStream? oldData = old.Count > 0 && store.Exists ? new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : null;
            try
            {
                var marks = new Dictionary<string, DirMark>(StringComparer.OrdinalIgnoreCase);
                var stack = new Stack<(string Dir, long Time)>();
                FileStream data;
                StreamWriter marksOut;
                if (ck is not null)
                {
                    // 이어서: 마지막으로 남긴 곳 뒤에 반쯤 쓴 것은 잘라 낸다
                    data = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.Read);
                    data.SetLength(ck.DataLength);
                    data.Seek(0, SeekOrigin.End);
                    using (var mf = new FileStream(marksPath, FileMode.Open, FileAccess.Write, FileShare.Read))
                    {
                        mf.SetLength(ck.MarksLength);
                    }

                    foreach (var line in File.ReadLines(marksPath, Encoding.UTF8))
                    {
                        if (ParseMark(line) is { } pm)
                        {
                            marks[pm.Dir] = pm.Mark;
                        }
                    }

                    marksOut = new StreamWriter(new FileStream(marksPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                    for (var i = ck.Stack.Count - 1; i >= 0; i--)
                    {
                        stack.Push(ck.Stack[i]);
                    }
                }
                else
                {
                    data = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read);
                    marksOut = new StreamWriter(new FileStream(marksPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                    stack.Push((root, 0));
                }

                // 읽고 있지만 아직 기록하지 않은 폴더들 (끊기면 다시 쌓아 다음에 이어서 한다)
                List<(string Dir, long Time)> pending = [];
                void SaveProgress()
                {
                    data.Flush(flushToDisk: true);
                    marksOut.Flush();
                    // 읽는 중인 폴더도 아직 안 남겼으니 맨 위에 넣어 둔다 (다음에 그것부터)
                    SaveCheckpoint(resumePath, new Checkpoint(pass, readAll, found, data.Length, marksOut.BaseStream.Length, checks, misses, listed, reused, skipped, DateTime.UtcNow, [.. pending, .. stack]));
                }

                var reading = new List<(FetchJob Job, Task<Fetched> Task)>();
                try
                {
                    var lastReport = Stopwatch.StartNew();
                    var lastSave = Stopwatch.StartNew();
                    var window = Stopwatch.StartNew();
                    int done = 0, asked = 0;
                    var askedTime = TimeSpan.Zero;
                    while (stack.Count > 0 || reading.Count > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (reading.Count == 0 && ShouldPause?.Invoke() == true)
                        {
                            // 복사 같은 작업 중엔 쉰다. 화면에 "작업이 끝나면 이어서 해요"라고 보이게 알린다
                            Report(new CrawlStatus(root, true, found, false, store.CompletedUtc) { Paused = true });
                            while (ShouldPause?.Invoke() == true)
                            {
                                if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
                                {
                                    ct.ThrowIfCancellationRequested();
                                }
                            }

                            Report(new CrawlStatus(root, true, found, false, store.CompletedUtc));
                            window.Restart();
                        }

                        // 자리가 비는 대로 다음 폴더를 읽기 시작한다: 묶음이 다 끝나기를 기다리면 느린 폴더 하나 때문에 나머지 자리가 논다
                        while (reading.Count < pace.Width && stack.Count > 0 && (reading.Count == 0 || ShouldPause?.Invoke() != true))
                        {
                            var p = stack.Pop();
                            var before = old.TryGetValue(p.Dir, out var o) ? o : null;
                            var job = new FetchJob(p.Dir, p.Time, before, before is not null && oldData is not null ? ReadBlock(oldData, before) : null);
                            pending.Add(p);

                            // 하나씩 읽을 때는 이 스레드에서 (가까운 드라이브는 이 스레드가 남에게 비켜 주며 읽는다).
                            // 같이 읽을 때는 폴더마다 제 스레드: 기다리는 읽기 16개가 공용 스레드를 차지하면 Windows가 스레드를 천천히 늘려 재는 값이 틀어진다
                            reading.Add((job, pace.Width == 1 && reading.Count == 0 ? Task.FromResult(Fetch(job, root, readAll)) : Task.Factory.StartNew(() => Fetch(job, root, readAll), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));
                        }

                        // 다 읽은 것이 있으면 먼저 기록한다 (끊으라는 말이 와도 읽은 것은 남긴다). 기다리는 동안 끊으면 읽는 중인 폴더는 다시 쌓아 다음에 읽는다
                        var at = reading.FindIndex(x => x.Task.IsCompleted);
                        if (at < 0)
                        {
                            at = Task.WaitAny(reading.Select(x => x.Task).ToArray(), ct);
                        }
                        var (doneJob, task) = reading[at];
                        reading.RemoveAt(at);
                        pending.RemoveAll(x => x.Dir == doneJob.Dir);
                        var got = task.Result;
                        var results = new[] { got };
                        var jobs = new[] { doneJob };

                        // 여기서부터 이 폴더를 다 기록할 때까지는 끊기지 않는다 (남기는 기록이 늘 맞게)
                        if (got.RootError is { } rootError)
                        {
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(rootError).Throw();
                        }

                        var newChildren = new List<(string Dir, long Time)>();
                        for (var i = 0; i < results.Length; i++)
                        {
                            var r = results[i];
                            var job = jobs[i];
                            var unchanged = 0;
                            if (r.Reused)
                            {
                                unchanged = job.Before!.Unchanged + 1;
                                reused++;
                                skipped++;
                                found += CountLines(r.Block);
                            }
                            else
                            {
                                asked++;
                                askedTime += r.Took;
                                listed++;
                                found += r.Count;
                                if (r.Ok && r.SameTime && job.OldBlock is not null)
                                {
                                    // 스스로 시험: 시각은 그대로인데 안 목록이 바뀌었나
                                    checks++;
                                    if (!r.SameNames)
                                    {
                                        misses++;
                                    }

                                    unchanged = r.SameNames ? job.Before!.Unchanged + 1 : 0;
                                }

                                newChildren.AddRange(r.Children);
                            }

                            var offset = data.Position;
                            data.Write(r.Block);
                            var mark = new DirMark(r.MTime, offset, r.Block.Length, unchanged);
                            marks[job.Dir] = mark;
                            marksOut.WriteLine(FormatMark(job.Dir, mark));
                        }

                        // 자주 쓰는 폴더로 가는 길은 맨 먼저, 그다음 최근에 바뀐 폴더: 나중에 쌓은 것이 먼저 나온다
                        foreach (var c in newChildren
                            .OrderBy(c => preferred.Count > 0 && LeadsTo(c.Dir, preferred))
                            .ThenByDescending(c => old.TryGetValue(c.Dir, out var m) ? m.Unchanged : -1))
                        {
                            stack.Push(c);
                        }

                        done++;
                        if (lastReport.Elapsed > TimeSpan.FromSeconds(1))
                        {
                            data.Flush(); // 모으는 중에도 찾을 수 있게
                            Report(new CrawlStatus(root, true, found, false, store.CompletedUtc) { Speed = SpeedNow() });
                            lastReport.Restart();
                        }

                        if (lastSave.Elapsed > CheckpointEvery)
                        {
                            SaveProgress();
                            lastSave.Restart();
                        }

                        // 같이 읽는 수만큼 끝날 때마다 빠르기를 고른다: 그동안 걸린 시간(쉰 시간 빼고) ÷ 끝난 폴더 수가 폴더 하나에 든 실제 시간
                        if (done >= pace.Width || (stack.Count == 0 && reading.Count == 0))
                        {
                            Breathe(window.Elapsed, done, asked == 0 ? TimeSpan.Zero : askedTime / asked);
                            window.Restart();
                            (done, asked, askedTime) = (0, 0, TimeSpan.Zero);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    for (var i = pending.Count - 1; i >= 0; i--)
                    {
                        stack.Push(pending[i]);
                    }

                    pending = [];
                    SaveProgress();
                    throw;
                }
                finally
                {
                    marksOut.Dispose();
                    data.Dispose();
                }

                oldData?.Dispose();
                oldData = null;
                if (found == 0 && IsWholeDrive(root) && UsedSpaceOf(root) > NothingFoundBytes)
                {
                    // 드라이브에 파일이 있는데 하나도 못 읽었다: "완료 · 0개" 대신 문제로 알리고, 예전 목록은 그대로 둔다
                    TryDelete(temp);
                    TryDelete(marksPath);
                    TryDelete(resumePath);
                    throw new IOException(NothingReadable);
                }

                var (tc, tm) = (ix.Checks + checks, ix.Misses + misses);
                if (tc > 2000)
                {
                    // 오래된 시험 결과는 반씩 잊는다 (드라이브가 바뀌면 다시 믿을 수도 있게)
                    (tc, tm) = (tc / 2, tm / 2);
                }

                SaveIndex(idxTemp, new IndexFile(pass, marks, tc, tm, Signal(root)));
                store.Replace(temp, found);
                File.Move(idxTemp, idxPath, overwrite: true);
                TryDelete(marksPath);
                TryDelete(resumePath);
                trusted = TimesTrusted(tc, tm);
            }
            finally
            {
                oldData?.Dispose();
            }

            LastPass = new PassStats(found, listed, reused, skipped, ck is not null, trusted);
            Report(new CrawlStatus(root, false, found, true, DateTime.UtcNow));
            return found;
        }
        catch (OperationCanceledException)
        {
            // 모은 데까지(.part)와 끊긴 곳(.resume)은 남긴다: 다음에 이어서 한다
            TryDelete(idxTemp);
            Report(new CrawlStatus(root, false, store.Count, store.Exists, store.CompletedUtc));
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 드라이브가 끊긴 경우 등: 마지막으로 남긴 곳부터 다음에 이어서 한다
            TryDelete(idxTemp);
            Report(new CrawlStatus(root, false, store.Count, store.Exists, store.CompletedUtc, ex.Message));
            return found;
        }
    }

    /// <summary>끊긴 곳에서 이어서 해도 되나: 너무 오래되지 않았고, 남긴 파일이 기록한 길이만큼 있고, 전부 다시 읽기를 바랄 때는 그때도 전부 읽던 중이어야.</summary>
    private static bool CanResume(Checkpoint ck, bool readAll, string temp, string marksPath)
    {
        try
        {
            return DateTime.UtcNow - ck.SavedUtc <= ResumeWithin
                && (!readAll || ck.ReadAll)
                && File.Exists(temp) && new FileInfo(temp).Length >= ck.DataLength
                && File.Exists(marksPath) && new FileInfo(marksPath).Length >= ck.MarksLength;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>이 드라이브를 모으다 끊긴 곳이 남아 있나 (Foldy를 켜면 주기를 기다리지 않고 이어서 한다).</summary>
    public bool HasUnfinished(string drive) => File.Exists(StoreFor(drive).FilePath + ".resume");

    private sealed record FetchJob(string Dir, long Time, DirMark? Before, byte[]? OldBlock);

    /// <summary>폴더 하나를 읽은 결과 (여러 폴더를 같이 읽고, 기록은 차례대로 한다).</summary>
    private sealed class Fetched
    {
        public long MTime { get; set; }

        public byte[] Block { get; set; } = [];

        public bool Reused { get; set; }

        public bool Ok { get; set; } = true;

        public bool SameTime { get; set; }

        public bool SameNames { get; set; }

        public long Count { get; set; }

        public TimeSpan Took { get; set; }

        public List<(string Dir, long Time)> Children { get; } = [];

        public Exception? RootError { get; set; }
    }

    /// <summary>폴더 하나: 시각을 보고 그대로인 끝 폴더면 지난 목록을, 아니면 목록을 새로 읽는다. 여러 스레드에서 같이 불린다(공유하는 것을 건드리지 않는다).</summary>
    private static Fetched Fetch(FetchJob job, string root, bool readAll)
    {
        var r = new Fetched();
        var sw = Stopwatch.StartNew();
        // 폴더 자신의 시각을 본다. 위 폴더 목록에 적힌 시각은 NTFS에서 늦게 고쳐져서, 그것을 기록하면 다음에 거의 모든 폴더를 다시 읽게 된다(CI 확인).
        // 목록을 안 읽어도 될 수 있는 끝 폴더만 시각을 따로 묻고, 나머지는 목록의 "." 줄에 딸려 오는 것을 쓴다
        // (Google Drive는 한 번 묻는 데 0.7ms. 2026-11-24 D:·G:·C: 11,681개 폴더에서 "." 시각이 따로 물은 시각과 모두 같음)
        if (!readAll && job.Dir != root && job.Before is not null && job.OldBlock is not null && !HasFolder(job.OldBlock))
        {
            r.MTime = DirTime(job.Dir);
            if (r.MTime != 0 && r.MTime == job.Before.MTime)
            {
                // 끝 폴더, 시각 그대로: 지난 목록을 쓴다
                r.SameTime = true;
                r.Block = job.OldBlock;
                r.Reused = true;
                return r;
            }
        }

        List<CrawlEntry> entries;
        try
        {
            entries = List(job.Dir, out var time);
            r.MTime = time;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (job.Dir == root)
            {
                r.RootError = ex;
                return r;
            }

            entries = [];
            r.Ok = false;
        }

        r.MTime = r.MTime != 0 ? r.MTime : DirTime(job.Dir); // 드라이브 맨 위에는 "."이 없다
        r.SameTime = job.Before is not null && r.MTime != 0 && r.MTime == job.Before.MTime;
        r.Took = sw.Elapsed;
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append(CrawlStore.Format(e)).Append('\n');
            r.Count++;
            if (e.IsFolder)
            {
                r.Children.Add((e.Path, e.ModifiedUtc.Ticks));
            }
        }

        r.Block = Encoding.UTF8.GetBytes(sb.ToString());
        if (r.Ok && r.SameTime && job.OldBlock is not null)
        {
            r.SameNames = SameNames(job.OldBlock, entries);
        }

        return r;
    }

    /// <summary>이 폴더가 먼저 모을 폴더이거나 그리로 가는 길인가.</summary>
    private static bool LeadsTo(string dir, IReadOnlyList<string> preferred)
    {
        foreach (var p in preferred)
        {
            if (p.Length >= dir.Length && p.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                && (p.Length == dir.Length || p[dir.Length] == '\\' || dir.EndsWith('\\')))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasFolder(byte[] block)
    {
        foreach (var line in Encoding.UTF8.GetString(block).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (CrawlStore.Parse(line.TrimEnd('\r')) is { IsFolder: true })
            {
                return true;
            }
        }

        return false;
    }

    private static int CountLines(byte[] block)
    {
        var n = 0;
        foreach (var b in block)
        {
            if (b == (byte)'\n')
            {
                n++;
            }
        }

        return n;
    }

    private static bool SameNames(byte[] oldBlock, List<CrawlEntry> now)
    {
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Encoding.UTF8.GetString(oldBlock).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (CrawlStore.Parse(line.TrimEnd('\r')) is { } e)
            {
                before.Add(e.Path);
            }
        }

        return before.Count == now.Count && now.All(e => before.Contains(e.Path));
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

    private static long? DefaultFreeSpace(string root)
    {
        try
        {
            return new DriveInfo(root).TotalFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>싼 변경 신호: 남은 공간과 맨 위 폴더 시각. 알 수 없으면 null.</summary>
    private long[]? Signal(string root)
    {
        var free = FreeSpaceOf(root);
        var t = DirTime(root);
        return free is { } f && t != 0 ? [f, t] : null;
    }

    /// <summary>
    /// 정기 색인 때가 됐어도 남은 공간과 맨 위 폴더 시각이 다 모은 때와 같으면 이번은 건너뛴다 (드라이브를 거의 읽지 않는다).
    /// 이 신호로는 이름만 바뀐 것을 알 수 없으므로 <see cref="QuietAtMost"/>가 지나면 그래도 모은다.
    /// </summary>
    private bool LooksQuiet(string root, DateTime done)
    {
        if (DateTime.UtcNow - done >= QuietAtMost)
        {
            return false;
        }

        var saved = LoadIndex(StoreFor(root).FilePath + ".idx").Signal;
        return saved is not null && Signal(root) is { } now && saved.AsSpan().SequenceEqual(now);
    }

    private sealed record IndexFile(int Pass, Dictionary<string, DirMark> Marks, long Checks, long Misses, long[]? Signal);

    private static IndexFile LoadIndex(string path)
    {
        var marks = new Dictionary<string, DirMark>(StringComparer.OrdinalIgnoreCase);
        var pass = 0;
        long checks = 0, misses = 0;
        long[]? signal = null;
        try
        {
            if (!File.Exists(path))
            {
                return new IndexFile(0, marks, 0, 0, null);
            }

            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (line.StartsWith('#'))
                {
                    var p = line.Split(' ');
                    switch (p[0])
                    {
                        case "#pass" when p.Length == 2:
                            _ = int.TryParse(p[1], out pass);
                            break;
                        case "#trust" when p.Length == 3:
                            _ = long.TryParse(p[1], out checks);
                            _ = long.TryParse(p[2], out misses);
                            break;
                        case "#sig" when p.Length == 3 && long.TryParse(p[1], out var a) && long.TryParse(p[2], out var b):
                            signal = [a, b];
                            break;
                    }

                    continue;
                }

                if (ParseMark(line) is { } pm)
                {
                    marks[pm.Dir] = pm.Mark;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            marks.Clear();
        }

        return new IndexFile(pass, marks, checks, misses, signal);
    }

    private static string FormatMark(string dir, DirMark m) =>
        string.Join('\t', dir, m.MTime.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            m.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Unchanged.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static (string Dir, DirMark Mark)? ParseMark(string line)
    {
        var p = line.Split('\t');
        return p.Length == 5 && long.TryParse(p[1], out var mt) && long.TryParse(p[2], out var off) && int.TryParse(p[3], out var len) && int.TryParse(p[4], out var un)
            ? (p[0], new DirMark(mt, off, len, un))
            : null;
    }

    private static void SaveIndex(string path, IndexFile ix)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        using var w = new StreamWriter(path, append: false, new UTF8Encoding(false));
        w.WriteLine("#pass " + ix.Pass.ToString(inv));
        w.WriteLine("#trust " + ix.Checks.ToString(inv) + " " + ix.Misses.ToString(inv));
        if (ix.Signal is [var a, var b])
        {
            w.WriteLine("#sig " + a.ToString(inv) + " " + b.ToString(inv));
        }

        foreach (var (dir, m) in ix.Marks)
        {
            w.WriteLine(FormatMark(dir, m));
        }
    }

    /// <summary>모으다 끊긴 곳. 목록(.part)과 폴더 기록(.marks)은 적어 둔 길이까지만 믿는다.</summary>
    private sealed record Checkpoint(int Pass, bool ReadAll, long Found, long DataLength, long MarksLength, long Checks, long Misses, int Listed, int Reused, int Skipped, DateTime SavedUtc, List<(string Dir, long Time)> Stack);

    private static void SaveCheckpoint(string path, Checkpoint c)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp, append: false, new UTF8Encoding(false)))
        {
            w.WriteLine("#resume 1");
            w.WriteLine(string.Join(' ', c.Pass.ToString(inv), c.ReadAll ? "1" : "0", c.Found.ToString(inv), c.DataLength.ToString(inv), c.MarksLength.ToString(inv),
                c.Checks.ToString(inv), c.Misses.ToString(inv), c.Listed.ToString(inv), c.Reused.ToString(inv), c.Skipped.ToString(inv), c.SavedUtc.Ticks.ToString(inv)));
            foreach (var (dir, t) in c.Stack)
            {
                w.WriteLine(dir + "\t" + t.ToString(inv));
            }
        }

        File.Move(tmp, path, overwrite: true);
    }

    private static Checkpoint? LoadCheckpoint(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var r = new StreamReader(path, Encoding.UTF8);
            if (r.ReadLine() != "#resume 1" || r.ReadLine()?.Split(' ') is not { Length: 11 } h)
            {
                return null;
            }

            var n = new long[11];
            for (var i = 0; i < 11; i++)
            {
                if (!long.TryParse(h[i], out n[i]))
                {
                    return null;
                }
            }

            var stack = new List<(string, long)>();
            string? line;
            while ((line = r.ReadLine()) is not null)
            {
                var tab = line.LastIndexOf('\t');
                if (tab > 0 && long.TryParse(line.AsSpan(tab + 1), out var t))
                {
                    stack.Add((line[..tab], t));
                }
            }

            return new Checkpoint((int)n[0], n[1] == 1, n[2], n[3], n[4], n[5], n[6], (int)n[7], (int)n[8], (int)n[9], new DateTime(n[10], DateTimeKind.Utc), stack);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // 목록은 한 번에 크게 받는다 (네트워크 왕복을 줄인다)
    private static readonly EnumerationOptions Flat = new() { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = true, BufferSize = 64 * 1024 };

    /// <summary>폴더 하나의 목록과 그 폴더 자신의 수정 시각 (목록의 "." 줄, 없으면 0).</summary>
    internal static List<CrawlEntry> List(string dir, out long time)
    {
        long self = 0;
        List<CrawlEntry> entries = [.. new FileSystemEnumerable<CrawlEntry?>(dir, (ref FileSystemEntry e) =>
        {
            if (e.FileName is "." or "..")
            {
                self = e.FileName.Length == 1 ? e.LastWriteTimeUtc.UtcDateTime.Ticks : self;
                return null;
            }

            var path = e.ToFullPath();
            // 링크(정션·심볼릭 링크) 안은 들어가지 않는다 (다른 곳을 두 번 세거나 끝없이 돌지 않게).
            // 클라우드 드라이브(OneDrive 같은 Windows 클라우드 파일)의 폴더도 "재분석 지점"이지만 링크가 아니라 진짜 폴더라 들어간다
            // (2026-11-23 회사 PC: 클라우드 드라이브 K:가 "색인 완료 · 0개"로 나왔다. 맨 위 폴더가 모두 이런 폴더라 다 건너뛴 것으로 본다).
            // 숨김 폴더도 들어간다: 회사 클라우드 드라이브(K:)는 맨 위 폴더가 숨김이다 (사용자 2026-11-23 "K 드라이브는 폴더가 히든 이라서 그럴까?").
            // 빼는 것은 Windows가 쓰는 폴더(휴지통·시스템 볼륨 정보)와 숨김+시스템 파일(desktop.ini·thumbs.db)뿐이다
            var skip = e.IsDirectory
                ? IsWindowsFolder(e.FileName) || ((e.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(path))
                : (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System);
            return skip
                ? null
                : new CrawlEntry(path, e.IsDirectory, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcDateTime);
        }, Flat)
            .OfType<CrawlEntry>()];
        time = self;
        return entries;
    }

    /// <summary>Windows가 드라이브마다 두는 폴더 (사용자 파일이 아니다)</summary>
    internal static bool IsWindowsFolder(ReadOnlySpan<char> name) =>
        name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
        || name.Equals("RECYCLER", StringComparison.OrdinalIgnoreCase) || name.Equals("$WinREAgent", StringComparison.OrdinalIgnoreCase);

    /// <summary>정션·심볼릭 링크인가. 클라우드 파일 같은 다른 재분석 지점은 링크가 아니다. 알 수 없으면 링크로 보고 들어가지 않는다.</summary>
    private static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

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
        // 1초 안에 끝나지 않으면(느린 드라이브를 읽는 중) 돌던 스레드가 아직 _cts·_kick을 쓰므로 버리지 않는다.
        // 먼저 버리면 그 스레드가 ObjectDisposedException으로 프로세스를 죽인다(CI Foldy.Core.Tests 2026-11-23). 남은 것은 GC가 치운다.
        List<Thread> workers;
        lock (_gate)
        {
            workers = [.. _workers];
        }

        var until = Environment.TickCount64 + 1000;
        var stopped = (_thread is null || _thread.Join(1000))
            && workers.All(w => w.Join(TimeSpan.FromMilliseconds(Math.Max(0, until - Environment.TickCount64))));
        if (stopped)
        {
            _cts.Dispose();
            _kick.Dispose();
        }
    }
}
