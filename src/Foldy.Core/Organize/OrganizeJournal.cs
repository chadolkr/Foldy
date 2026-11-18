using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foldy.Core.Organize;

public enum JournalRecordType
{
    Header,
    Mkdir,
    Move,
    Fail,
    Done,
    Undone,
}

/// <summary>정리 저널 레코드 (본문은 JSON).</summary>
public sealed record JournalRecord
{
    public JournalRecordType Type { get; init; }

    public DateTime TimeUtc { get; init; } = DateTime.UtcNow;

    public string? Job { get; init; }

    public string? Root { get; init; }

    public string? Src { get; init; }

    public string? Dst { get; init; }

    public bool IsFolder { get; init; }

    public long Size { get; init; }

    public DateTime? LastWriteUtc { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// 정리 저널 (복사 저널과 같은 형식, 설계서 7.2): 추가 전용 레코드 = [길이 u32 LE][CRC32 u32 LE][UTF-8 JSON 본문].
/// 레코드마다 디스크까지 flush하므로 중간에 꺼져도 앞의 레코드는 남고, 깨진 꼬리는 읽을 때 버린다.
/// </summary>
public sealed class OrganizeJournalWriter : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly FileStream _fs;

    public OrganizeJournalWriter(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public string Path { get; }

    internal static JsonSerializerOptions JsonOptions => Json;

    public void Append(JournalRecord record)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(head, (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], Crc32.Compute(body));
        _fs.Write(head);
        _fs.Write(body);
        _fs.Flush(flushToDisk: true);
    }

    public void Dispose() => _fs.Dispose();
}

public static class OrganizeJournalReader
{
    public const int MaxRecordBytes = 1 << 20;

    /// <summary>읽을 수 있는 레코드까지 읽는다. 깨진 꼬리가 있으면 truncated=true.</summary>
    public static IReadOnlyList<JournalRecord> Read(string path, out bool truncated)
    {
        truncated = false;
        var list = new List<JournalRecord>();
        var data = File.ReadAllBytes(path);
        var pos = 0;
        while (pos < data.Length)
        {
            if (data.Length - pos < 8)
            {
                truncated = true;
                break;
            }

            var len = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            if (len > MaxRecordBytes || data.Length - pos - 8 < len)
            {
                truncated = true;
                break;
            }

            var body = data.AsSpan(pos + 8, (int)len);
            if (Crc32.Compute(body) != crc)
            {
                truncated = true;
                break;
            }

            try
            {
                var rec = JsonSerializer.Deserialize<JournalRecord>(body, OrganizeJournalWriter.JsonOptions);
                if (rec is not null)
                {
                    list.Add(rec);
                }
            }
            catch (JsonException)
            {
                truncated = true;
                break;
            }

            pos += 8 + (int)len;
        }

        return list;
    }
}

/// <summary>CRC-32 (IEEE 802.3, 다항식 0xEDB88320).</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            t[i] = c;
        }

        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
