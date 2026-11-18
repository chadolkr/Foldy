using System.Globalization;
using System.IO;
using Foldy.Core.FileSystem;

namespace Foldy.App;

/// <summary>목록 행. 표시 문자열은 처음 보일 때 한 번만 만든다(가상화로 보이는 행만 계산됨).</summary>
public sealed class FileRow(FsEntry entry)
{
    private string? _size;
    private string? _modified;
    private string? _type;

    public FsEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public string SizeText => _size ??= Entry.Kind == FsKind.File ? FormatSize(Entry.Size) : "";

    public string ModifiedText => _modified ??=
        Entry.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public string TypeText => _type ??= Entry.Kind switch
    {
        FsKind.Folder => "파일 폴더",
        FsKind.Link => "바로 가기 / 링크",
        FsKind.Drive => "드라이브",
        _ => Path.GetExtension(Entry.Name) is { Length: > 1 } ext
            ? ext[1..].ToUpperInvariant() + " 파일"
            : "파일",
    };

    /// <summary>탐색기와 같은 KB 단위(올림) 표기.</summary>
    public static string FormatSize(long bytes)
    {
        var kb = (bytes + 1023) / 1024;
        return kb.ToString("N0", CultureInfo.CurrentCulture) + " KB";
    }

    /// <summary>벤치용 가짜 항목 생성 (디스크 I/O 없음).</summary>
    public static FileRow[] Synthetic(int count)
    {
        string[] exts = [".txt", ".md", ".cs", ".rs", ".png", ".jpg", ".zip", ".pdf", ".docx", ".exe", ""];
        var rnd = new Random(42);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new FileRow[count];
        for (var i = 0; i < count; i++)
        {
            var folder = i % 25 == 0;
            var name = folder
                ? $"폴더_{i:D6}"
                : $"file_{i:D6}{exts[rnd.Next(exts.Length)]}";
            var e = new FsEntry(
                name,
                FsPath.Local(@"C:\bench\" + name),
                folder ? FsKind.Folder : FsKind.File,
                folder ? 0 : rnd.NextInt64(0, 8L * 1024 * 1024 * 1024),
                null,
                baseTime.AddMinutes(rnd.Next(0, 500_000)),
                baseTime,
                folder ? FileAttributes.Directory : FileAttributes.Archive,
                null);
            rows[i] = new FileRow(e);
        }

        return rows;
    }
}
