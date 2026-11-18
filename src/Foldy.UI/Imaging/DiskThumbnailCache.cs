using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace Foldy.UI.Imaging;

/// <summary>
/// 디스크 썸네일 캐시 (설계서 5.12, 선택 사항). 파일 하나(<c>%LOCALAPPDATA%\Foldy\thumbs.cache</c>)에 레코드를 덧붙이고,
/// 색인은 열 때 레코드 머리만 훑어 메모리에 만든다 (SQLite 없이).
/// <para>
/// 형식: "FTC1" 다음에 레코드 반복. 레코드 = [int32 길이][경로(UTF-8, 7비트 길이 접두)][int64 수정 시각][int64 파일 크기]
/// [int32 픽셀 크기][int32 데이터 길이][데이터(JPEG, 투명하면 PNG)]. 같은 키는 뒤 레코드가 이긴다.
/// 상한(기본 500MB)을 넘으면 최근 레코드만 절반 크기까지 남기고 다시 쓴다. 꼬리가 깨졌으면 잘라 낸다.
/// </para>
/// 스레드 안전 (모든 파일 접근은 잠금 안). 다른 Foldy 인스턴스가 쓰고 있으면 <see cref="TryOpen"/>이 null.
/// </summary>
public sealed class DiskThumbnailCache : IDisposable
{
    public const long DefaultMaxBytes = 500L * 1024 * 1024;

    private const int MaxRecordBytes = 16 * 1024 * 1024;
    private static readonly byte[] Magic = "FTC1"u8.ToArray();

    private readonly Lock _gate = new();
    private readonly Dictionary<ThumbKey, (long Offset, int Length)> _index = [];
    private readonly string _path;
    private FileStream _file;
    private bool _disposed;

    private DiskThumbnailCache(string path, FileStream file, long maxBytes)
    {
        _path = path;
        _file = file;
        MaxBytes = maxBytes;
        LoadIndex();
    }

    /// <summary>기본 위치: %LOCALAPPDATA%\Foldy\thumbs.cache</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy", "thumbs.cache");

    public long MaxBytes { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    public long FileBytes
    {
        get
        {
            lock (_gate)
            {
                return _disposed ? 0 : _file.Length;
            }
        }
    }

    /// <summary>캐시 파일을 연다. 열 수 없으면(잠김, 권한) null — 디스크 캐시 없이 동작하면 된다.</summary>
    public static DiskThumbnailCache? TryOpen(string? path = null, long maxBytes = DefaultMaxBytes)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            return new DiskThumbnailCache(path, fs, maxBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Contains(ThumbKey key)
    {
        lock (_gate)
        {
            return _index.ContainsKey(key);
        }
    }

    /// <summary>캐시에서 썸네일을 꺼내 디코딩한다 (Freeze됨). 없으면 null. 백그라운드 스레드에서 부른다.</summary>
    public BitmapSource? TryGet(ThumbKey key)
    {
        byte[] data;
        lock (_gate)
        {
            if (_disposed || !_index.TryGetValue(key, out var loc))
            {
                return null;
            }

            data = new byte[loc.Length];
            _file.Position = loc.Offset;
            _file.ReadExactly(data);
        }

        try
        {
            using var ms = new MemoryStream(data, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException)
        {
            lock (_gate)
            {
                _index.Remove(key);
            }

            return null;
        }
    }

    /// <summary>썸네일을 압축해 덧붙인다. 백그라운드 스레드에서 부른다.</summary>
    public void Put(ThumbKey key, BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var data = Encode(image);
        using var payload = new MemoryStream();
        using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(key.Path);
            w.Write(key.ModifiedTicks);
            w.Write(key.Length);
            w.Write(key.PixelSize);
            w.Write(data.Length);
        }

        var headerLen = (int)payload.Length;
        payload.Write(data);
        if (payload.Length > MaxRecordBytes)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var start = _file.Length;
            _file.Position = start;
            Span<byte> len = stackalloc byte[4];
            BitConverter.TryWriteBytes(len, (int)payload.Length);
            _file.Write(len);
            payload.Position = 0;
            payload.CopyTo(_file);
            _file.Flush();
            _index[key] = (start + 4 + headerLen, data.Length);

            if (_file.Length > MaxBytes)
            {
                Compact(MaxBytes / 2);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _file.Dispose();
            _index.Clear();
        }
    }

    private static byte[] Encode(BitmapSource image)
    {
        BitmapEncoder enc = HasTransparency(image) ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static bool HasTransparency(BitmapSource image)
    {
        if (image.Format.BitsPerPixel != 32)
        {
            return false;
        }

        var stride = image.PixelWidth * 4;
        var px = new byte[stride * image.PixelHeight];
        image.CopyPixels(px, stride, 0);
        for (var i = 3; i < px.Length; i += 4)
        {
            if (px[i] != 255)
            {
                return true;
            }
        }

        return false;
    }

    private void LoadIndex()
    {
        if (_file.Length < Magic.Length)
        {
            _file.SetLength(0);
            _file.Write(Magic);
            _file.Flush();
            return;
        }

        var head = new byte[Magic.Length];
        _file.Position = 0;
        _file.ReadExactly(head);
        if (!head.AsSpan().SequenceEqual(Magic))
        {
            _file.SetLength(0);
            _file.Position = 0;
            _file.Write(Magic);
            _file.Flush();
            return;
        }

        var pos = (long)Magic.Length;
        var lenBuf = new byte[4];
        using var reader = new BinaryReader(_file, Encoding.UTF8, leaveOpen: true);
        while (pos + 4 <= _file.Length)
        {
            _file.Position = pos;
            _file.ReadExactly(lenBuf);
            var recLen = BitConverter.ToInt32(lenBuf);
            if (recLen <= 0 || recLen > MaxRecordBytes || pos + 4 + recLen > _file.Length)
            {
                break;
            }

            try
            {
                var path = reader.ReadString();
                var mtime = reader.ReadInt64();
                var length = reader.ReadInt64();
                var size = reader.ReadInt32();
                var dataLen = reader.ReadInt32();
                var dataOffset = _file.Position;
                if (dataLen <= 0 || dataOffset + dataLen != pos + 4 + recLen)
                {
                    break;
                }

                _index[new ThumbKey(path, mtime, length, size)] = (dataOffset, dataLen);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or FormatException)
            {
                break;
            }

            pos += 4 + recLen;
        }

        if (pos < _file.Length)
        {
            _file.SetLength(pos); // 깨진 꼬리 (쓰다가 꺼진 경우)
        }
    }

    /// <summary>최근(파일 뒤쪽) 레코드부터 <paramref name="targetBytes"/>까지 남기고 새 파일로 바꾼다. 잠금 안에서 부른다.</summary>
    private void Compact(long targetBytes)
    {
        var keep = new List<(ThumbKey Key, byte[] Data)>();
        long total = Magic.Length;
        foreach (var (key, loc) in _index.OrderByDescending(kv => kv.Value.Offset))
        {
            var recBytes = loc.Length + Encoding.UTF8.GetByteCount(key.Path) + 40;
            if (total + recBytes > targetBytes)
            {
                break;
            }

            var data = new byte[loc.Length];
            _file.Position = loc.Offset;
            _file.ReadExactly(data);
            keep.Add((key, data));
            total += recBytes;
        }

        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new BinaryWriter(fs, Encoding.UTF8))
        {
            w.Write(Magic);
            _index.Clear();
            keep.Reverse(); // 오래된 것부터 써서 순서를 유지한다
            foreach (var (key, data) in keep)
            {
                using var payload = new MemoryStream();
                using (var pw = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
                {
                    pw.Write(key.Path);
                    pw.Write(key.ModifiedTicks);
                    pw.Write(key.Length);
                    pw.Write(key.PixelSize);
                    pw.Write(data.Length);
                }

                var headerLen = payload.Length;
                payload.Write(data);
                w.Write((int)payload.Length);
                var recStart = fs.Position;
                w.Write(payload.GetBuffer(), 0, (int)payload.Length);
                _index[key] = (recStart + headerLen, data.Length);
            }
        }

        _file.Dispose();
        File.Move(tmp, _path, overwrite: true);
        _file = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
    }
}
