using System.Collections.Concurrent;

namespace Foldy.Core.Threading;

/// <summary>드라이브 키별로 큐를 지연 생성하고, 오래 쓰지 않은 큐는 정리한다 (설계서 5.2).</summary>
public sealed class DriveQueueRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<DriveKey, Lazy<DriveWorkQueue>> _queues = new();
    private readonly TimeSpan? _stallTimeout;

    private readonly NativeIoCancel? _nativeIo;

    public DriveQueueRegistry(TimeSpan? stallTimeout = null, NativeIoCancel? nativeIo = null)
    {
        _stallTimeout = stallTimeout;
        _nativeIo = nativeIo;
    }

    public DriveWorkQueue Get(DriveKey key) =>
        _queues.GetOrAdd(key, k => new Lazy<DriveWorkQueue>(() => new DriveWorkQueue(k, _stallTimeout, nativeIo: _nativeIo))).Value;

    public DriveWorkQueue ForPath(string path) => Get(DriveKey.FromPath(path));

    /// <summary>idle 이상 쓰지 않았고 대기 작업이 없는 큐를 정리한다.</summary>
    public async Task TrimAsync(TimeSpan idle)
    {
        var now = DateTime.UtcNow;
        foreach (var (key, lazy) in _queues)
        {
            if (lazy.IsValueCreated && lazy.Value.PendingCount == 0 && now - lazy.Value.LastUsedUtc > idle &&
                _queues.TryRemove(key, out var removed))
            {
                await removed.Value.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _queues.Values)
        {
            if (lazy.IsValueCreated)
            {
                await lazy.Value.DisposeAsync();
            }
        }

        _queues.Clear();
    }
}
