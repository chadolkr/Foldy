using System.Globalization;

namespace Foldy.Git;

/// <summary>blame 한 줄.</summary>
/// <param name="LineNo">최종 파일의 줄 번호 (1부터).</param>
/// <param name="BlockIndex">같은 커밋이 이어지는 덩어리의 순번 (번갈아 음영을 줄 때 쓴다).</param>
/// <param name="IsBlockStart">덩어리의 첫 줄.</param>
public sealed record BlameLine(
    int LineNo, string Hash, string Author, DateTimeOffset Date, string Summary, string Text,
    bool IsBoundary, bool IsUncommitted, int BlockIndex, bool IsBlockStart)
{
    public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;
}

/// <summary><c>git blame --porcelain</c> 출력 파서 (순수 함수).</summary>
public static class GitBlameParser
{
    private sealed class Meta
    {
        public string Author = "";
        public DateTimeOffset Date;
        public string Summary = "";
        public bool Boundary;
    }

    public static IReadOnlyList<BlameLine> Parse(string porcelain)
    {
        var result = new List<BlameLine>();
        var metas = new Dictionary<string, Meta>(StringComparer.Ordinal);
        string? hash = null;
        var finalLine = 0;
        Meta? cur = null;
        var block = -1;
        string? prevHash = null;
        long authorTime = 0;
        var tz = TimeSpan.Zero;

        foreach (var rawLine in porcelain.Split('\n'))
        {
            var line = rawLine;
            if (line.Length > 0 && line[0] == '\t')
            {
                if (hash is null || cur is null) continue;
                var start = hash != prevHash;
                if (start) block++;
                prevHash = hash;
                var text = line[1..];
                if (text.EndsWith('\r')) text = text[..^1];
                result.Add(new BlameLine(finalLine, hash, cur.Author, cur.Date, cur.Summary, text,
                    cur.Boundary, IsZeros(hash), block, start));
                continue;
            }
            if (line.EndsWith('\r')) line = line[..^1];
            if (line.Length == 0) continue;

            if (TryHeader(line, out var h, out var fl))
            {
                hash = h;
                finalLine = fl;
                if (!metas.TryGetValue(h, out cur))
                {
                    cur = new Meta();
                    metas[h] = cur;
                    authorTime = 0;
                    tz = TimeSpan.Zero;
                }
                continue;
            }
            if (cur is null) continue;

            // 커밋 정보는 그 커밋이 처음 나올 때만 온다
            if (line.StartsWith("author ", StringComparison.Ordinal)) cur.Author = line[7..];
            else if (line.StartsWith("author-time ", StringComparison.Ordinal))
            {
                if (long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out authorTime))
                    cur.Date = DateTimeOffset.FromUnixTimeSeconds(authorTime).ToOffset(tz);
            }
            else if (line.StartsWith("author-tz ", StringComparison.Ordinal))
            {
                tz = ParseTz(line[10..]);
                if (authorTime != 0) cur.Date = DateTimeOffset.FromUnixTimeSeconds(authorTime).ToOffset(tz);
            }
            else if (line.StartsWith("summary ", StringComparison.Ordinal)) cur.Summary = line[8..];
            else if (line == "boundary") cur.Boundary = true;
        }
        return result;
    }

    /// <summary>"&lt;sha&gt; &lt;원본줄&gt; &lt;최종줄&gt; [&lt;묶음 줄 수&gt;]".</summary>
    private static bool TryHeader(string line, out string hash, out int finalLine)
    {
        hash = "";
        finalLine = 0;
        var sp = line.IndexOf(' ');
        if (sp is not (40 or 64)) return false;
        for (var i = 0; i < sp; i++)
        {
            var c = line[i];
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        var parts = line[(sp + 1)..].Split(' ');
        if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out finalLine)) return false;
        hash = line[..sp];
        return true;
    }

    private static bool IsZeros(string hash) => hash.All(c => c == '0');

    /// <summary>"+0900" / "-0330".</summary>
    private static TimeSpan ParseTz(string s)
    {
        if (s.Length != 5 || s[0] is not ('+' or '-')
            || !int.TryParse(s.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hh)
            || !int.TryParse(s.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var mm))
            return TimeSpan.Zero;
        var t = new TimeSpan(hh, mm, 0);
        return s[0] == '-' ? -t : t;
    }
}
