using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Foldy.Platform.Imaging;

/// <summary>JPEG에서 읽은 EXIF 정보. 없는 값은 null.</summary>
public sealed class ExifInfo
{
    /// <summary>촬영일 (DateTimeOriginal, 없으면 DateTime). 카메라 현지 시각 그대로.</summary>
    public DateTime? DateTaken { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    /// <summary>EXIF 방향 1~8 (1 = 그대로).</summary>
    public int Orientation { get; init; } = 1;

    /// <summary>원본 해상도 (SOF 헤더 우선, 없으면 EXIF PixelX/YDimension). 회전 전 값.</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>노출 시간 (초).</summary>
    public double? ExposureTime { get; init; }

    public double? FNumber { get; init; }

    public int? Iso { get; init; }

    /// <summary>초점 거리 (mm).</summary>
    public double? FocalLength { get; init; }

    /// <summary>IFD1에 들어 있는 JPEG 썸네일 (보통 160×120). 없으면 비어 있음.</summary>
    public ReadOnlyMemory<byte> Thumbnail { get; init; }

    /// <summary>방향 5~8은 가로·세로가 바뀐다.</summary>
    public bool SwapsAxes => Orientation is >= 5 and <= 8;

    /// <summary>"Canon EOS R5"처럼 제조사와 모델을 합친 이름. 모델에 제조사가 들어 있으면 모델만.</summary>
    public string? Camera
    {
        get
        {
            var make = Make?.Trim();
            var model = Model?.Trim();
            if (string.IsNullOrEmpty(model))
            {
                return string.IsNullOrEmpty(make) ? null : make;
            }

            if (string.IsNullOrEmpty(make))
            {
                return model;
            }

            var firstWord = make.Split(' ', 2)[0];
            return model.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase) ? model : make + " " + model;
        }
    }
}

/// <summary>
/// 의존성 없는 최소 EXIF 리더 (JPEG APP1 + TIFF IFD). 세그먼트 머리만 읽고 나머지는 건너뛰므로
/// 큰 사진도 몇 KB만 읽는다. 잘못된 파일에는 예외 대신 null을 돌려준다.
/// 스레드 안전 (상태 없음).
/// </summary>
public static class ExifReader
{
    private const int MaxSegments = 64;

    /// <summary>
    /// 파일에서 EXIF를 읽는다. JPEG이 아니거나 클라우드 전용 파일이면 null (내용을 열지 않는다).
    /// </summary>
    public static ExifInfo? TryReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (CloudFileGuard.MustNotReadContent(path))
        {
            return null;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            return Read(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>JPEG 스트림에서 EXIF를 읽는다 (탐색 가능한 스트림). JPEG이 아니면 null.</summary>
    public static ExifInfo? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> hdr = stackalloc byte[4];
        if (ReadFully(stream, hdr[..2]) != 2 || hdr[0] != 0xFF || hdr[1] != 0xD8)
        {
            return null;
        }

        byte[]? exif = null;
        int width = 0, height = 0;
        for (var seg = 0; seg < MaxSegments; seg++)
        {
            // 마커: FF xx (앞쪽 FF 채움 바이트는 건너뛴다)
            var b = stream.ReadByte();
            if (b != 0xFF)
            {
                break;
            }

            int marker;
            do
            {
                marker = stream.ReadByte();
            }
            while (marker == 0xFF);

            if (marker < 0 || marker == 0xD9 || marker == 0xDA)
            {
                break; // EOI 또는 SOS: 메타데이터 끝
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                continue; // 길이 없는 마커
            }

            if (ReadFully(stream, hdr[..2]) != 2)
            {
                break;
            }

            var len = BinaryPrimitives.ReadUInt16BigEndian(hdr) - 2;
            if (len < 0)
            {
                break;
            }

            var start = stream.Position;
            if (marker == 0xE1 && exif is null && len > 6)
            {
                var buf = new byte[len];
                if (ReadFully(stream, buf) != len)
                {
                    break;
                }

                if (buf.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8))
                {
                    exif = buf;
                }
            }
            else if (IsSof(marker) && len >= 5)
            {
                Span<byte> sof = stackalloc byte[5];
                if (ReadFully(stream, sof) != 5)
                {
                    break;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(sof[1..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(sof[3..]);
                break; // SOF는 APP 세그먼트 뒤에 온다: 더 읽을 것이 없다
            }

            stream.Seek(start + len, SeekOrigin.Begin);
        }

        var info = exif is null ? new ExifInfo() : ParseTiff(exif.AsMemory(6));
        return new ExifInfo
        {
            DateTaken = info.DateTaken,
            Make = info.Make,
            Model = info.Model,
            Orientation = info.Orientation,
            Width = width > 0 ? width : info.Width,
            Height = height > 0 ? height : info.Height,
            ExposureTime = info.ExposureTime,
            FNumber = info.FNumber,
            Iso = info.Iso,
            FocalLength = info.FocalLength,
            Thumbnail = info.Thumbnail,
        };
    }

    private static bool IsSof(int m) => m is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;

    private static int ReadFully(Stream s, Span<byte> buf)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = s.Read(buf[total..]);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>"Exif\0\0" 다음의 TIFF 블록을 해석한다. 범위를 벗어난 값은 무시한다.</summary>
    internal static ExifInfo ParseTiff(ReadOnlyMemory<byte> tiffMemory)
    {
        var t = new Tiff(tiffMemory);
        if (!t.Valid)
        {
            return new ExifInfo();
        }

        string? make = null, model = null, dateTime = null, dateOriginal = null;
        int orientation = 1, pxW = 0, pxH = 0;
        double? exposure = null, fnumber = null, focal = null;
        int? iso = null;
        uint exifIfd = 0, thumbOffset = 0, thumbLength = 0;

        var ifd0 = t.ReadUInt32(4);
        var next = t.ForEachEntry(ifd0, e =>
        {
            switch (e.Tag)
            {
                case 0x010F: make = t.Ascii(e); break;
                case 0x0110: model = t.Ascii(e); break;
                case 0x0112: orientation = (int)t.Integer(e); break;
                case 0x0132: dateTime = t.Ascii(e); break;
                case 0x8769: exifIfd = t.Integer(e); break;
            }
        });

        if (exifIfd != 0)
        {
            t.ForEachEntry(exifIfd, e =>
            {
                switch (e.Tag)
                {
                    case 0x9003: dateOriginal = t.Ascii(e); break;
                    case 0xA002: pxW = (int)t.Integer(e); break;
                    case 0xA003: pxH = (int)t.Integer(e); break;
                    case 0x829A: exposure = t.Rational(e); break;
                    case 0x829D: fnumber = t.Rational(e); break;
                    case 0x8827: iso = (int)t.Integer(e); break;
                    case 0x920A: focal = t.Rational(e); break;
                }
            });
        }

        if (next != 0)
        {
            t.ForEachEntry(next, e =>
            {
                switch (e.Tag)
                {
                    case 0x0201: thumbOffset = t.Integer(e); break;
                    case 0x0202: thumbLength = t.Integer(e); break;
                }
            });
        }

        var thumb = ReadOnlyMemory<byte>.Empty;
        if (thumbOffset > 0 && thumbLength > 2 && (long)thumbOffset + thumbLength <= tiffMemory.Length)
        {
            var slice = tiffMemory.Slice((int)thumbOffset, (int)thumbLength);
            if (slice.Span[0] == 0xFF && slice.Span[1] == 0xD8)
            {
                thumb = slice;
            }
        }

        return new ExifInfo
        {
            Make = make,
            Model = model,
            Orientation = orientation is >= 1 and <= 8 ? orientation : 1,
            DateTaken = ParseDate(dateOriginal) ?? ParseDate(dateTime),
            Width = pxW,
            Height = pxH,
            ExposureTime = exposure,
            FNumber = fnumber,
            Iso = iso,
            FocalLength = focal,
            Thumbnail = thumb,
        };
    }

    /// <summary>EXIF 날짜 "yyyy:MM:dd HH:mm:ss".</summary>
    internal static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        return DateTime.TryParseExact(s.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private readonly record struct Entry(ushort Tag, ushort Type, uint Count, int ValueOffset);

    /// <summary>TIFF 바이트 순서를 감춘 읽기 도우미.</summary>
    private readonly struct Tiff
    {
        private readonly ReadOnlyMemory<byte> _data;
        private readonly bool _little;

        public Tiff(ReadOnlyMemory<byte> data)
        {
            _data = data;
            var s = data.Span;
            if (s.Length < 8)
            {
                Valid = false;
                return;
            }

            _little = s[0] == 'I' && s[1] == 'I';
            var big = s[0] == 'M' && s[1] == 'M';
            Valid = (_little || big) && ReadUInt16At(s, 2, _little) == 42;
        }

        public bool Valid { get; }

        private ReadOnlySpan<byte> S => _data.Span;

        private static ushort ReadUInt16At(ReadOnlySpan<byte> s, int o, bool little) =>
            little ? BinaryPrimitives.ReadUInt16LittleEndian(s[o..]) : BinaryPrimitives.ReadUInt16BigEndian(s[o..]);

        public ushort ReadUInt16(int o) => o >= 0 && o + 2 <= S.Length ? ReadUInt16At(S, o, _little) : (ushort)0;

        public uint ReadUInt32(int o) => o >= 0 && o + 4 <= S.Length
            ? (_little ? BinaryPrimitives.ReadUInt32LittleEndian(S[o..]) : BinaryPrimitives.ReadUInt32BigEndian(S[o..]))
            : 0u;

        /// <summary>IFD의 항목을 차례로 넘기고 다음 IFD 오프셋을 돌려준다.</summary>
        public uint ForEachEntry(uint ifdOffset, Action<Entry> visit)
        {
            if (ifdOffset == 0 || ifdOffset + 2 > S.Length)
            {
                return 0;
            }

            var o = (int)ifdOffset;
            int n = ReadUInt16(o);
            if (n > 512 || o + 2 + n * 12 + 4 > S.Length)
            {
                return 0;
            }

            for (var i = 0; i < n; i++)
            {
                var p = o + 2 + i * 12;
                var type = ReadUInt16(p + 2);
                var count = ReadUInt32(p + 4);
                var size = TypeSize(type) * (long)count;
                var valueOffset = size <= 4 ? p + 8 : (int)Math.Min(int.MaxValue, ReadUInt32(p + 8));
                if (size > 4 && valueOffset + size > S.Length)
                {
                    continue;
                }

                visit(new Entry(ReadUInt16(p), type, count, valueOffset));
            }

            return ReadUInt32(o + 2 + n * 12);
        }

        private static int TypeSize(ushort type) => type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 => 4,
            5 or 10 or 12 => 8,
            _ => 0,
        };

        public string? Ascii(Entry e)
        {
            if (e.Type != 2 || e.Count == 0)
            {
                return null;
            }

            var bytes = S.Slice(e.ValueOffset, (int)e.Count);
            var end = bytes.IndexOf((byte)0);
            var text = Encoding.UTF8.GetString(end >= 0 ? bytes[..end] : bytes).Trim();
            return text.Length == 0 ? null : text;
        }

        public uint Integer(Entry e) => e.Type switch
        {
            3 or 8 => ReadUInt16(e.ValueOffset),
            4 or 9 => ReadUInt32(e.ValueOffset),
            1 or 7 => e.ValueOffset < S.Length ? S[e.ValueOffset] : 0u,
            _ => 0u,
        };

        public double? Rational(Entry e)
        {
            if (e.Type is not (5 or 10))
            {
                return e.Type is 3 or 4 ? Integer(e) : null;
            }

            var num = ReadUInt32(e.ValueOffset);
            var den = ReadUInt32(e.ValueOffset + 4);
            if (den == 0)
            {
                return null;
            }

            return e.Type == 10 ? (double)(int)num / (int)den : (double)num / den;
        }
    }
}
