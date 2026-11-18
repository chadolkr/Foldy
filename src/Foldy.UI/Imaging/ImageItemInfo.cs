using System.IO;
using System.Collections.Concurrent;
using System.Reflection;

namespace Foldy.UI.Imaging;

/// <summary>
/// 이미지 보기가 항목 하나에서 필요로 하는 값. Foldy.UI는 Presentation을 참조하지 않으므로
/// 호스트가 넘긴 항목(FileItem 등)을 이 값으로 바꿔 쓴다.
/// </summary>
public readonly record struct ImageItemInfo(string Path, string Name, DateTime Modified, long Length, FileAttributes Attributes)
{
    /// <summary>캐시 키: 경로 + 크기 + 수정 시각 (설계서 5.12). 파일이 바뀌면 키가 달라져 다시 만든다.</summary>
    public ThumbKey KeyFor(int pixelSize) => new(Path, Modified.Ticks, Length, pixelSize);

    public string Extension => System.IO.Path.GetExtension(Name);
}

/// <summary>
/// 기본 변환기. string(경로), FileInfo, 그리고 FullPath·Name 속성과 Entry(Modified·Size·Attributes)를 가진
/// 객체(Foldy.Presentation.Files.FileItem)를 이름으로 찾아 읽는다. 형식별 접근자는 한 번만 만든다.
/// </summary>
public static class ImageItemAdapter
{
    private static readonly ConcurrentDictionary<Type, Func<object, ImageItemInfo>> Cache = new();

    public static Func<object, ImageItemInfo> Default { get; } = Convert;

    public static ImageItemInfo Convert(object item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item switch
        {
            ImageItemInfo info => info,
            string path => FromPath(path),
            FileInfo fi => new ImageItemInfo(fi.FullName, fi.Name, SafeUtc(fi), SafeLength(fi), SafeAttributes(fi)),
            _ => Cache.GetOrAdd(item.GetType(), Build)(item),
        };
    }

    public static ImageItemInfo FromPath(string path)
    {
        var fi = new FileInfo(path);
        return new ImageItemInfo(path, fi.Name, SafeUtc(fi), SafeLength(fi), SafeAttributes(fi));
    }

    private static DateTime SafeUtc(FileInfo fi)
    {
        try
        {
            return fi.Exists ? fi.LastWriteTimeUtc : default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    private static long SafeLength(FileInfo fi)
    {
        try
        {
            return fi.Exists ? fi.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static FileAttributes SafeAttributes(FileInfo fi)
    {
        try
        {
            return fi.Exists ? fi.Attributes : default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    private static Func<object, ImageItemInfo> Build(Type type)
    {
        var path = Prop(type, "FullPath") ?? Prop(type, "Path");
        var name = Prop(type, "Name");
        if (path is null || path.PropertyType != typeof(string))
        {
            return o => FromPath(o.ToString() ?? "");
        }

        var entry = Prop(type, "Entry");
        var src = entry?.PropertyType ?? type;
        var modified = Prop(src, "Modified") ?? Prop(src, "LastWriteTimeUtc");
        var size = Prop(src, "Size") ?? Prop(src, "Length");
        var attrs = Prop(src, "Attributes");

        return o =>
        {
            var p = (string?)path.GetValue(o) ?? "";
            var n = name?.GetValue(o) as string ?? System.IO.Path.GetFileName(p);
            var e = entry is null ? o : entry.GetValue(o);
            if (e is null)
            {
                return new ImageItemInfo(p, n, default, 0, default);
            }

            return new ImageItemInfo(
                p,
                n,
                modified?.GetValue(e) is DateTime d ? d : default,
                size?.GetValue(e) is long l ? l : 0,
                attrs?.GetValue(e) is FileAttributes a ? a : default);
        };
    }

    private static PropertyInfo? Prop(Type t, string name) => t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
}
