using System.IO;
using System.Text;

namespace Foldy.UI.Markdown;

/// <summary>Markdown 파일 읽기와 인코딩 판별 (설계서 5.9: UTF-8 우선, BOM과 CP949 자동 판별).</summary>
public static class MarkdownText
{
    /// <summary>이보다 큰 파일은 앞부분부터 보여 주고 나머지를 이어 붙인다.</summary>
    public const long LargeFileThreshold = 1024 * 1024;

    private static readonly Lazy<Encoding> Cp949 = new(() =>
    {
        // .NET에는 코드 페이지 인코딩이 기본 등록돼 있지 않다. 여러 번 등록해도 무해하다.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949);
    });

    /// <summary>바이트의 인코딩을 판별한다. BOM → 엄격한 UTF-8 → CP949 순.</summary>
    public static Encoding DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8;
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode;
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode;
        }

        return IsValidUtf8(bytes) ? Encoding.UTF8 : Cp949.Value;
    }

    /// <summary>바이트를 문자열로 바꾼다. BOM은 결과에 넣지 않는다.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, out Encoding encoding)
    {
        encoding = DetectEncoding(bytes);
        var preamble = encoding.GetPreamble();
        if (preamble.Length > 0 && bytes.StartsWith(preamble))
        {
            bytes = bytes[preamble.Length..];
        }

        return encoding.GetString(bytes);
    }

    /// <summary>파일을 읽어 문자열로. 다른 프로그램이 쓰는 중이어도 읽을 수 있게 공유 모드로 연다.</summary>
    public static async Task<(string Text, Encoding Encoding)> ReadFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, useAsync: true);
        var buffer = new byte[fs.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await fs.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        var text = Decode(buffer.AsSpan(0, read), out var enc);
        return (text, enc);
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes) => System.Text.Unicode.Utf8.IsValid(bytes);
}
