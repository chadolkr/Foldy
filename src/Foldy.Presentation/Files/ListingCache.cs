namespace Foldy.Presentation.Files;

/// <summary>
/// 폴더 목록 메모리 캐시 (Stale-While-Revalidate, DESIGN.md). 다시 열면 캐시를 먼저 보여주고 뒤에서 새로 고친다.
/// 너무 큰 목록(기본 2만 개 초과)은 메모리를 아끼려고 넣지 않는다. 앱에서는 UI 스레드에서 쓰지만, UI 스레드가 없는 곳(시험·여러 탭이 함께 불러오기)에서도 깨지지 않게 잠근다.
/// SQLite 영구 캐시(foldy.db listing_cache)는 느린 드라이브용으로 M2에서 추가.
/// </summary>
public sealed class ListingCache(int capacity = 64, int maxItems = 20_000)
{
    private readonly LinkedList<(string Path, FileItem[] Items)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Path, FileItem[] Items)>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public FileItem[]? TryGet(string path)
    {
        lock (_gate)
        {
            return TryGetLocked(path);
        }
    }

    private FileItem[]? TryGetLocked(string path)
    {
        if (!_map.TryGetValue(path, out var node))
        {
            return null;
        }

        _lru.Remove(node);
        _lru.AddFirst(node);
        return node.Value.Items;
    }

    public void Put(string path, FileItem[] items)
    {
        lock (_gate)
        {
            PutLocked(path, items);
        }
    }

    private void PutLocked(string path, FileItem[] items)
    {
        InvalidateLocked(path);
        if (items.Length > maxItems)
        {
            return;
        }

        _map[path] = _lru.AddFirst((path, items));
        while (_lru.Count > capacity)
        {
            var last = _lru.Last!;
            _map.Remove(last.Value.Path);
            _lru.RemoveLast();
        }
    }

    public void Invalidate(string path)
    {
        lock (_gate)
        {
            InvalidateLocked(path);
        }
    }

    private void InvalidateLocked(string path)
    {
        if (_map.Remove(path, out var node))
        {
            _lru.Remove(node);
        }
    }
}
