using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;

namespace Foldy.Core.Vault;

/// <summary>암호가 틀렸을 때.</summary>
public sealed class WrongPasswordException() : Exception("암호가 맞지 않아요.");

/// <summary>
/// 금고(.foldyvault)와 잠긴 파일(.foldylock)의 바탕: 표준 AES-256 zip.
/// 확장자만 바꾸면(.zip) 7-Zip·반디집에서도 열린다. 암호는 저장하지 않고 기록(로그)에도 남기지 않는다.
/// 내용은 메모리에서만 풀고, 임시 파일을 만들지 않는다.
/// </summary>
public static class SecureZip
{
    public const string LockExtension = ".foldylock";
    public const string VaultExtension = ".foldyvault";
    public const string MemoSuffix = ".memo" + LockExtension;
    public const string NoteEntry = "note.md";
    public const string ImagesFolder = "images/";

    /// <summary>항목들을 AES-256으로 암호화해 쓴다.</summary>
    public static void Write(string path, IEnumerable<(string Name, Func<Stream> Open)> entries, string password, CancellationToken ct = default)
    {
        var tmp = path + ".writing";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipOutputStream(fs) { Password = password, IsStreamOwner = false })
        {
            zip.SetLevel(6);
            var buf = new byte[81920];
            foreach (var (name, open) in entries)
            {
                ct.ThrowIfCancellationRequested();
                var e = new ZipEntry(ZipEntry.CleanName(name)) { AESKeySize = 256, DateTime = DateTime.Now, IsUnicodeText = true };
                zip.PutNextEntry(e);
                using (var src = open())
                {
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        zip.Write(buf, 0, n);
                    }
                }

                zip.CloseEntry();
            }

            zip.Finish();
        }

        // 다 쓴 뒤에만 바꿔 끼운다 (쓰다 실패해도 이전 파일은 그대로)
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>항목 이름 목록 (암호 확인 겸).</summary>
    public static IReadOnlyList<(string Name, long Size)> List(string path, string password)
    {
        using var zf = Open(path, password);
        var list = new List<(string, long)>();
        foreach (ZipEntry e in zf)
        {
            if (e.IsFile)
            {
                list.Add((e.Name, e.Size));
            }
        }

        // 첫 항목을 풀어 보며 암호를 확인한다 (목록은 암호 없이도 보이므로)
        if (list.Count > 0)
        {
            _ = Read(zf, list[0].Item1);
        }

        return list;
    }

    /// <summary>항목 하나를 메모리로 푼다.</summary>
    public static byte[] ReadEntry(string path, string password, string name)
    {
        using var zf = Open(path, password);
        return Read(zf, name);
    }

    /// <summary>모든 항목을 메모리로 푼다 (잠긴 메모처럼 작은 것만).</summary>
    public static Dictionary<string, byte[]> ReadAll(string path, string password)
    {
        using var zf = Open(path, password);
        var d = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipEntry e in zf)
        {
            if (e.IsFile)
            {
                d[e.Name] = Read(zf, e.Name);
            }
        }

        return d;
    }

    /// <summary>모든 항목을 끝까지 풀어 보며 확인한다 (암호와 내용이 맞는지). 맞으면 true.</summary>
    public static bool Verify(string path, string password, CancellationToken ct = default)
    {
        try
        {
            using var zf = Open(path, password);
            if (!zf.TestArchive(testData: true))
            {
                return false;
            }

            foreach (ZipEntry e in zf)
            {
                ct.ThrowIfCancellationRequested();
                if (e.IsFile)
                {
                    using var s = zf.GetInputStream(e);
                    s.CopyTo(Stream.Null);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is ZipException or WrongPasswordException or IOException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>폴더 전체를 꺼낸다. 이미 있는 파일은 덮어쓰지 않고 " (2)"를 붙인다. 꺼낸 개수.</summary>
    public static int ExtractAll(string path, string password, string destFolder, CancellationToken ct = default)
    {
        using var zf = Open(path, password);
        var n = 0;
        var root = Path.GetFullPath(destFolder);
        foreach (ZipEntry e in zf)
        {
            ct.ThrowIfCancellationRequested();
            if (!e.IsFile)
            {
                continue;
            }

            if (SafeTarget(root, e.Name) is not { } target)
            {
                continue; // zip 밖으로 나가는 이름은 무시
            }

            var dst = Unique(target);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            using (var s = zf.GetInputStream(e))
            using (var o = File.Create(dst))
            {
                s.CopyTo(o);
            }

            File.SetLastWriteTime(dst, e.DateTime);
            n++;
        }

        return n;
    }

    /// <summary>항목 이름을 꺼낼 위치로. 폴더 밖으로 나가는 이름("../", 절대 경로)이면 null (zip slip 막기).</summary>
    public static string? SafeTarget(string destFolder, string entryName)
    {
        // ':'는 드라이브 문자("C:x")나 NTFS 대체 데이터 스트림, '\\'로 시작하면 다른 서버 → 모두 거부
        if (string.IsNullOrWhiteSpace(entryName) || entryName.Contains('\0', StringComparison.Ordinal) || entryName.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var rel = entryName.Replace('\\', '/');
        if (rel.StartsWith('/'))
        {
            return null;
        }

        var root = Path.GetFullPath(destFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string dst;
        try
        {
            dst = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return dst.StartsWith(root, StringComparison.OrdinalIgnoreCase) && dst.Length > root.Length ? dst : null;
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p))
            {
                return p;
            }
        }
    }

    private static ZipFile Open(string path, string password)
    {
        var zf = new ZipFile(path) { Password = password };
        return zf;
    }

    private static byte[] Read(ZipFile zf, string name)
    {
        var e = zf.GetEntry(name) ?? throw new FileNotFoundException(name);
        try
        {
            using var s = zf.GetInputStream(e);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (ZipException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("AES", StringComparison.OrdinalIgnoreCase))
        {
            throw new WrongPasswordException();
        }
    }

    public static byte[] Sha256(Stream s) => SHA256.HashData(s);
}

/// <summary>잠긴 메모: note.md + images/*. 메모리에만 풀어 둔다.</summary>
public sealed class LockedNote
{
    public string Markdown { get; set; } = "";

    public Dictionary<string, byte[]> Images { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static LockedNote Load(string path, string password)
    {
        var all = SecureZip.ReadAll(path, password);
        var n = new LockedNote();
        if (all.TryGetValue(SecureZip.NoteEntry, out var md))
        {
            n.Markdown = System.Text.Encoding.UTF8.GetString(md);
        }

        foreach (var (k, v) in all.Where(kv => kv.Key.StartsWith(SecureZip.ImagesFolder, StringComparison.OrdinalIgnoreCase)))
        {
            n.Images[k] = v;
        }

        return n;
    }

    public void Save(string path, string password)
    {
        // 본문에서 쓰지 않는 이미지는 함께 지운다
        var used = Images.Where(kv => Markdown.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)).ToList();
        var md = System.Text.Encoding.UTF8.GetBytes(Markdown);
        var entries = new List<(string, Func<Stream>)> { (SecureZip.NoteEntry, () => new MemoryStream(md)) };
        entries.AddRange(used.Select(kv => (kv.Key, (Func<Stream>)(() => new MemoryStream(kv.Value)))));
        SecureZip.Write(path, entries, password);
    }

    /// <summary>이미지를 넣고 본문에 쓸 이름을 돌려준다 (images/그림-1.png).</summary>
    public string AddImage(byte[] bytes, string ext)
    {
        for (var i = Images.Count + 1; ; i++)
        {
            var name = $"{SecureZip.ImagesFolder}그림-{i}{ext}";
            if (!Images.ContainsKey(name))
            {
                Images[name] = bytes;
                return name;
            }
        }
    }
}

/// <summary>파일 잠그기·풀기. 잠글 때 원본 바이트를 그대로 넣고, 풀 때 똑같이 돌려준다 (내용은 해석하지 않는다).</summary>
public static class FileLocker
{
    /// <summary>이름.확장자 → 이름.확장자.foldylock. 다시 열어 원본과 같은지(SHA-256) 확인한다. 실패하면 잠긴 파일을 지우고 false.</summary>
    public static (bool Ok, string LockPath) Lock(string file, string password)
    {
        var lockPath = SecureZip.Unique(file + SecureZip.LockExtension);
        var name = Path.GetFileName(file);
        byte[] hash;
        using (var s = File.OpenRead(file))
        {
            hash = SecureZip.Sha256(s);
        }

        SecureZip.Write(lockPath, [(name, () => File.OpenRead(file))], password);
        var back = SecureZip.ReadEntry(lockPath, password, name);
        if (!System.Security.Cryptography.SHA256.HashData(back).AsSpan().SequenceEqual(hash))
        {
            File.Delete(lockPath);
            return (false, lockPath);
        }

        return (true, lockPath);
    }

    /// <summary>잠긴 파일 안의 원래 이름.</summary>
    public static string InnerName(string lockPath, string password) => SecureZip.List(lockPath, password)[0].Name;

    /// <summary>원래 파일로 되돌린다 (같은 폴더, 이름이 겹치면 " (2)"). 만든 경로.</summary>
    public static string Unlock(string lockPath, string password)
    {
        var name = InnerName(lockPath, password);
        var dst = SecureZip.Unique(Path.Combine(Path.GetDirectoryName(lockPath)!, Path.GetFileName(name)));
        File.WriteAllBytes(dst, SecureZip.ReadEntry(lockPath, password, name));
        return dst;
    }

    /// <summary>미리 볼 수 있는 내용인지 (확장자에 맞는 머리 모양인지). 아니면 "원래 프로그램으로 열어 주세요".</summary>
    public static bool LooksPreviewable(string name, ReadOnlySpan<byte> head)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        static bool Starts(ReadOnlySpan<byte> h, ReadOnlySpan<byte> sig) => h.Length >= sig.Length && h[..sig.Length].SequenceEqual(sig);
        return ext switch
        {
            ".pdf" => Starts(head, "%PDF"u8),
            ".docx" or ".xlsx" or ".pptx" or ".zip" => Starts(head, "PK"u8),
            ".png" => Starts(head, [0x89, 0x50, 0x4E, 0x47]),
            ".jpg" or ".jpeg" => Starts(head, [0xFF, 0xD8, 0xFF]),
            ".gif" => Starts(head, "GIF8"u8),
            ".txt" or ".md" or ".csv" or ".log" or ".json" => IsText(head),
            _ => false,
        };
    }

    private static bool IsText(ReadOnlySpan<byte> head)
    {
        foreach (var b in head)
        {
            if (b == 0)
            {
                return false;
            }
        }

        try
        {
            _ = new System.Text.UTF8Encoding(false, true).GetString(head.Length > 4096 ? head[..4000] : head);
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
    }
}

/// <summary>금고: 폴더를 통째로 .foldyvault 하나에 넣는다.</summary>
public static class VaultMaker
{
    public static string Create(string folder, string password, CancellationToken ct = default)
    {
        var root = folder.TrimEnd('\\', '/');
        var vault = SecureZip.Unique(root + SecureZip.VaultExtension);
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
        SecureZip.Write(vault, files.Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), (Func<Stream>)(() => File.OpenRead(f)))), password, ct);
        return vault;
    }
}
