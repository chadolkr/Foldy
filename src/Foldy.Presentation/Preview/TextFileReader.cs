using System.Text;
using System.Text.Unicode;

namespace Foldy.Presentation.Preview;

/// <summary>텍스트 읽기 결과. <see cref="Text"/>가 null이면 텍스트가 아니다(바이너리).</summary>
public sealed record TextReadResult(string? Text, string EncodingName, bool Truncated, long TotalBytes);

/// <summary>
/// 미리보기용 텍스트 읽기: 앞부분(기본 1MB)만 읽고 인코딩을 판별한다 (설계서 5.9와 같은 규칙:
/// BOM → UTF-8 검증 → UTF-16 휴리스틱 → CP949). 판별은 순수 함수(<see cref="Decode"/>)라 파일 없이 시험한다.
/// </summary>
public static class TextFileReader
{
    public const int DefaultLimit = 1024 * 1024;

    // CP949(EUC-KR 확장)는 .NET 기본 공급자에 없어 CodePages 공급자를 한 번 등록한다.
    private static readonly Lazy<Encoding> Cp949 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    });

    public static async Task<TextReadResult> ReadAsync(string path, int limit = DefaultLimit, CancellationToken ct = default)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var total = fs.Length;
        var buf = new byte[(int)Math.Min(total, limit)];
        var read = 0;
        while (read < buf.Length)
        {
            var n = await fs.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return Decode(buf.AsSpan(0, read), truncated: total > read, total);
    }

    /// <summary>바이트열의 인코딩을 판별해 문자열로 만든다. truncated면 끝의 잘린 문자는 버린다.</summary>
    public static TextReadResult Decode(ReadOnlySpan<byte> bytes, bool truncated, long totalBytes)
    {
        // 1) BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Utf8WithBom(bytes[3..], truncated, totalBytes, "UTF-8 (BOM)");
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
        {
            var body = bytes[4..];
            return new TextReadResult(Encoding.UTF32.GetString(body[..(body.Length / 4 * 4)]), "UTF-32 LE", truncated, totalBytes);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Utf16(bytes[2..], bigEndian: false, truncated, totalBytes);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Utf16(bytes[2..], bigEndian: true, truncated, totalBytes);
        }

        // 2) NUL 바이트: BOM 없는 UTF-16이거나 바이너리
        if (bytes.IndexOf((byte)0) >= 0)
        {
            return LooksUtf16(bytes) is { } be
                ? Utf16(bytes, be, truncated, totalBytes)
                : new TextReadResult(null, "binary", truncated, totalBytes);
        }

        // 3) UTF-8 (순수 ASCII 포함)
        var tail = Utf8TailTrim(bytes, truncated);
        if (Utf8.IsValid(bytes[..(bytes.Length - tail)]))
        {
            return new TextReadResult(Encoding.UTF8.GetString(bytes[..(bytes.Length - tail)]), "UTF-8", truncated, totalBytes);
        }

        // 4) 제어 문자가 많으면 바이너리, 아니면 CP949
        if (ControlRatio(bytes) > 0.05)
        {
            return new TextReadResult(null, "binary", truncated, totalBytes);
        }

        // CP949의 잘린 2바이트 문자는 마지막 한 글자만 깨지므로 그대로 둔다.
        return new TextReadResult(Cp949.Value.GetString(bytes), "CP949", truncated, totalBytes);
    }

    private static TextReadResult Utf8WithBom(ReadOnlySpan<byte> body, bool truncated, long total, string name)
    {
        var tail = Utf8TailTrim(body, truncated);
        // BOM이 있으면 UTF-8이라고 선언한 것이므로 잘못된 바이트는 U+FFFD로 둔다.
        return new TextReadResult(Encoding.UTF8.GetString(body[..(body.Length - tail)]), name, truncated, total);
    }

    private static TextReadResult Utf16(ReadOnlySpan<byte> body, bool bigEndian, bool truncated, long total)
    {
        var even = body[..(body.Length / 2 * 2)];
        var enc = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        var text = enc.GetString(even);
        // 끝이 서로게이트 앞쪽 절반이면 버린다 (잘린 문자).
        if (truncated && text.Length > 0 && char.IsHighSurrogate(text[^1]))
        {
            text = text[..^1];
        }

        return new TextReadResult(text, bigEndian ? "UTF-16 BE" : "UTF-16 LE", truncated, total);
    }

    /// <summary>BOM 없는 UTF-16 추정: NUL이 한쪽 홀짝 위치에만 몰려 있으면 그 쪽이 상위 바이트. 반환 true = big-endian.</summary>
    private static bool? LooksUtf16(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4)
        {
            return null;
        }

        int evenNul = 0, oddNul = 0, pairs = b.Length / 2;
        for (var i = 0; i + 1 < b.Length; i += 2)
        {
            if (b[i] == 0)
            {
                evenNul++;
            }

            if (b[i + 1] == 0)
            {
                oddNul++;
            }
        }

        // 라틴·ASCII 글자가 많으면 상위 바이트가 0. 한글만 있는 문서는 NUL이 없어 이 경로에 오지 않는다.
        if (oddNul >= pairs * 0.3 && evenNul <= pairs * 0.02)
        {
            return false;
        }

        if (evenNul >= pairs * 0.3 && oddNul <= pairs * 0.02)
        {
            return true;
        }

        return null;
    }

    /// <summary>잘린 읽기에서 끝에 걸친 불완전 UTF-8 문자의 바이트 수 (0~3).</summary>
    private static int Utf8TailTrim(ReadOnlySpan<byte> b, bool truncated)
    {
        if (!truncated || b.Length == 0)
        {
            return 0;
        }

        for (var back = 1; back <= Math.Min(3, b.Length); back++)
        {
            var c = b[^back];
            if ((c & 0xC0) == 0x80)
            {
                continue; // 이어지는 바이트
            }

            var need = c >= 0xF0 ? 4 : c >= 0xE0 ? 3 : c >= 0xC0 ? 2 : 1;
            return need > back ? back : 0;
        }

        return 0;
    }

    private static double ControlRatio(ReadOnlySpan<byte> b)
    {
        if (b.Length == 0)
        {
            return 0;
        }

        var n = 0;
        foreach (var c in b)
        {
            if (c < 0x20 && c is not (0x09 or 0x0A or 0x0D or 0x0C or 0x1B))
            {
                n++;
            }
        }

        return (double)n / b.Length;
    }
}
