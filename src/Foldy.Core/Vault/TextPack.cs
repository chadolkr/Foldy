using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Foldy.Core.Vault;

/// <summary>
/// 그림이 든 메모: TextBundle 규격의 .textpack (zip 하나에 text.md + info.json + assets/).
/// 그림이 없는 메모는 그냥 .md. 그림은 원본 화질 그대로 assets/ 안에.
/// 다른 사람이 보낸 파일일 수 있으므로 열 때 이름·개수·크기를 제한한다 (디스크에는 풀지 않고 메모리로만 읽는다).
/// </summary>
public static class TextPack
{
    public const string Extension = ".textpack";
    public const string Assets = "assets/";
    private const string TextEntry = "text.md";
    private const string InfoEntry = "info.json";
    private const int MaxEntries = 2000;
    private const long MaxEntryBytes = 64L << 20;
    private const long MaxTotalBytes = 256L << 20;

    public static bool Is(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public static LockedNote Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException("항목이 너무 많은 파일이에요.");
        }

        var note = new LockedNote();
        long total = 0;
        foreach (var e in zip.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                continue; // 폴더
            }

            if (!SafeName(name))
            {
                continue; // "../", 절대 경로, ':' 같은 이름은 무시
            }

            if (e.Length > MaxEntryBytes || (total += e.Length) > MaxTotalBytes)
            {
                throw new InvalidDataException("너무 큰 파일이에요.");
            }

            if (name.Equals(TextEntry, StringComparison.OrdinalIgnoreCase))
            {
                note.Markdown = Encoding.UTF8.GetString(Read(e));
            }
            else if (name.StartsWith(Assets, StringComparison.OrdinalIgnoreCase))
            {
                note.Images[name] = Read(e);
            }
        }

        return note;
    }

    /// <summary>쓰다 실패해도 이전 파일이 남게, 새 파일에 다 쓴 뒤 바꿔 끼운다. 본문에서 쓰지 않는 그림은 뺀다.</summary>
    public static void Save(string path, LockedNote note)
    {
        var tmp = path + ".writing";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            Write(zip, InfoEntry, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["version"] = 2,
                ["type"] = "net.daringfireball.markdown",
                ["transient"] = false,
                ["creatorIdentifier"] = "io.github.chadolkr.foldy",
            }));
            Write(zip, TextEntry, Encoding.UTF8.GetBytes(note.Markdown));
            foreach (var (name, bytes) in note.Images.Where(kv => note.Markdown.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)))
            {
                Write(zip, name, bytes, CompressionLevel.NoCompression); // 그림은 이미 압축돼 있다
            }
        }

        File.Move(tmp, path, overwrite: true);
    }

    private static bool SafeName(string name) =>
        name.Length > 0 && !name.StartsWith('/') && !name.Contains(':', StringComparison.Ordinal) && !name.Contains('\0', StringComparison.Ordinal)
        && !name.Split('/').Any(part => part is ".." or ".");

    private static byte[] Read(ZipArchiveEntry e)
    {
        // 머리의 크기(e.Length)는 파일을 만든 쪽이 적은 값이라 믿지 않는다: 실제로 풀리는 양을 세며 읽고, 한도를 넘으면 바로 멈춘다
        // (작다고 적어 두고 수 GB로 풀리는 압축 폭탄으로 메모리를 다 쓰게 하는 것 막기, 보안 리뷰 2026-11-22)
        using var s = e.Open();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > MaxEntryBytes)
            {
                throw new InvalidDataException("너무 큰 파일이에요.");
            }

            ms.Write(buf, 0, n);
        }

        return ms.ToArray();
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var s = zip.CreateEntry(name, level).Open();
        s.Write(bytes);
    }
}
