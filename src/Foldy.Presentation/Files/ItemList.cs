using System.Collections;
using System.Collections.Specialized;

namespace Foldy.Presentation.Files;

/// <summary>
/// 목록 컨트롤에 넘기는 읽기 전용 목록 (설계서 6.2: ObservableCollection 대신 배열 + IList 래퍼).
/// 배치 추가는 변경 이벤트를 한 번만 보낸다. UI 스레드에서만 변경한다.
/// </summary>
public sealed class ItemList : IList, IReadOnlyList<FileItem>, INotifyCollectionChanged
{
    private readonly List<FileItem> _items;

    public ItemList() => _items = [];

    public ItemList(IEnumerable<FileItem> items) => _items = [.. items];

    public static readonly ItemList Empty = new();

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => _items.Count;

    public FileItem this[int index] => _items[index];

    public void AddBatch(IEnumerable<FileItem> batch)
    {
        _items.AddRange(batch);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public int IndexOfName(string name)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    public IEnumerator<FileItem> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    object? IList.this[int index]
    {
        get => _items[index];
        set => throw new NotSupportedException();
    }

    bool IList.IsReadOnly => true;

    bool IList.IsFixedSize => false;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    bool IList.Contains(object? value) => value is FileItem t && _items.Contains(t);

    int IList.IndexOf(object? value) => value is FileItem t ? _items.IndexOf(t) : -1;

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
}
