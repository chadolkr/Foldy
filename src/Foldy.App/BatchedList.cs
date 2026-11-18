using System.Collections;
using System.Collections.Specialized;

namespace Foldy.App;

/// <summary>
/// 배치 추가 시 변경 이벤트를 한 번만 발생시키는 읽기 전용 목록 (설계서 6.2: ObservableCollection 대신
/// 불변 배열 + IList 래퍼). WPF ItemsControl은 범위 Add 알림을 지원하지 않으므로 배치마다 Reset을 보낸다.
/// UI 스레드에서만 변경한다.
/// </summary>
public sealed class BatchedList<T> : IList, IReadOnlyList<T>, INotifyCollectionChanged
{
    private readonly List<T> _items = [];

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => _items.Count;

    public T this[int index] => _items[index];

    public void AddBatch(IEnumerable<T> batch)
    {
        _items.AddRange(batch);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void Clear()
    {
        _items.Clear();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

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

    bool IList.Contains(object? value) => value is T t && _items.Contains(t);

    int IList.IndexOf(object? value) => value is T t ? _items.IndexOf(t) : -1;

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
}
