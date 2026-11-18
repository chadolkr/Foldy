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

    // 자동 업데이트 (설계서 10.5). 이전 서비스는 이 타입을 모르고 Error 후 연결을 끊는다.
    UpdateStatus = 0x00A1,
    CheckNow = 0x00A2,
    InstallWhenIdle = 0x00A3,
    SkipVersion = 0x00A4,
    SetChannel = 0x00A5,
    AppStarted = 0x00A6,
    HelloReply = 0x8001,
    StatusReply = 0x8002,
    SearchBatch = 0x8003,
    FolderSizesReply = 0x8004,
    UpdateStatusReply = 0x80A1,
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

public sealed record StatusReply(IndexState State, ulong IndexedEntries, long LastUsn, ushort ProgressPermille, IReadOnlyList<VolumeStatus> Volumes);

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

/// <summary>서비스 업데이트 상태 (UpdateStatusReply.State).</summary>
public enum UpdateState : byte
{
    Idle = 0,
    Checking = 1,
    Downloading = 2,
    ReadyToInstall = 3,
    Installing = 4,
    Failed = 5,
}

public enum UpdateChannel : byte
{
    Stable = 0,
    Beta = 1,
}

/// <summary>업데이트 요청의 Error 코드 (<see cref="ProtocolException.ErrorCode"/>).</summary>
public static class UpdateErrorCodes
{
    /// <summary>이 서비스 실행에서는 자동 업데이트가 꺼져 있음 → 기존 방식으로 폴백.</summary>
    public const uint Disabled = 0xA0;

    /// <summary>설치할 준비된 업데이트가 없음.</summary>
    public const uint NotReady = 0xA1;

    public const uint BadArgument = 0xA2;
}

/// <param name="LastCheckUnix">마지막으로 확인에 성공한 시각 (Unix 초, 0 = 없음).</param>
/// <param name="InstallPending">설치 요청을 받아 Foldy 종료를 기다리는 중.</param>
public sealed record UpdateStatusReply(
    UpdateState State,
    string CurrentVersion,
    string AvailableVersion,
    UpdateChannel Channel,
    ushort ProgressPermille,
    ulong Downloaded,
    ulong Total,
    ulong LastCheckUnix,
    string LastError,
    bool Mandatory,
    bool InstallPending,
    string Notes)
{
    public DateTimeOffset? LastCheck => LastCheckUnix == 0 ? null : DateTimeOffset.FromUnixTimeSeconds((long)LastCheckUnix);
}

public sealed class ProtocolException : IOException
{
    public ProtocolException(string message)
        : base(message)
    {
    }

    public ProtocolException(string message, uint errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>서비스가 Error로 답했을 때의 코드 (프레임 자체가 깨졌으면 null).</summary>
    public uint? ErrorCode { get; }
}

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

    // ── 자동 업데이트 요청 (본문에 경로가 없다: 설치 파일은 서비스가 정한다) ──

    public static byte[] EncodeUpdateStatus() => Frame(MsgType.UpdateStatus, []);

    public static byte[] EncodeCheckNow() => Frame(MsgType.CheckNow, []);

    public static byte[] EncodeInstallWhenIdle(bool relaunch, uint sessionId)
    {
        var w = new PostcardWriter();
        w.WriteBool(relaunch);
        w.WriteVarUInt(sessionId);
        return Frame(MsgType.InstallWhenIdle, w.ToArray());
    }

    public static byte[] EncodeSkipVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var w = new PostcardWriter();
        w.WriteString(version);
        return Frame(MsgType.SkipVersion, w.ToArray());
    }

    public static byte[] EncodeSetChannel(UpdateChannel channel)
    {
        var w = new PostcardWriter();
        w.WriteU8((byte)channel);
        return Frame(MsgType.SetChannel, w.ToArray());
    }

    public static byte[] EncodeAppStarted(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var w = new PostcardWriter();
        w.WriteString(version);
        return Frame(MsgType.AppStarted, w.ToArray());
    }

    // ── 응답 디코더 ──

    public static UpdateStatusReply DecodeUpdateStatusReply(ReadOnlySpan<byte> body)
    {
        var r = new PostcardReader(body);
        return new UpdateStatusReply(
            (UpdateState)r.ReadByte(),
            r.ReadString(),
            r.ReadString(),
            (UpdateChannel)r.ReadByte(),
            (ushort)r.ReadVarUInt(),
            r.ReadVarUInt(),
            r.ReadVarUInt(),
            r.ReadVarUInt(),
            r.ReadString(),
            r.ReadBool(),
            r.ReadBool(),
            r.ReadString());
    }

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

        return new StatusReply(state, entries, usn, progress, vols);
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

    public void WriteBool(bool v) => WriteByte(v ? (byte)1 : (byte)0);

    /// <summary>postcard의 u8은 varint가 아니라 바이트 하나.</summary>
    public void WriteU8(byte v) => WriteByte(v);

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
