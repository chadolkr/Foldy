using System.Globalization;
using Foldy.Core.FileSystem;

namespace Foldy.Presentation.Files;

/// <summary>목록 행 하나. 표시 문자열은 처음 보일 때 한 번만 만든다.</summary>
public sealed class FileItem(FsEntry entry, Func<FsEntry, string>? typeName = null)
{
    private string? _size;
    private string? _modified;
    private string? _type;

    public FsEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public string FullPath => Entry.Path.Value;

    public bool IsFolder => Entry.Kind is FsKind.Folder or FsKind.Drive || (Entry.Kind == FsKind.Link && (Entry.Attributes & FileAttributes.Directory) != 0);

    // 탐색기처럼 숨김(Hidden)만 숨긴다. System만 있는 폴더(예: 회사 보안 드라이브 K:의 My files)는 보인다.
    public bool IsHidden => (Entry.Attributes & FileAttributes.Hidden) != 0;

    public string Extension => IsFolder ? "" : System.IO.Path.GetExtension(Entry.Name);

    /// <summary>폴더의 하위 전체 크기 (색인 서비스가 있을 때만, 나중에 채워진다).</summary>
    public long? FolderSize
    {
        get => _folderSize ?? Entry.SubtreeSize;
        set
        {
            _folderSize = value;
            _size = null;
        }
    }

    private long? _folderSize;

    /// <summary>정렬·표시에 쓰는 크기: 파일은 크기, 폴더는 알면 하위 전체 크기.</summary>
    public long SortSize => IsFolder ? FolderSize ?? -1 : Entry.Size;

    public string SizeText => _size ??= Entry.Kind == FsKind.File ? FormatSize(Entry.Size) : FolderSize is { } fs ? FormatBytes(fs) : "";

    public string ModifiedText => _modified ??=
        Entry.Modified == default ? "" : Entry.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    /// <summary>좁은 열용: 오늘은 "08:22", 올해는 "11-19", 지난해 이전은 "24-04-06". 정확한 시각은 미리보기·속성에서.</summary>
    public string ShortModifiedText => _shortModified ??= Entry.Modified == default ? "" : FormatShortDate(Entry.Modified.ToLocalTime(), DateTime.Now);

    public static string FormatShortDate(DateTime local, DateTime now) =>
        local.Date == now.Date ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
        : local.Year == now.Year ? local.ToString("MM-dd", CultureInfo.InvariantCulture)
        : local.ToString("yy-MM-dd", CultureInfo.InvariantCulture);

    private string? _shortModified;

    public string TypeText => _type ??= typeName?.Invoke(Entry) ?? DefaultTypeName(Entry);

    public static string DefaultTypeName(FsEntry e) => e.Kind switch
    {
        FsKind.Folder => "파일 폴더",
        FsKind.Link => "링크",
        FsKind.Drive => "드라이브",
        _ => System.IO.Path.GetExtension(e.Name) is { Length: > 1 } ext ? ext[1..].ToUpperInvariant() + " 파일" : "파일",
    };

    /// <summary>탐색기와 같은 KB 단위(올림) 표기.</summary>
    public static string FormatSize(long bytes)
    {
        var kb = (bytes + 1023) / 1024;
        return kb.ToString("N0", CultureInfo.CurrentCulture) + " KB";
    }

    /// <summary>사람이 읽기 좋은 크기 (1.2 GB).</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0 ? $"{bytes:N0} B" : $"{v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture)} {units[u]}";
    }

    public override string ToString() => Name;
}
