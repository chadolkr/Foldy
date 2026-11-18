using System.Windows.Media.Imaging;

namespace Foldy.UI.Imaging;

/// <summary>썸네일 캐시 키: 경로 + 수정 시각 + 파일 크기 + 픽셀 크기 (경로는 대소문자 무시).</summary>
public readonly record struct ThumbKey(string Path, long ModifiedTicks, long Length, int PixelSize)
{
    public bool Equals(ThumbKey other) =>
        ModifiedTicks == other.ModifiedTicks && Length == other.Length && PixelSize == other.PixelSize &&
        string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Path ?? ""), ModifiedTicks, Length, PixelSize);
}

/// <summary>
/// 디코딩된 썸네일의 메모리 캐시 (설계서 5.12: 최대 200MB). 바이트 예산을 넘으면 가장 오래 안 쓴 것부터 버린다(LRU).
/// 값은 Freeze된 BitmapSource라 어느 스레드에서 꺼내 써도 된다. 스레드 안전.
/// </summary>
public sealed class ThumbnailMemoryCache
{
    public const long DefaultBudgetBytes = 200L * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<ThumbKey, LinkedListNode<Slot>> _map = [];
    private readonly LinkedList<Slot> _lru = []; // 앞쪽이 최근

    public ThumbnailMemoryCache(long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        BudgetBytes = budgetBytes;
    }

    public long BudgetBytes { get; }

    public long TotalBytes { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>PBGRA 기준 메모리 크기.</summary>
    public static long SizeOf(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return (long)image.PixelWidth * image.PixelHeight * Math.Max(1, (image.Format.BitsPerPixel + 7) / 8);
    }

    public bool TryGet(ThumbKey key, out BitmapSource image)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null!;
        return false;
    }

    /// <summary>같은 경로·수정 시각의 다른 크기 썸네일 중 하나 (크기를 바꾸는 동안 임시로 늘려 그린다).</summary>
    public BitmapSource? FindAnySize(ThumbKey key, IEnumerable<int> sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        foreach (var s in sizes)
        {
            if (s != key.PixelSize && TryGet(key with { PixelSize = s }, out var img))
            {
                return img;
            }
        }

        return null;
    }

    public void Add(ThumbKey key, BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.IsFrozen)
        {
            image.Freeze();
        }

        var size = SizeOf(image);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var old))
            {
                RemoveNode(old);
            }

            if (size > BudgetBytes)
            {
                return; // 예산보다 큰 것은 넣지 않는다
            }

            _map[key] = _lru.AddFirst(new Slot(key, image, size));
            TotalBytes += size;
            while (TotalBytes > BudgetBytes && _lru.Last is { } last)
            {
                RemoveNode(last);
            }
        }
    }

    /// <summary>경로의 모든 크기 썸네일을 버린다 (파일이 바뀌었거나 지워졌을 때).</summary>
    public void Invalidate(string path)
    {
        lock (_gate)
        {
            foreach (var k in _map.Keys.Where(k => string.Equals(k.Path, path, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                RemoveNode(_map[k]);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
            TotalBytes = 0;
        }
    }

    public bool Contains(ThumbKey key)
    {
        lock (_gate)
        {
            return _map.ContainsKey(key);
        }
    }

    private void RemoveNode(LinkedListNode<Slot> node)
    {
        _lru.Remove(node);
        _map.Remove(node.Value.Key);
        TotalBytes -= node.Value.Bytes;
    }

    private sealed record Slot(ThumbKey Key, BitmapSource Image, long Bytes);
}
