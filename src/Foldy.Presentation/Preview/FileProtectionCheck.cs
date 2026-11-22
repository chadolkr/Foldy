using System.Text;

namespace Foldy.Presentation.Preview;

/// <summary>파일이 보호돼서 미리 볼 수 없는 경우.</summary>
public enum FileProtection
{
    None,

    /// <summary>DRM(문서 보안): 회사 보안 프로그램이 내용을 암호화했거나, Office 권한 관리로 잠긴 파일.</summary>
    Drm,

    /// <summary>Office 문서에 암호가 걸려 있다.</summary>
    Password,

    /// <summary>암호화된 Office 문서인데 암호인지 DRM인지 가리지 못했다.</summary>
    Protected,
}

/// <summary>
/// 미리보기 전에 파일 앞부분을 보고 DRM·암호로 보호된 파일인지 알아본다 (사용자 2026-11-22: "DRM 걸려 있는 경우 에러로 표시하지 말고
/// DRM 걸려서 못 보여주는 걸로 알려 줄 수 있니?"). 특정 보안 제품의 표시를 찾지 않는다(회사 정보 규칙, docs/no-company-info.md):
/// <list type="bullet">
/// <item>확장자가 말하는 형식(Office는 ZIP·복합 문서, PDF는 %PDF, 그림은 그 형식의 머리)과 내용이 다르고, 알려진 다른 형식도 아니며,
/// 내용이 암호문처럼 고르게 흩어져 있으면(엔트로피가 높으면) DRM으로 본다. 문서 보안 프로그램은 파일을 자기 머리 + 암호문으로 바꾼다.</item>
/// <item>Office 문서가 암호화 상자(복합 문서 + 암호화 데이터 공간)로 바뀌어 있으면, 권한 관리(DRM) 표시가 있으면 DRM, 강한 암호화 표시면 암호.</item>
/// </list>
/// 확실하지 않으면 None (평소처럼 미리보기를 해 본다). 파일을 못 열면 None.
/// </summary>
public static class FileProtectionCheck
{
    private const int HeadBytes = 64 * 1024;
    private const int MarkerScanBytes = 1024 * 1024;

    /// <summary>암호문으로 볼 엔트로피 (바이트당 비트, 최대 8). 글은 5~6, 압축·암호문은 7.8 넘게.</summary>
    private const double EncryptedEntropy = 7.3;

    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private enum Expect
    {
        Zip,
        Ole,
        Pdf,
        Rtf,
        Image,
        Text,
    }

    private static readonly Dictionary<string, Expect> ByExtension = Build();

    /// <summary>이 확장자를 살펴보나 (모르는 확장자는 살펴보지 않는다).</summary>
    public static bool Checks(string extension) => ByExtension.ContainsKey(extension);

    /// <summary>파일을 읽어 판정한다. 앞 64KB(복합 문서면 끝 1MB까지)만 읽는다.</summary>
    public static FileProtection Detect(string path, string extension)
    {
        if (!ByExtension.TryGetValue(extension, out var expect))
        {
            return FileProtection.None;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var length = fs.Length;
            if (length == 0)
            {
                return FileProtection.None;
            }

            var head = new byte[(int)Math.Min(HeadBytes, length)];
            fs.ReadExactly(head);
            return Classify(expect, head, () => Tail(fs, length, head.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return FileProtection.None;
        }
    }

    /// <summary>앞부분(과 필요하면 끝부분)으로 판정 (시험용으로 열어 둠).</summary>
    public static FileProtection Classify(string extension, byte[] head, byte[]? tail = null) =>
        ByExtension.TryGetValue(extension, out var expect) ? Classify(expect, head, () => tail ?? []) : FileProtection.None;

    private static FileProtection Classify(Expect expect, byte[] head, Func<byte[]> tail)
    {
        var ole = head.AsSpan().StartsWith(Ole);
        switch (expect)
        {
            case Expect.Zip when IsZip(head):
                return FileProtection.None;

            // 암호화된 Office 문서(docx 등)는 ZIP이 아니라 복합 문서 안의 암호화 패키지가 된다
            case Expect.Zip when ole:
                return OleMarkers(head, tail) switch
                {
                    Marker.Drm => FileProtection.Drm,
                    Marker.Password => FileProtection.Password,
                    _ => FileProtection.Protected,
                };

            // 예전 Office(doc·xls·ppt)·한글(hwp): 권한 관리 표시가 있을 때만 DRM (암호 걸린 예전 문서는 처리기에 맡긴다)
            case Expect.Ole when ole:
                return OleMarkers(head, tail) == Marker.Drm ? FileProtection.Drm : FileProtection.None;

            case Expect.Pdf when IsPdf(head):
            case Expect.Rtf when StartsWithAscii(head, "{\\rtf"):
            case Expect.Image when IsImage(head):
                return FileProtection.None;
        }

        // 확장자와 다른 알려진 형식(이름만 바뀐 파일)은 보호가 아니다
        if (IsKnownFormat(head))
        {
            return FileProtection.None;
        }

        return LooksEncrypted(head) ? FileProtection.Drm : FileProtection.None;
    }

    private enum Marker
    {
        None,
        Drm,
        Password,
    }

    /// <summary>복합 문서 안의 데이터 공간 이름: 권한 관리면 "DRMEncrypted…"·"DRMContent"·"…DRMTransform", 암호면 "StrongEncryption…"·"EncryptionInfo".</summary>
    private static Marker OleMarkers(byte[] head, Func<byte[]> tail)
    {
        var found = Find(head);
        if (found != Marker.None)
        {
            return found;
        }

        return Find(tail());

        static Marker Find(byte[] b)
        {
            if (b.Length == 0)
            {
                return Marker.None;
            }

            if (Contains(b, "DRMEncrypted") || Contains(b, "DRMContent") || Contains(b, "DRMTransform"))
            {
                return Marker.Drm;
            }

            return Contains(b, "StrongEncryption") || Contains(b, "EncryptionInfo") ? Marker.Password : Marker.None;
        }
    }

    /// <summary>UTF-16LE(복합 문서의 이름) 또는 ASCII로 들어 있나.</summary>
    private static bool Contains(byte[] data, string text) =>
        data.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0 || data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    private static byte[] Tail(FileStream fs, long length, int already)
    {
        if (length <= already)
        {
            return [];
        }

        var n = (int)Math.Min(MarkerScanBytes, length - already);
        var buf = new byte[n];
        fs.Seek(length - n, SeekOrigin.Begin);
        fs.ReadExactly(buf);
        return buf;
    }

    private static bool IsZip(byte[] h) =>
        h.Length >= 4 && h[0] == 'P' && h[1] == 'K' && h[2] is 3 or 5 or 7 && h[3] is 4 or 6 or 8;

    /// <summary>%PDF는 앞 1KB 안 어디에 있어도 된다 (PDF 사양).</summary>
    private static bool IsPdf(byte[] h) => h.AsSpan(0, Math.Min(h.Length, 1024)).IndexOf("%PDF-"u8) >= 0;

    private static bool StartsWithAscii(byte[] h, string s) => h.AsSpan().StartsWith(Encoding.ASCII.GetBytes(s));

    private static bool IsImage(byte[] h) =>
        h.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) // JPEG
        || h.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']) // PNG
        || StartsWithAscii(h, "GIF8")
        || StartsWithAscii(h, "BM")
        || h.AsSpan().StartsWith((ReadOnlySpan<byte>)[(byte)'I', (byte)'I', 0x2A, 0x00]) // TIFF
        || h.AsSpan().StartsWith((ReadOnlySpan<byte>)[(byte)'M', (byte)'M', 0x00, 0x2A])
        || (StartsWithAscii(h, "RIFF") && h.Length >= 12 && h.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        || h.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00]); // ICO

    /// <summary>확장자와 상관없이 알려진 형식 (이름만 바뀐 파일·압축 파일 등).</summary>
    private static bool IsKnownFormat(byte[] h) =>
        IsZip(h) || h.AsSpan().StartsWith(Ole) || IsPdf(h) || IsImage(h) || StartsWithAscii(h, "{\\rtf")
        || h.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]) // 7z
        || StartsWithAscii(h, "Rar!")
        || h.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]) // gzip
        || h.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00]) // xz
        || StartsWithAscii(h, "BZh")
        || StartsWithAscii(h, "MZ")
        || StartsWithAscii(h, "%!PS")
        || StartsWithAscii(h, "8BPS")
        || StartsWithAscii(h, "OggS")
        || StartsWithAscii(h, "fLaC")
        || StartsWithAscii(h, "ID3")
        || (h.Length >= 8 && h.AsSpan(4, 4).SequenceEqual("ftyp"u8)) // MP4·HEIC
        || h.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]) // MKV·WebM
        || StartsWithAscii(h, "HWP Document File"); // 한글 3.0

    /// <summary>
    /// 암호문처럼 보이나: 머리(앞 1KB까지)를 뺀 나머지 바이트가 고르게 흩어져 있다.
    /// 글·표·대부분의 바이너리 형식은 이만큼 고르지 않다. 표본이 너무 작으면(512바이트 미만) 판단하지 않는다.
    /// </summary>
    public static bool LooksEncrypted(ReadOnlySpan<byte> data)
    {
        var skip = Math.Min(1024, data.Length / 4);
        var sample = data[skip..];
        if (sample.Length < 512)
        {
            return false;
        }

        Span<int> counts = stackalloc int[256];
        foreach (var b in sample)
        {
            counts[b]++;
        }

        double entropy = 0;
        foreach (var c in counts)
        {
            if (c > 0)
            {
                var p = (double)c / sample.Length;
                entropy -= p * Math.Log2(p);
            }
        }

        return entropy >= EncryptedEntropy;
    }

    private static Dictionary<string, Expect> Build()
    {
        var d = new Dictionary<string, Expect>(StringComparer.OrdinalIgnoreCase);
        void Add(Expect e, string exts)
        {
            foreach (var x in exts.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                d["." + x] = e;
            }
        }

        Add(Expect.Zip, "docx docm dotx dotm xlsx xlsm xltx xltm xlsb pptx pptm potx potm ppsx ppsm vsdx hwpx odt ods odp");
        Add(Expect.Ole, "doc dot xls xlt ppt pot pps hwp msg vsd");
        Add(Expect.Pdf, "pdf");
        Add(Expect.Rtf, "rtf");
        Add(Expect.Image, "jpg jpeg jpe jfif png gif bmp dib tif tiff webp ico");
        Add(Expect.Text, "txt csv tsv log md markdown xml json html htm ini");
        return d;
    }
}
