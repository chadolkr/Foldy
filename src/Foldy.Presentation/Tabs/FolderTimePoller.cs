namespace Foldy.Presentation.Tabs;

/// <summary>
/// 변경 알림을 받을 수 없는 드라이브(네트워크·회사 클라우드 드라이브)에서 보고 있는 폴더 하나만 짧게 확인한다.
/// 1초마다 그 폴더의 수정 시각 하나만 묻는다(목록을 읽지 않는다). 파일이 생기거나 지워지거나 이름이 바뀌면 폴더 시각이 바뀌므로 그때만 새로 고친다.
/// 묻는 데 오래 걸리는 드라이브는 5초마다로 늦춘다. 앞의 확인이 안 끝났으면 겹쳐 묻지 않는다.
/// (사용자 2026-11-24: "K 드라이브 폴더에 외부에서 파일을 저장했는데 폴더에는 표시가 안된다. 1초로 하면 부하가 가지 않을까?")
/// </summary>
public sealed class FolderTimePoller : IDisposable
{
    public static TimeSpan Every { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>시각 하나 묻는 데 이보다 오래 걸리면 느린 드라이브로 보고 <see cref="SlowEvery"/>로 늦춘다.</summary>
    public static TimeSpan SlowAsk { get; set; } = TimeSpan.FromMilliseconds(300);

    public static TimeSpan SlowEvery { get; set; } = TimeSpan.FromSeconds(5);

    private readonly string _folder;
    private readonly Func<bool> _active;
    private readonly Action _changed;
    private readonly Func<string, long?> _timeOf;
    private readonly Timer _timer;
    private long? _last;
    private int _busy;
    private volatile bool _disposed;

    /// <param name="active">거짓이면(보이지 않는 탭) 묻지 않는다.</param>
    /// <param name="timeOf">폴더 수정 시각 (시험에서 바꾼다). 모르면 null.</param>
    public FolderTimePoller(string folder, Func<bool> active, Action changed, Func<string, long?>? timeOf = null)
    {
        _folder = folder;
        _active = active;
        _changed = changed;
        _timeOf = timeOf ?? DefaultTimeOf;
        _timer = new Timer(_ => Tick(), null, Every, Timeout.InfiniteTimeSpan);
    }

    private void Tick()
    {
        if (_disposed || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        var next = Every;
        try
        {
            if (_active())
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var now = _timeOf(_folder);
                if (sw.Elapsed > SlowAsk)
                {
                    next = SlowEvery;
                }

                if (now is not null && now != _last)
                {
                    var first = _last is null;
                    _last = now;
                    if (!first)
                    {
                        _changed();
                    }
                }
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            if (!_disposed)
            {
                try
                {
                    _timer.Change(next, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private static long? DefaultTimeOf(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder).Ticks : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
