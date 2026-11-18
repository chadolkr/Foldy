using System.Diagnostics;

namespace Foldy.Core.Threading;

public enum WorkPriority { ActiveTab = 0, Icons = 1, Prefetch = 2, SizeCalc = 3 }

public enum DriveHealth { Ok, Slow, Unresponsive }

/// <summary>막힌 동기 I/O 끊기 훅 (Win32 CancelSynchronousIo는 Foldy.Platform이 제공한다).</summary>
public sealed record NativeIoCancel(Func<uint> CurrentThreadId, Action<uint> Cancel);

/// <summary>워치독이 작업을 포기했을 때 대기 중인 호출자에게 전달되는 예외.</summary>
public sealed class DriveUnresponsiveException(DriveKey drive)
    : IOException($"드라이브 {drive}가 응답하지 않습니다.")
{
    public DriveKey Drive { get; } = drive;
}

/// <summary>
/// 드라이브 하나의 전용 작업 큐 (설계서 5.2).
/// <list type="bullet">
/// <item>전용 스레드 1개에서 우선순위 순서로 실행한다. ThreadPool은 쓰지 않는다(블로킹 호출이 풀을 고갈시킴).</item>
/// <item>워치독이 <see cref="StallTimeout"/>보다 오래 묶인 호출을 감지하면 그 스레드를 버리고 새로 만든다.
/// 버려진 스레드는 블로킹 호출이 끝나면 스스로 종료한다. (CancelSynchronousIo 시도는 Foldy.Platform 연결 후 M1)</item>
/// <item>한 드라이브가 멈춰도 다른 드라이브의 큐와 UI는 영향을 받지 않는다.</item>
/// </list>
/// 스레드 소유권: 공개 멤버는 아무 스레드에서나 호출 가능.
/// </summary>
public sealed class DriveWorkQueue : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly PriorityQueue<WorkItem, (int Priority, long Seq)> _queue = new();
    private readonly Timer _watchdog;
    private readonly Action<Thread>? _onStall;
    private readonly NativeIoCancel? _nativeIo;
    private long _seq;
    private int _generation;
    private bool _disposed;
    private Worker _worker;

    public DriveWorkQueue(DriveKey drive, TimeSpan? stallTimeout = null, Action<Thread>? onStall = null, NativeIoCancel? nativeIo = null)
    {
        Drive = drive;
        StallTimeout = stallTimeout ?? TimeSpan.FromSeconds(5);
        _onStall = onStall;
        _nativeIo = nativeIo;
        _worker = StartWorker();
        var period = TimeSpan.FromMilliseconds(Math.Clamp(StallTimeout.TotalMilliseconds / 5, 20, 500));
        _watchdog = new Timer(_ => CheckStall(), null, period, period);
    }

    public DriveKey Drive { get; }

    public TimeSpan StallTimeout { get; }

    public DriveHealth Health { get; private set; } = DriveHealth.Ok;

    public event Action<DriveWorkQueue, DriveHealth>? HealthChanged;

    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>작업을 큐에 넣는다. 취소되면 즉시 Canceled로 끝나고, 실행 전이면 실행하지 않는다.</summary>
    public Task<T> Enqueue<T>(Func<CancellationToken, T> work, WorkPriority priority, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        var item = new WorkItem<T>(work, ct);
        if (ct.IsCancellationRequested)
        {
            item.Tcs.TrySetCanceled(ct);
            return item.Tcs.Task;
        }

        item.Registration = ct.Register(static s => ((WorkItem)s!).Cancel(), item);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LastUsedUtc = DateTime.UtcNow;
            _queue.Enqueue(item, ((int)priority, _seq++));
            Monitor.Pulse(_gate);
        }

        return item.Tcs.Task;
    }

    public Task Enqueue(Action<CancellationToken> work, WorkPriority priority, CancellationToken ct) =>
        Enqueue<bool>(c => { work(c); return true; }, priority, ct);

    public int PendingCount
    {
        get { lock (_gate) { return _queue.Count; } }
    }

    private Worker StartWorker()
    {
        var w = new Worker(++_generation);
        var t = new Thread(() => WorkerLoop(w))
        {
            IsBackground = true,
            Name = $"DriveQueue {Drive} #{w.Generation}",
        };
        w.Thread = t;
        t.Start();
        return w;
    }

    private void WorkerLoop(Worker self)
    {
        self.NativeId = _nativeIo?.CurrentThreadId() ?? 0;
        while (true)
        {
            WorkItem item;
            lock (_gate)
            {
                while (true)
                {
                    if (_disposed || self.Abandoned)
                    {
                        return;
                    }

                    if (_queue.TryDequeue(out item!, out _))
                    {
                        break;
                    }

                    Monitor.Wait(_gate);
                }

                if (item.IsCompleted)
                {
                    continue; // 대기 중 취소됨
                }

                self.Current = item;
                self.StartedTicks = Stopwatch.GetTimestamp();
            }

            item.Run();
            var elapsed = Stopwatch.GetElapsedTime(self.StartedTicks);

            lock (_gate)
            {
                self.Current = null;
                if (self.Abandoned)
                {
                    return; // 워치독이 이미 새 스레드를 만들었다.
                }
            }

            SetHealth(elapsed > TimeSpan.FromSeconds(1) ? DriveHealth.Slow : DriveHealth.Ok);
        }
    }

    private void CheckStall()
    {
        WorkItem? stalled = null;
        Thread? stalledThread = null;
        uint stalledNativeId = 0;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var w = _worker;
            if (w.Current is { } cur && Stopwatch.GetElapsedTime(w.StartedTicks) > StallTimeout)
            {
                w.Abandoned = true;
                stalled = cur;
                stalledThread = w.Thread;
                stalledNativeId = w.NativeId;
                _worker = StartWorker();
            }
        }

        if (stalled is not null)
        {
            // 먼저 막힌 동기 I/O를 끊어 본다 (CancelSynchronousIo). 끊기지 않아도 스레드는 이미 버렸다.
            if (_nativeIo is not null && stalledNativeId != 0)
            {
                try { _nativeIo.Cancel(stalledNativeId); } catch (Exception) { /* 진단용 시도 */ }
            }

            stalled.Fail(new DriveUnresponsiveException(Drive));
            SetHealth(DriveHealth.Unresponsive);
            if (stalledThread is not null)
            {
                try { _onStall?.Invoke(stalledThread); } catch (Exception) { /* 진단 훅 실패는 무시 */ }
            }
        }
    }

    private void SetHealth(DriveHealth h)
    {
        if (Health == h)
        {
            return;
        }

        Health = h;
        HealthChanged?.Invoke(this, h);
    }

    public ValueTask DisposeAsync()
    {
        List<WorkItem> pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            pending = [];
            while (_queue.TryDequeue(out var i, out _))
            {
                pending.Add(i);
            }

            Monitor.PulseAll(_gate);
        }

        _watchdog.Dispose();
        foreach (var i in pending)
        {
            i.Cancel();
        }

        return ValueTask.CompletedTask;
    }

    private sealed class Worker(int generation)
    {
        public int Generation { get; } = generation;
        public Thread? Thread { get; set; }
        public WorkItem? Current { get; set; }
        public long StartedTicks { get; set; }
        public bool Abandoned { get; set; }
        public uint NativeId { get; set; }
    }

    private abstract class WorkItem
    {
        public CancellationTokenRegistration Registration { get; set; }
        public abstract bool IsCompleted { get; }
        public abstract void Run();
        public abstract void Cancel();
        public abstract void Fail(Exception ex);
    }

    private sealed class WorkItem<T>(Func<CancellationToken, T> work, CancellationToken ct) : WorkItem
    {
        public TaskCompletionSource<T> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool IsCompleted => Tcs.Task.IsCompleted;

        public override void Run()
        {
            try
            {
                Tcs.TrySetResult(work(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Tcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                Tcs.TrySetException(ex);
            }
            finally
            {
                Registration.Dispose();
            }
        }

        public override void Cancel() => Tcs.TrySetCanceled(ct.IsCancellationRequested ? ct : new CancellationToken(true));

        public override void Fail(Exception ex) => Tcs.TrySetException(ex);
    }
}
