using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Foldy.Indexer.Client;

// engine/foldy-proto/src/lib.rs 와 같은 정의. 바꿀 때는 양쪽과 골든 파일을 함께 갱신한다.
// (M2에서 foldy-proto로부터 이 파일을 생성하도록 바꿀 예정)

public enum MsgType : ushort
{
    Hello = 0x0001,
    Status = 0x0002,
    HelloReply = 0x8001,
    StatusReply = 0x8002,
    Error = 0x80FF,
}

public sealed record Hello(uint Protocol, string Client);

public sealed record HelloReply(uint Protocol, string ServiceVersion, IReadOnlyList<string> Volumes);

public sealed record StatusReply(byte State, ulong IndexedEntries, long LastUsn);

public sealed record ErrorReply(uint Code, string Message);

public sealed class ProtocolException(string message) : IOException(message);

public static class Protocol
{
    public const string DefaultPipeName = "Foldy.Indexer.v1";
    public const uint Version = 1;
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

    public static byte[] Encode(Hello m)
    {
        var w = new PostcardWriter();
        w.WriteVarUInt(m.Protocol);
        w.WriteString(m.Client);
        return Frame(MsgType.Hello, w.ToArray());
    }

    public static byte[] EncodeStatus() => Frame(MsgType.Status, []);

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
        return new StatusReply(r.ReadByte(), r.ReadVarUInt(), r.ReadVarInt());
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
