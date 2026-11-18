using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Foldy.TestSupport;

/// <summary>시험 JPEG에 넣을 EXIF 값.</summary>
public sealed class ExifSpec
{
    public string? Make { get; init; }

    public string? Model { get; init; }

    public DateTime? DateTaken { get; init; }

    public int Orientation { get; init; } = 1;

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    /// <summary>(분자, 분모)</summary>
    public (uint Num, uint Den)? FNumber { get; init; }

    public (uint Num, uint Den)? ExposureTime { get; init; }

    public ushort? Iso { get; init; }

    public (uint Num, uint Den)? FocalLength { get; init; }

    /// <summary>IFD1에 넣을 JPEG 썸네일.</summary>
    public byte[]? ThumbnailJpeg { get; init; }
}

/// <summary>
/// 시험·벤치용 이미지 생성 (WPF 인코더 + 직접 만든 EXIF APP1). System.Drawing을 쓰지 않는다.
/// 카메라 사진처럼 IFD0(Make·Model·Orientation) → Exif IFD(촬영일·해상도·노출) → IFD1(JPEG 썸네일) 구조를 만든다.
/// WPF 객체를 만들므로 STA 스레드에서 부르는 것이 안전하다.
/// </summary>
public static class TestImages
{
    /// <summary>시드별로 다른 그라데이션 + 격자 무늬 (압축률이 실제 사진과 비슷하게 너무 단순하지 않게).</summary>
    public static BitmapSource CreatePattern(int width, int height, int seed = 0)
    {
        var stride = width * 4;
        var px = new byte[stride * height];
        var rnd = new Random(seed);
        var (r0, g0, b0) = (rnd.Next(256), rnd.Next(256), rnd.Next(256));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = y * stride + x * 4;
                var grid = ((x / 32) + (y / 32)) % 2 == 0 ? 24 : 0;
                px[o] = (byte)((b0 + x * 255 / Math.Max(1, width)) % 256);
                px[o + 1] = (byte)((g0 + y * 255 / Math.Max(1, height) + grid) % 256);
                px[o + 2] = (byte)((r0 + (x + y) * 128 / Math.Max(1, width + height) + (x * y % 7)) % 256);
                px[o + 3] = 255;
            }
        }

        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    public static byte[] EncodeJpeg(BitmapSource source, int quality = 85)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>JPEG을 만들고 SOI 바로 뒤에 EXIF APP1을 끼워 넣는다.</summary>
    public static byte[] CreateJpegWithExif(int width, int height, ExifSpec spec, int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var main = EncodeJpeg(CreatePattern(width, height, seed));
        return InsertApp1(main, BuildExifApp1(spec));
    }

    public static byte[] InsertApp1(byte[] jpeg, byte[] app1)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        ArgumentNullException.ThrowIfNull(app1);
        var result = new byte[jpeg.Length + app1.Length];
        jpeg.AsSpan(0, 2).CopyTo(result);
        app1.CopyTo(result, 2);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + app1.Length));
        return result;
    }

    /// <summary>FF E1 [길이] "Exif\0\0" [리틀 엔디언 TIFF].</summary>
    public static byte[] BuildExifApp1(ExifSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var ifd0 = new List<Entry>();
        if (spec.Make is { } make)
        {
            ifd0.Add(Entry.Ascii(0x010F, make));
        }

        if (spec.Model is { } model)
        {
            ifd0.Add(Entry.Ascii(0x0110, model));
        }

        ifd0.Add(Entry.Short(0x0112, (ushort)spec.Orientation));
        var exifPtr = Entry.Long(0x8769, 0);
        ifd0.Add(exifPtr);

        var exif = new List<Entry>();
        if (spec.ExposureTime is { } et)
        {
            exif.Add(Entry.Rational(0x829A, et.Num, et.Den));
        }

        if (spec.FNumber is { } fn)
        {
            exif.Add(Entry.Rational(0x829D, fn.Num, fn.Den));
        }

        if (spec.Iso is { } iso)
        {
            exif.Add(Entry.Short(0x8827, iso));
        }

        if (spec.DateTaken is { } d)
        {
            exif.Add(Entry.Ascii(0x9003, d.ToString("yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (spec.FocalLength is { } fl)
        {
            exif.Add(Entry.Rational(0x920A, fl.Num, fl.Den));
        }

        if (spec.PixelWidth > 0)
        {
            exif.Add(Entry.Long(0xA002, (uint)spec.PixelWidth));
            exif.Add(Entry.Long(0xA003, (uint)spec.PixelHeight));
        }

        var ifd1 = new List<Entry>();
        Entry? thumbPtr = null;
        if (spec.ThumbnailJpeg is { } thumb)
        {
            ifd1.Add(Entry.Short(0x0103, 6)); // Compression = JPEG
            thumbPtr = Entry.Long(0x0201, 0);
            ifd1.Add(thumbPtr);
            ifd1.Add(Entry.Long(0x0202, (uint)thumb.Length));
        }

        // 배치: 머리(8) → IFD0 → Exif IFD → IFD1 → 값 영역 → 썸네일
        static int IfdSize(List<Entry> e) => e.Count == 0 ? 0 : 2 + 12 * e.Count + 4;
        var ifd0Off = 8;
        var exifOff = ifd0Off + IfdSize(ifd0);
        var ifd1Off = exifOff + IfdSize(exif);
        var dataOff = ifd1Off + IfdSize(ifd1);
        exifPtr.SetLong((uint)exifOff);

        using var data = new MemoryStream();
        var all = ifd0.Concat(exif).Concat(ifd1).ToList();
        foreach (var e in all.Where(e => e.Data.Length > 4))
        {
            e.DataOffset = dataOff + (int)data.Length;
            data.Write(e.Data);
            if (data.Length % 2 == 1)
            {
                data.WriteByte(0); // 워드 정렬
            }
        }

        var thumbOff = dataOff + (int)data.Length;
        thumbPtr?.SetLong((uint)thumbOff);

        using var tiff = new MemoryStream();
        using (var w = new BinaryWriter(tiff, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("II"u8);
            w.Write((ushort)42);
            w.Write((uint)ifd0Off);
            WriteIfd(w, ifd0, ifd1.Count > 0 ? (uint)ifd1Off : 0);
            WriteIfd(w, exif, 0);
            WriteIfd(w, ifd1, 0);
            w.Write(data.ToArray());
            if (spec.ThumbnailJpeg is { } t)
            {
                w.Write(t);
            }
        }

        var payload = tiff.ToArray();
        var segLen = 2 + 6 + payload.Length;
        if (segLen > 0xFFFF)
        {
            throw new ArgumentException("EXIF가 64KB를 넘습니다 (썸네일이 너무 큼).", nameof(spec));
        }

        var app1 = new byte[2 + segLen];
        app1[0] = 0xFF;
        app1[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(app1.AsSpan(2), (ushort)segLen);
        "Exif\0\0"u8.CopyTo(app1.AsSpan(4));
        payload.CopyTo(app1, 10);
        return app1;
    }

    private static void WriteIfd(BinaryWriter w, List<Entry> entries, uint next)
    {
        if (entries.Count == 0)
        {
            return;
        }

        w.Write((ushort)entries.Count);
        foreach (var e in entries.OrderBy(e => e.Tag))
        {
            w.Write(e.Tag);
            w.Write(e.Type);
            w.Write(e.Count);
            if (e.Data.Length <= 4)
            {
                var v = new byte[4];
                e.Data.CopyTo(v, 0);
                w.Write(v);
            }
            else
            {
                w.Write((uint)e.DataOffset);
            }
        }

        w.Write(next);
    }

    private sealed class Entry(ushort tag, ushort type, uint count, byte[] data)
    {
        public ushort Tag { get; } = tag;

        public ushort Type { get; } = type;

        public uint Count { get; } = count;

        public byte[] Data { get; private set; } = data;

        public int DataOffset { get; set; }

        public static Entry Ascii(ushort tag, string s)
        {
            var b = Encoding.ASCII.GetBytes(s + "\0");
            return new Entry(tag, 2, (uint)b.Length, b);
        }

        public static Entry Short(ushort tag, ushort v)
        {
            var b = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            return new Entry(tag, 3, 1, b);
        }

        public static Entry Long(ushort tag, uint v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            return new Entry(tag, 4, 1, b);
        }

        public static Entry Rational(ushort tag, uint num, uint den)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(b, num);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), den);
            return new Entry(tag, 5, 1, b);
        }

        public void SetLong(uint v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            Data = b;
        }
    }
}
