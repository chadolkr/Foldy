using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Foldy.Indexer.Client;

// engine/foldy-proto/src/lib.rs 와 같은 정의. 바꿀 때는 양쪽과 골든 파일을 함께 갱신한다.
// (foldy-proto로부터 이 파일을 생성하는 것은 다음 단계)

public enum MsgType : ushort
{
    Hello = 0x0001,
    Status = 0x0002,
    Search = 0x0003,
    FolderSizes = 0x0004,
    CancelSearch = 0x0005,

    /// <summary>분석 DB 만들기 시작 (v2 확장). 진행률은 StatusReply.Analysis.</summary>
    BuildAnalysis = 0x0006,

    /// <summary>볼륨의 최신 분석 DB 경로·시각과 이전 스냅샷 (v2 확장).</summary>
    AnalysisInfo = 0x0007,
    HelloReply = 0x8001,
    StatusReply = 0x8002,
    SearchBatch = 0x8003,
    FolderSizesReply = 0x8004,
    BuildAnalysisReply = 0x8006,
    AnalysisInfoReply = 0x8007,
    Error = 0x80FF,
}

/// <summary>색인 상태 (StatusReply.State, VolumeStatus.State).</summary>
public enum IndexState : byte
{
    NotReady = 0,
    Indexing = 1,
    Ready = 2,
}

public enum SearchStatus : byte
{
    Ok = 0,
    Cancelled = 1,

    /// <summary>색인이 준비되지 않음. 호출자는 일반 열거 검색으로 폴백한다.</summary>
    NotReady = 2,
    BadQuery = 3,
}

public enum FolderSizesStatus : byte
{
    Ok = 0,
    NotFound = 1,

    /// <summary>이 경로의 볼륨은 색인되지 않았거나 준비 중.</summary>
    NotIndexed = 2,
}

public sealed record Hello(uint Protocol, string Client);

public sealed record HelloReply(uint Protocol, string ServiceVersion, IReadOnlyList<string> Volumes);

public sealed record VolumeStatus(string Volume, IndexState State, ulong Entries, long LastUsn, ushort ProgressPermille, string Message);

public sealed record StatusReply(IndexState State, ulong IndexedEntries, long LastUsn, ushort ProgressPermille, IReadOnlyList<VolumeStatus> Volumes)
{
    /// <summary>분석 DB를 만들었거나 만드는 중인 볼륨 (v2 확장: 옛 서비스면 빈 목록).</summary>
    public IReadOnlyList<AnalysisProgressStatus> Analysis { get; init; } = [];
}

public enum AnalysisJobState : byte
{
    Idle = 0,
    Building = 1,
    Ready = 2,
    Failed = 3,
}

public sealed record AnalysisProgressStatus(string Volume, AnalysisJobState State, ushort ProgressPermille, string Message);

public enum AnalysisStatus : byte
{
    /// <summary>BuildAnalysis: 시작함. AnalysisInfo: 스냅샷 있음.</summary>
    Ok = 0,
    AlreadyRunning = 1,

    /// <summary>색인이 아직 준비되지 않음 → 폴백.</summary>
    NotReady = 2,

    /// <summary>서비스가 다루지 않는 볼륨 → 폴백.</summary>
    UnknownVolume = 3,

    /// <summary>AnalysisInfo: 아직 만든 스냅샷이 없음.</summary>
    NotFound = 4,
}

public sealed record BuildAnalysisReply(AnalysisStatus Status, string Message);

/// <param name="ScanTime">분석 시각 (FILETIME, UTC).</param>
public sealed record AnalysisSnapshotInfo(string DbPath, long ScanTime)
{
    public DateTime ScanTimeUtc => ScanTime > 0 ? DateTime.FromFileTimeUtc(ScanTime) : DateTime.MinValue;
}

public sealed record AnalysisInfoReply(AnalysisStatus Status, string Volume, string DbPath, long ScanTime, IReadOnlyList<AnalysisSnapshotInfo> Previous)
{
    public DateTime ScanTimeUtc => ScanTime > 0 ? DateTime.FromFileTimeUtc(ScanTime) : DateTime.MinValue;
}

/// <param name="Cursor">0이면 처음부터, 아니면 이전 결과의 NextCursor.</param>
public sealed record SearchRequest(uint SearchId, string Query, uint Limit, ulong Cursor);

/// <param name="Size">파일은 논리 크기, 폴더는 하위 전체 크기.</param>
/// <param name="Mtime">수정 시각 (FILETIME, UTC).</param>
public sealed record SearchHit(string Path, ulong Size, ulong AllocSize, long Mtime, bool IsDir)
{
    public DateTime LastWriteTimeUtc => Mtime > 0 ? DateTime.FromFileTimeUtc(Mtime) : DateTime.MinValue;
}

public sealed record SearchBatch(uint SearchId, IReadOnlyList<SearchHit> Hits, bool Done, SearchStatus Status, ulong NextCursor);

/// <summary>SearchAsync가 모은 결과. NextCursor가 0이 아니면 limit에 닿아 더 있다.</summary>
public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, SearchStatus Status, ulong NextCursor);

public sealed record FolderEntry(string Name, bool IsDir, ulong Size, ulong AllocSize);

public sealed record FolderSizesReply(FolderSizesStatus Status, ulong TotalSize, ulong TotalAlloc, IReadOnlyList<FolderEntry> Entries);

public sealed record ErrorReply(uint Code, string Message);

public sealed class ProtocolException(string message) : IOException(message);

public static class Protocol
{
    public const string DefaultPipeName = "Foldy.Indexer.v1";
    public const uint Version = 2;
    public const int HeaderLength = 6;
    public const int MaxBody = 16 * 1024 * 1024;

    public static byte[] Frame(MsgType type, ReadOnlySpan<byte> body)
    {
        var f = new byte[HeaderLength + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(f, (uint)body.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(4), (ushort)type);
        body.CopyTo(f.AsSpan(HeaderLength));
        return f;
    }

    public static (int Length, MsgType Type) ReadHeader(ReadOnlySpan<byte> h)
    {
        var len = BinaryPrimitives.ReadUInt32LittleEndian(h);
        if (len > MaxBody)
        {
            throw new ProtocolException($"frame too large: {len}");
        }

        var type = (MsgType)BinaryPrimitives.ReadUInt16LittleEndian(h[4..]);
        if (!Enum.IsDefined(type))
        {
            throw new ProtocolException($"unknown message type: 0x{(ushort)type:X4}");
        }

        return ((int)len, type);
    }

    // ── 요청 인코더 ──

    public static byte[] Encode(Hello m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var w = new PostcardWriter();
        w.WriteVarUInt(m.Protocol);
        w.WriteString(m.Client);
        return Frame(MsgType.Hello, w.ToArray());
    }

    public static byte[] EncodeStatus() => Frame(MsgType.Status, []);

    public static byte[] Encode(SearchRequest m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var w = new PostcardWriter();
        w.WriteVarUInt(m.SearchId);
        w.WriteString(m.Query);
        w.WriteVarUInt(m.Limit);
        w.WriteVarUInt(m.Cursor);
        return Frame(MsgType.Search, w.ToArray());
    }

    public static byte[] EncodeCancelSearch(uint searchId)
    {
        var w = new PostcardWriter();
        w.WriteVarUInt(searchId);
        return Frame(MsgType.CancelSearch, w.ToArray());
    }

    public static byte[] EncodeFolderSizes(string path)
    {
        var w = new PostcardWriter();
        w.WriteString(path);
        return Frame(MsgType.FolderSizes, w.ToArray());
    }

    public static byte[] EncodeBuildAnalysis(string volume)
    {
        var w = new PostcardWriter();
        w.WriteString(volume);
        return Frame(MsgType.BuildAnalysis, w.ToArray());
    }

    public static byte[] EncodeAnalysisInfo(string volume)
    {
        var w = new PostcardWriter();
        w.WriteString(volume);
        return Frame(MsgType.AnalysisInfo, w.ToArray());
    }

    // ── 응답 디코더 ──

    public static HelloReply DecodeHelloReply(ReadOnlySpan<byte> body)
    {
        var r = new PostcardReader(body);
        var protocol = (uint)r.ReadVarUInt();
        var version = r.ReadString();
        var n = r.ReadLength();
        var vols = new string[n];
        for (var i = 0; i < n; i++)
        {
            vols[i] = r.ReadString();
        }

        return new HelloReply(protocol, version, vols);
    }

    public static StatusReply DecodeStatusReply(ReadOnlySpan<byte> body)
    {
        var r = new PostcardReader(body);
        var state = (IndexState)r.ReadByte();
        var entries = r.ReadVarUInt();
        var usn = r.ReadVarInt();
        var progress = (ushort)r.ReadVarUInt();
        var n = r.ReadLength();
        var vols = new VolumeStatus[n];
        for (var i = 0; i < n; i++)
        {
            vols[i] = new VolumeStatus(r.ReadString(), (IndexState)r.ReadByte(), r.ReadVarUInt(), r.ReadVarInt(), (ushort)r.ReadVarUInt(), r.ReadString());
        }

        // v2 확장: 옛 서비스는 analysis 목록을 보내지 않는다.
        if (r.AtEnd)
        {
            return new StatusReply(state, entries, usn, progress, vols);
        }

        var na = r.ReadLength();
        var analysis = new AnalysisProgressStatus[na];
        for (var i = 0; i < na; i++)
        {
            analysis[i] = new AnalysisProgressStatus(r.ReadString(), (AnalysisJobState)r.ReadByte(), (ushort)r.ReadVarUInt(), r.ReadString());
        }

        return new StatusReply(state, entries, usn, progress, vols) { Analysis = analysis };
    }

    public static BuildAnalysisReply DecodeBuildAnalysisReply(ReadOnlySpan<byte> body)
    {
        var r = new PostcardReader(body);
        return new BuildAnalysisReply((AnalysisStatus)r.ReadByte(), r.ReadString());
    }

    public static AnalysisInfoReply DecodeAnalysisInfoReply(ReadOnlySpan<byte> body)
    {
        var r = new PostcardReader(body);
        var status = (AnalysisStatus)r.ReadByte();
        var volume = r.ReadString();
        var path = r.ReadString();
        var time = r.ReadVarInt();
        var n = r.ReadLength();
        var prev = new AnalysisSnapshotInfo[n];
        for (var i = 0; i < n; i++)
        {
            prev[i] = new AnalysisSnapshotInfo(r.ReadString(), r.ReadVarInt());
        }

        return new AnalysisInfoReply(status, volume, path, time, prev);
    }

    public static SearchBatch DecodeSearchBatch(ReadOnlySpan<byte> body)
    {
        var r = new PostcardReader(body);
        var id = (uint)r.ReadVarUInt();
        var n = r.ReadLength();
        var hits = new SearchHit[n];
        for (var i = 0; i < n; i++)
        {
            hits[i] = new SearchHit(r.ReadString(), r.ReadVarUInt(), r.ReadVarUInt(), r.ReadVarInt(), r.ReadBool());
        }

        return new SearchBatch(id, hits, r.ReadBool(), (SearchStatus)r.ReadByte(), r.ReadVarUInt());
    }

    public static FolderSizesReply DecodeFolderSizesReply(ReadOnlySpan<byte> body)
    {
        var r = new PostcardReader(body);
        var status = (FolderSizesStatus)r.ReadByte();
        var total = r.ReadVarUInt();
        var alloc = r.ReadVarUInt();
        var n = r.ReadLength();
        var entries = new FolderEntry[n];
        for (var i = 0; i < n; i++)
        {
            entries[i] = new FolderEntry(r.ReadString(), r.ReadBool(), r.ReadVarUInt(), r.ReadVarUInt());
        }

        return new FolderSizesReply(status, total, alloc, entries);
    }

    public static ErrorReply DecodeError(ReadOnlySpan<byte> body)
    {
        var r = new PostcardReader(body);
        return new ErrorReply((uint)r.ReadVarUInt(), r.ReadString());
    }
}

/// <summary>postcard 형식의 최소 구현 (LEB128 varint, zigzag, 길이 접두 문자열).</summary>
internal ref struct PostcardReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;

    public readonly bool AtEnd => _pos >= _data.Length;

    public byte ReadByte() =>
        _pos < _data.Length ? _data[_pos++] : throw new ProtocolException("unexpected end of body");

    public bool ReadBool() => ReadByte() switch
    {
        0 => false,
        1 => true,
        var b => throw new ProtocolException($"invalid bool {b}"),
    };

    public ulong ReadVarUInt()
    {
        ulong v = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            var b = ReadByte();
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return v;
            }
        }

        throw new ProtocolException("varint too long");
    }

    public long ReadVarInt()
    {
        var z = ReadVarUInt();
        return (long)(z >> 1) ^ -(long)(z & 1);
    }

    public int ReadLength()
    {
        var n = ReadVarUInt();
        if (n > (ulong)(_data.Length - _pos))
        {
            throw new ProtocolException("length exceeds body");
        }

        return (int)n;
    }

    public string ReadString()
    {
        var n = ReadLength();
        var s = Encoding.UTF8.GetString(_data.Slice(_pos, n));
        _pos += n;
        return s;
    }
}

internal sealed class PostcardWriter
{
    private readonly ArrayBufferWriter<byte> _buf = new();

    public void WriteVarUInt(ulong v)
    {
        while (v >= 0x80)
        {
            WriteByte((byte)(v | 0x80));
            v >>= 7;
        }

        WriteByte((byte)v);
    }

    private void WriteByte(byte b)
    {
        _buf.GetSpan(1)[0] = b;
        _buf.Advance(1);
    }

    public void WriteString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteVarUInt((ulong)bytes.Length);
        _buf.Write(bytes);
    }

    public byte[] ToArray() => _buf.WrittenSpan.ToArray();
}
