namespace Foldy.Presentation.Files;

public enum SortKey { Name, Modified, Type, Size }

public sealed record SortSpec(SortKey Key = SortKey.Name, bool Descending = false)
{
    public SortSpec Toggle(SortKey key) => key == Key ? this with { Descending = !Descending } : new SortSpec(key, key is SortKey.Modified or SortKey.Size);
}

/// <summary>탐색기 규칙: 폴더가 항상 먼저, 그 안에서 키 → 이름(자연 정렬) 순. 백그라운드 스레드에서 안전.</summary>
public static class FileSorter
{
    public static FileItem[] Sort(IEnumerable<FileItem> items, SortSpec spec, CancellationToken ct = default)
    {
        var arr = items as FileItem[] ?? [.. items];
        var cmp = Create(spec);
        // Array.Sort는 취소를 지원하지 않으므로, 큰 목록은 정렬 전에만 확인한다.
        ct.ThrowIfCancellationRequested();
        Array.Sort(arr, cmp);
        return arr;
    }

    public static IComparer<FileItem> Create(SortSpec spec) => Comparer<FileItem>.Create((a, b) =>
    {
        if (a.IsFolder != b.IsFolder)
        {
            return a.IsFolder ? -1 : 1;
        }

        var c = spec.Key switch
        {
            SortKey.Modified => a.Entry.Modified.CompareTo(b.Entry.Modified),
            SortKey.Size => a.Entry.Size.CompareTo(b.Entry.Size),
            SortKey.Type => NaturalComparer.Instance.Compare(a.TypeText, b.TypeText),
            _ => 0,
        };
        if (c == 0)
        {
            c = NaturalComparer.Instance.Compare(a.Name, b.Name);
        }

        return spec.Descending ? -c : c;
    });
}
