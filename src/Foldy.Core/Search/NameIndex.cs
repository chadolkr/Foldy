using System.Text;

namespace Foldy.Core.Search;

/// <summary>
/// 모아 둔 목록 파일(<see cref="CrawlStore"/>)의 이름만 메모리에 담아 두고 찾는다 (1.18.0, 2026-11-28).
/// 목록 파일을 찾을 때마다 처음부터 읽으면 C:(200만 줄, 260MB)에서 흔치 않은 낱말 하나에 0.7초가 걸렸다.
/// 이름(마지막 부분)만 대문자 UTF-8로 한 덩어리에 이어 두면 46MB이고, 한 번 훑는 데 5~9ms다 (노트북 실측).
/// 찾은 줄만 파일에서 그 자리로 가 읽는다. 파일이 바뀌면(크기·시각) 쓰지 않고 다시 만든다.
/// </summary>
internal sealed class NameIndex
{
    private NameIndex(string file, long length, DateTime written, byte[] names, int[] starts, long[] lines)
    {
        File = file;
        Length = length;
        Written = written;
        _names = names;
        _starts = starts;
        _lines = lines;
    }

    private readonly byte[] _names; // 이름마다 대문자 UTF-8 + 0
    private readonly int[] _starts; // 줄마다 _names 안의 이름 시작
    private readonly long[] _lines; // 줄마다 파일 안의 줄 시작

    public string File { get; }

    public long Length { get; }

    public DateTime Written { get; }

    public int Count => _starts.Length;

    /// <summary>메모리에 쓰는 크기 (대략, 바이트).</summary>
    public long Bytes => _names.LongLength + (_starts.LongLength * 4) + (_lines.LongLength * 8);

    /// <summary>파일이 만들 때와 그대로인가 (크기와 마지막 쓴 시각).</summary>
    public bool StillFits()
    {
        try
        {
            var fi = new FileInfo(File);
            return fi.Exists && fi.Length == Length && fi.LastWriteTimeUtc == Written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>목록 파일에서 만든다. 파일이 없거나 읽는 도중 바뀌면 null.</summary>
    public static NameIndex? Build(string file, CancellationToken ct = default)
    {
        FileInfo before;
        try
        {
            before = new FileInfo(file);
            if (!before.Exists || before.Length >= int.MaxValue * 8L)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var (length, written) = (before.Length, before.LastWriteTimeUtc);
        var estimate = (int)Math.Clamp(length / 120, 16, 50_000_000); // 줄 하나 평균 130바이트쯤
        var names = new Growable<byte>(Math.Max(1024, estimate * 24));
        var starts = new Growable<int>(estimate);
        var lines = new Growable<long>(estimate);
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            var buf = new byte[1 << 20];
            var carry = new Growable<byte>(4096); // 버퍼 끝에 걸친 줄
            long pos = 0, lineStart = 0;
            int n;
            var batches = 0;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                if ((++batches & 15) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var span = buf.AsSpan(0, n);
                var at = 0;
                while (at < span.Length)
                {
                    var nl = span[at..].IndexOf((byte)'\n');
                    if (nl < 0)
                    {
                        carry.AddRange(span[at..]);
                        break;
                    }

                    if (carry.Count > 0)
                    {
                        carry.AddRange(span.Slice(at, nl));
                        AddLine(carry.AsSpan(), lineStart, names, starts, lines);
                        carry.Clear();
                    }
                    else
                    {
                        AddLine(span.Slice(at, nl), lineStart, names, starts, lines);
                    }

                    lineStart = pos + at + nl + 1;
                    at += nl + 1;
                }

                pos += n;
            }

            if (carry.Count > 0)
            {
                AddLine(carry.AsSpan(), lineStart, names, starts, lines);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var index = new NameIndex(file, length, written, names.ToArray(), starts.ToArray(), lines.ToArray());
        return index.StillFits() ? index : null; // 읽는 사이 바뀌었으면 쓰지 않는다
    }

    /// <summary>줄 하나: 경로 \t ... 에서 이름(마지막 \ 뒤)만 대문자로 담는다.</summary>
    private static void AddLine(ReadOnlySpan<byte> line, long lineStart, Growable<byte> names, Growable<int> starts, Growable<long> lines)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        var tab = line.IndexOf((byte)'\t');
        if (tab <= 0)
        {
            return;
        }

        var path = line[..tab];
        var name = path[(path.LastIndexOf((byte)'\\') + 1)..];
        starts.Add(names.Count);
        lines.Add(lineStart);
        var ascii = true;
        foreach (var b in name)
        {
            if (b >= 0x80)
            {
                ascii = false;
                break;
            }
        }

        if (ascii)
        {
            foreach (var b in name)
            {
                names.Add(b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b);
            }
        }
        else
        {
            names.AddRange(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(name).ToUpperInvariant()));
        }

        names.Add(0);
    }

    /// <summary>낱말을 찾는 모양으로 (대문자 UTF-8). <see cref="StringComparison.OrdinalIgnoreCase"/>와 같게 맞춘다.</summary>
    public static byte[] Key(string word) => Encoding.UTF8.GetBytes(word.ToUpperInvariant());

    /// <summary>이름에 낱말이 모두 든 줄의 파일 안 위치 (앞에서부터, 낱말 하나 이상).</summary>
    public IEnumerable<long> Find(IReadOnlyList<byte[]> words, CancellationToken ct = default)
    {
        if (words.Count == 0)
        {
            yield break;
        }

        // 가장 긴 낱말로 훑고(덜 맞는다) 나머지는 그 이름 안에서만 본다
        var first = words.OrderByDescending(w => w.Length).First();
        var at = 0;
        var seen = 0;
        while (at < _names.Length)
        {
            var i = _names.AsSpan(at).IndexOf(first);
            if (i < 0)
            {
                yield break;
            }

            var hit = at + i;
            var entry = EntryAt(hit);
            var start = _starts[entry];
            var end = entry + 1 < _starts.Length ? _starts[entry + 1] - 1 : _names.Length - 1; // 이름 뒤 0의 자리
            var ok = true;
            foreach (var w in words)
            {
                if (!ReferenceEquals(w, first) && _names.AsSpan(start, end - start).IndexOf(w) < 0)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                yield return _lines[entry];
            }

            at = end + 1;
            if ((++seen & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    private int EntryAt(int pos)
    {
        var i = Array.BinarySearch(_starts, pos);
        return i >= 0 ? i : ~i - 1;
    }

    /// <summary>파일의 그 자리에서 줄 하나를 읽는다.</summary>
    public static string? ReadLineAt(FileStream fs, long offset, byte[] scratch)
    {
        fs.Seek(offset, SeekOrigin.Begin);
        var acc = new Growable<byte>(256);
        int n;
        while ((n = fs.Read(scratch, 0, scratch.Length)) > 0)
        {
            var nl = scratch.AsSpan(0, n).IndexOf((byte)'\n');
            if (nl >= 0)
            {
                acc.AddRange(scratch.AsSpan(0, nl));
                break;
            }

            acc.AddRange(scratch.AsSpan(0, n));
        }

        var line = acc.AsSpan();
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        return line.Length == 0 ? null : Encoding.UTF8.GetString(line);
    }

    /// <summary>늘어나는 배열 (List보다 덜 복사하고 Span으로 쓰려고).</summary>
    private sealed class Growable<T>(int capacity)
        where T : struct
    {
        private T[] _items = new T[Math.Max(16, capacity)];

        public int Count { get; private set; }

        public void Add(T item)
        {
            if (Count == _items.Length)
            {
                Array.Resize(ref _items, checked(_items.Length * 2));
            }

            _items[Count++] = item;
        }

        public void AddRange(ReadOnlySpan<T> items)
        {
            if (Count + items.Length > _items.Length)
            {
                Array.Resize(ref _items, Math.Max(checked(_items.Length * 2), Count + items.Length));
            }

            items.CopyTo(_items.AsSpan(Count));
            Count += items.Length;
        }

        public void Clear() => Count = 0;

        public Span<T> AsSpan() => _items.AsSpan(0, Count);

        public T[] ToArray() => _items.AsSpan(0, Count).ToArray();
    }
}
