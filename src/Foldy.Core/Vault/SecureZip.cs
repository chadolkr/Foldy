using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ICSharpCode.SharpZipLib.Zip;

namespace Foldy.Core.Vault;

/// <summary>암호가 틀렸을 때.</summary>
public sealed class WrongPasswordException() : Exception(Foldy.Core.Localization.Loc.T("sz.wrongPw"));

/// <summary>
/// 금고(.foldyvault)와 잠긴 파일(.foldylock)의 바탕: AES-256 zip.
/// <para>
/// 형식 2 (지금 쓰는 것): 겉은 보통 zip이고 안에 세 가지가 있다.
/// <c>foldy-secure.json</c>(암호화 안 함: 형식·KDF·소금·반복 횟수), <c>index</c>(AES: 원래 이름·크기·시각 목록),
/// <c>d/000001</c>…(AES: 내용). zip 암호는 사용자 암호가 아니라 PBKDF2-HMAC-SHA256(600,000회)로 만든 키라서,
/// 표준 zip 암호(규격상 PBKDF2-SHA1 1,000회)보다 암호 맞히기가 수백 배 느리고, 파일 이름·폴더 구조도 암호 없이는 보이지 않는다.
/// 7-Zip으로는 열 수 없다 (형식은 이 파일과 docs에 공개되어 있다).
/// </para>
/// <para>형식 1 (0.3.18까지): 사용자 암호를 그대로 쓴 표준 AES zip. 계속 읽을 수 있다.</para>
/// 암호는 저장하지 않고 기록(로그)에도 남기지 않는다. 내용은 메모리에서만 풀고, 임시 파일을 만들지 않는다.
/// </summary>
public static class SecureZip
{
    public const string LockExtension = ".foldylock";
    public const string VaultExtension = ".foldyvault";
    public const string MemoSuffix = ".memo" + LockExtension;
    public const string NoteEntry = "note.md";
    public const string ImagesFolder = "images/";

    /// <summary>형식 2의 머리 항목 (암호화하지 않음).</summary>
    public const string HeaderEntry = "foldy-secure.json";
    private const string IndexEntry = "index";
    private const string DataPrefix = "d/";

    /// <summary>새로 만들 때의 PBKDF2-HMAC-SHA256 반복 횟수 (OWASP 2023 권장값).</summary>
    public const int Iterations = 600_000;

    // 남이 만든 파일이 반복 횟수를 터무니없이 키워 앱을 멈추게 하지 못하게
    private const int MinIterations = 100_000;
    private const int MaxIterations = 10_000_000;

    /// <summary>항목들을 AES-256으로 암호화해 쓴다 (형식 2).</summary>
    public static void Write(string path, IEnumerable<(string Name, Func<Stream> Open)> entries, string password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        // 같은 파일을 다시 저장할 때(잠긴 메모 자동 저장)는 그 파일의 소금을 그대로 써서 키를 다시 만들지 않는다 (0.3초 멈춤 방지).
        // 각 항목은 WinZip AES가 항목마다 따로 소금을 붙이므로 같은 키를 다시 써도 안전하다.
        var (salt, iterations) = ExistingHeader(path) ?? (RandomNumberGenerator.GetBytes(16), Iterations);
        var key = CachedKey(password, salt, iterations);
        var tmp = path + ".writing";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipOutputStream(fs) { IsStreamOwner = false })
            {
                zip.SetLevel(6);
                var stamp = new DateTime(2000, 1, 1); // 겉 항목의 시각은 감춘다 (진짜 시각은 index 안에)
                var header = JsonSerializer.SerializeToUtf8Bytes(new Header(2, "pbkdf2-sha256", iterations, Convert.ToBase64String(salt)));
                zip.Password = null;
                zip.PutNextEntry(new ZipEntry(HeaderEntry) { DateTime = stamp });
                zip.Write(header, 0, header.Length);
                zip.CloseEntry();

                zip.Password = key;
                var index = new List<IndexItem>();
                var buf = new byte[81920];
                var n = 0;
                foreach (var (name, open) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var id = $"{DataPrefix}{++n:D6}";
                    zip.PutNextEntry(new ZipEntry(id) { AESKeySize = 256, DateTime = stamp });
                    long size = 0;
                    using (var src = open())
                    {
                        int r;
                        while ((r = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            zip.Write(buf, 0, r);
                            size += r;
                        }
                    }

                    zip.CloseEntry();
                    index.Add(new IndexItem(ZipEntry.CleanName(name), id, size, DateTime.Now));
                }

                var idx = JsonSerializer.SerializeToUtf8Bytes(index);
                zip.PutNextEntry(new ZipEntry(IndexEntry) { AESKeySize = 256, DateTime = stamp });
                zip.Write(idx, 0, idx.Length);
                zip.CloseEntry();
                zip.Finish();
            }

            // 다 쓴 뒤에만 바꿔 끼운다 (쓰다 실패해도 이전 파일은 그대로)
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            File.Delete(tmp); // 쓰다 만 파일을 남기지 않는다
            throw;
        }
    }

    /// <summary>항목 이름 목록 (암호 확인 겸).</summary>
    public static IReadOnlyList<(string Name, long Size)> List(string path, string password)
    {
        using var a = Archive.Open(path, password);
        var list = a.Items.Select(i => (i.Name, i.Size)).ToList();

        // 형식 1은 목록이 암호 없이도 보이므로 첫 항목을 풀어 보며 암호를 확인한다 (형식 2는 index를 풀면서 확인됨)
        if (!a.IsV2 && list.Count > 0)
        {
            _ = a.Read(list[0].Name);
        }

        return list;
    }

    /// <summary>항목 하나를 메모리로 푼다.</summary>
    public static byte[] ReadEntry(string path, string password, string name)
    {
        using var a = Archive.Open(path, password);
        return a.Read(name);
    }

    /// <summary>모든 항목을 메모리로 푼다 (잠긴 메모처럼 작은 것만).</summary>
    public static Dictionary<string, byte[]> ReadAll(string path, string password)
    {
        using var a = Archive.Open(path, password);
        var d = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in a.Items)
        {
            d[i.Name] = a.Read(i.Name);
        }

        return d;
    }

    /// <summary>모든 항목을 끝까지 풀어 보며 확인한다 (암호와 내용이 맞는지). 맞으면 true.</summary>
    public static bool Verify(string path, string password, CancellationToken ct = default)
    {
        try
        {
            using var a = Archive.Open(path, password);
            if (!a.Zip.TestArchive(testData: true))
            {
                return false;
            }

            foreach (var i in a.Items)
            {
                ct.ThrowIfCancellationRequested();
                using var s = a.OpenStream(i.Name);
                s.CopyTo(Stream.Null);
            }

            return true;
        }
        catch (Exception ex) when (ex is ZipException or WrongPasswordException or IOException or InvalidDataException or JsonException)
        {
            return false;
        }
    }

    /// <summary>폴더 전체를 꺼낸다. 이미 있는 파일은 덮어쓰지 않고 " (2)"를 붙인다. 꺼낸 개수.</summary>
    public static int ExtractAll(string path, string password, string destFolder, CancellationToken ct = default)
    {
        using var a = Archive.Open(path, password);
        var n = 0;
        var root = Path.GetFullPath(destFolder);
        foreach (var i in a.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (SafeTarget(root, i.Name) is not { } target)
            {
                continue; // zip 밖으로 나가는 이름은 무시
            }

            var dst = Unique(target);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            using (var s = a.OpenStream(i.Name))
            using (var o = File.Create(dst))
            {
                s.CopyTo(o);
            }

            File.SetLastWriteTime(dst, i.Time);
            n++;
        }

        return n;
    }

    private static (byte[] Salt, int Iterations)? ExistingHeader(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var zf = new ZipFile(path);
            if (zf.GetEntry(HeaderEntry) is not { } h)
            {
                return null;
            }

            using var hs = zf.GetInputStream(h);
            var header = JsonSerializer.Deserialize<Header>(hs);
            if (header is not { Format: 2, Kdf: "pbkdf2-sha256" } || header.IterationCount < Iterations || header.IterationCount > MaxIterations)
            {
                return null;
            }

            var salt = Convert.FromBase64String(header.Salt);
            return salt.Length >= 16 ? (salt, header.IterationCount) : null;
        }
        catch (Exception ex) when (ex is IOException or ZipException or JsonException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>파일이 형식 2(강한 키)인지. 암호 없이 알 수 있다.</summary>
    public static bool IsStrongFormat(string path)
    {
        using var zf = new ZipFile(path);
        return zf.GetEntry(HeaderEntry) is not null;
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

    /// <summary>PBKDF2-HMAC-SHA256 → zip 암호로 쓸 64자 16진수 키.</summary>
    internal static string DeriveKey(string password, byte[] salt, int iterations) =>
        Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32));

    // 같은 금고를 여러 번 여는 동안(미리 보기, 하나씩 꺼내기) 키를 매번 다시 만들면 한 번에 0.3초쯤 걸린다.
    // 최근 몇 개만 메모리에 둔다 (암호 자체가 이미 메모리에 있으므로 더 위험해지지 않는다).
    private static readonly Dictionary<string, string> KeyCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> KeyOrder = new();

    private static string CachedKey(string password, byte[] salt, int iterations)
    {
        var id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{iterations}:{Convert.ToBase64String(salt)}:{password}")));
        lock (KeyCache)
        {
            if (KeyCache.TryGetValue(id, out var k))
            {
                return k;
            }
        }

        var key = DeriveKey(password, salt, iterations);
        lock (KeyCache)
        {
            if (KeyCache.TryAdd(id, key))
            {
                KeyOrder.Enqueue(id);
                while (KeyOrder.Count > 8)
                {
                    KeyCache.Remove(KeyOrder.Dequeue());
                }
            }
        }

        return key;
    }

    /// <summary>앱을 잠글 때·끝낼 때 기억해 둔 키를 지운다.</summary>
    public static void ForgetKeys()
    {
        lock (KeyCache)
        {
            KeyCache.Clear();
            KeyOrder.Clear();
        }
    }

    private static bool IsAuthFailure(ZipException ex) =>
        ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("AES", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("auth", StringComparison.OrdinalIgnoreCase);

    public static byte[] Sha256(Stream s) => SHA256.HashData(s);

    private sealed record Header(
        [property: JsonPropertyName("format")] int Format,
        [property: JsonPropertyName("kdf")] string Kdf,
        [property: JsonPropertyName("iterations")] int IterationCount,
        [property: JsonPropertyName("salt")] string Salt);

    private sealed record IndexItem(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("entry")] string Entry,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("time")] DateTime Time);

    /// <summary>열린 금고·잠긴 파일. 형식 1·2를 같은 모양으로 보여 준다.</summary>
    private sealed class Archive : IDisposable
    {
        private readonly Dictionary<string, IndexItem> _byName;

        private Archive(ZipFile zip, bool v2, List<IndexItem> items)
        {
            Zip = zip;
            IsV2 = v2;
            Items = items;
            _byName = new Dictionary<string, IndexItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in items)
            {
                _byName[i.Name] = i;
            }
        }

        public ZipFile Zip { get; }

        public bool IsV2 { get; }

        public IReadOnlyList<IndexItem> Items { get; }

        public static Archive Open(string path, string password)
        {
            var zf = new ZipFile(path);
            try
            {
                if (zf.GetEntry(HeaderEntry) is not { } h)
                {
                    zf.Password = password;
                    var items = new List<IndexItem>();
                    foreach (ZipEntry e in zf)
                    {
                        if (e.IsFile)
                        {
                            items.Add(new IndexItem(e.Name, e.Name, e.Size, e.DateTime));
                        }
                    }

                    return new Archive(zf, false, items);
                }

                Header header;
                using (var hs = zf.GetInputStream(h))
                {
                    header = JsonSerializer.Deserialize<Header>(hs) ?? throw new InvalidDataException(Foldy.Core.Localization.Loc.T("sz.noHeader"));
                }

                if (header.Format != 2 || header.Kdf != "pbkdf2-sha256" || header.IterationCount is < MinIterations or > MaxIterations)
                {
                    throw new InvalidDataException(Foldy.Core.Localization.Loc.T("sz.unsupported"));
                }

                var salt = Convert.FromBase64String(header.Salt);
                if (salt.Length < 16)
                {
                    throw new InvalidDataException(Foldy.Core.Localization.Loc.T("sz.badHeader"));
                }

                zf.Password = CachedKey(password, salt, header.IterationCount);
                var idx = zf.GetEntry(IndexEntry) ?? throw new InvalidDataException(Foldy.Core.Localization.Loc.T("sz.noList"));
                List<IndexItem> list;
                try
                {
                    using var s = zf.GetInputStream(idx);
                    list = JsonSerializer.Deserialize<List<IndexItem>>(s) ?? [];
                }
                catch (ZipException ex) when (IsAuthFailure(ex))
                {
                    throw new WrongPasswordException();
                }
                catch (JsonException)
                {
                    // 키가 틀렸는데 우연히 2바이트 확인 값이 맞으면 풀린 내용이 엉망이다
                    throw new WrongPasswordException();
                }

                return new Archive(zf, true, list);
            }
            catch
            {
                zf.Close();
                throw;
            }
        }

        public Stream OpenStream(string name)
        {
            var item = _byName.GetValueOrDefault(name) ?? throw new FileNotFoundException(name);
            var e = Zip.GetEntry(item.Entry) ?? throw new FileNotFoundException(name);
            try
            {
                return Zip.GetInputStream(e);
            }
            catch (ZipException ex) when (IsAuthFailure(ex))
            {
                throw new WrongPasswordException();
            }
        }

        public byte[] Read(string name)
        {
            try
            {
                using var s = OpenStream(name);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            catch (ZipException ex) when (IsAuthFailure(ex))
            {
                throw new WrongPasswordException();
            }
        }

        public void Dispose() => Zip.Close();
    }
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
    public string AddImage(byte[] bytes, string ext, string folder = SecureZip.ImagesFolder)
    {
        for (var i = Images.Count + 1; ; i++)
        {
            var name = Foldy.Core.Localization.Loc.F("sz.imageName", folder, i, ext);
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

/// <summary>금고를 만든 결과. <see cref="Skipped"/>가 있으면 원본을 지우면 안 된다.</summary>
public sealed record VaultResult(string VaultPath, int Files, long Bytes, IReadOnlyList<(string Path, string Reason)> Skipped)
{
    /// <summary>원본 폴더의 모든 파일이 금고에 들어갔는지 (원본을 휴지통으로 보내도 되는지).</summary>
    public bool Complete => Skipped.Count == 0;
}

/// <summary>금고: 폴더를 통째로 .foldyvault 하나에 넣는다.</summary>
public static class VaultMaker
{
    /// <summary>
    /// 폴더 안 파일을 모두 넣는다. 숨김·시스템 파일과 OneDrive "필요할 때 받기" 파일(재분석 지점이 붙은 파일)도 넣는다.
    /// 폴더 링크(정션·심볼릭 링크)와 열 수 없는 폴더는 따라가지 않고 <see cref="VaultResult.Skipped"/>에 적는다.
    /// </summary>
    public static VaultResult Create(string folder, string password, CancellationToken ct = default)
    {
        var root = folder.TrimEnd('\\', '/');
        var vault = SecureZip.Unique(root + SecureZip.VaultExtension);
        var (files, skipped) = Scan(root, ct);
        long bytes = 0;
        foreach (var f in files)
        {
            bytes += new FileInfo(f).Length;
        }

        SecureZip.Write(vault, files.Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), (Func<Stream>)(() => File.OpenRead(f)))), password, ct);
        return new VaultResult(vault, files.Count, bytes, skipped);
    }

    /// <summary>넣을 파일과 건너뛸 것. 파일 수·크기를 미리 보여 줄 때도 쓴다.</summary>
    public static (List<string> Files, List<(string Path, string Reason)> Skipped) Scan(string root, CancellationToken ct = default)
    {
        var files = new List<string>();
        var skipped = new List<(string, string)>();
        var stack = new Stack<string>();
        stack.Push(root);
        var opts = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
                {
                    if (e is DirectoryInfo d)
                    {
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            skipped.Add((d.FullName, Foldy.Core.Localization.Loc.T("sz.link")));
                        }
                        else
                        {
                            stack.Push(d.FullName);
                        }
                    }
                    else
                    {
                        files.Add(e.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skipped.Add((dir, Foldy.Core.Localization.Loc.T("sz.cantOpen")));
            }
        }

        return (files, skipped);
    }
}
