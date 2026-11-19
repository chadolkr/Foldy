using System.Globalization;
using System.Text.RegularExpressions;

namespace Foldy.Git;

public enum DiffLineKind : byte
{
    Context,
    Added,
    Removed,
    /// <summary>"@@ -1,3 +1,4 @@ 함수명" 한 줄.</summary>
    Hunk,
    /// <summary>"\ No newline at end of file" 같은 안내.</summary>
    Meta,
}

/// <summary>diff 한 줄. 줄 번호는 Hunk/Meta 에는 없고, 추가는 OldNo, 삭제는 NewNo 가 없다.</summary>
public sealed record DiffLine(DiffLineKind Kind, int? OldNo, int? NewNo, string Text)
{
    public string Marker => Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "-", _ => " " };

    /// <summary>탭을 공백 4칸으로 펼친 화면용 글.</summary>
    public string Display => Text.Contains('\t') ? Text.Replace("\t", "    ") : Text;
}

/// <summary>파일 하나의 diff 해석 결과.</summary>
/// <param name="Lines">화면에 보일 줄 (최대 maxLines).</param>
/// <param name="Truncated">Lines 가 전체보다 짧다 (더 보기 가능).</param>
/// <param name="TotalLines">잘리지 않았을 때의 전체 줄 수 (hunk 머리글 포함).</param>
public sealed record FileDiff(IReadOnlyList<DiffLine> Lines, bool IsBinary, bool Truncated, int TotalLines, int AddedCount, int RemovedCount)
{
    public static FileDiff Empty { get; } = new([], false, false, 0, 0, 0);

    /// <summary>바이너리도 아니고 보여 줄 줄도 없다 (모드만 바뀐 경우 등).</summary>
    public bool IsEmpty => !IsBinary && Lines.Count == 0;
}

/// <summary>나란히 보기의 한 줄. Header 가 있으면 가로 전체를 쓰는 hunk 머리글/안내 줄이다.</summary>
public sealed record SideBySideRow(DiffLine? Left, DiffLine? Right, DiffLine? Header)
{
    public bool IsHeader => Header is not null;
}

public enum GitDiffKind : byte
{
    /// <summary>워킹트리 ↔ 인덱스 (git diff).</summary>
    Unstaged,
    /// <summary>인덱스 ↔ HEAD (git diff --cached).</summary>
    Staged,
    /// <summary>한 커밋이 바꾼 내용 (git show).</summary>
    Commit,
}

/// <summary>diff 대상. Path 는 저장소 루트 기준 상대 경로('/').</summary>
/// <param name="Untracked">추적 안 하는 새 파일 (git diff --no-index 로 전체를 추가로 보여 준다).</param>
public sealed record GitDiffTarget(GitDiffKind Kind, string Path, string? OrigPath = null, string? Hash = null, bool Untracked = false);

/// <summary>unified diff 텍스트를 <see cref="FileDiff"/> 로 바꾸는 순수 파서.</summary>
public static partial class GitDiffParser
{
    /// <summary>한 번에 보여 주는 최대 줄 수.</summary>
    public const int DefaultMaxLines = 20_000;

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkRegex();

    public static FileDiff Parse(string text, int maxLines = DefaultMaxLines)
    {
        var lines = new List<DiffLine>();
        int total = 0, added = 0, removed = 0;
        var binary = false;
        var inHunk = false;
        var combined = false;       // "@@@" 병합 diff: 앞 두 글자가 부모별 표시
        int oldNo = 0, newNo = 0, oldLeft = 0, newLeft = 0;

        void Emit(DiffLineKind kind, int? o, int? n, string t)
        {
            total++;
            if (lines.Count < maxLines) lines.Add(new DiffLine(kind, o, n, t));
        }

        var pos = 0;
        while (pos < text.Length)
        {
            var nl = text.IndexOf('\n', pos);
            var end = nl < 0 ? text.Length : nl;
            var raw = text.AsSpan(pos, end - pos);
            pos = nl < 0 ? text.Length : nl + 1;
            if (raw.Length > 0 && raw[^1] == '\r') raw = raw[..^1];

            if (inHunk && raw.Length > 0 && raw[0] == '\\')
            {
                Emit(DiffLineKind.Meta, null, null, raw.ToString());
                continue;
            }

            if (inHunk && combined)
            {
                if (raw.StartsWith("@@@") || raw.StartsWith("diff ")) { inHunk = false; }
                else
                {
                    var pre = raw.Length >= 2 ? raw[..2] : raw;
                    var body = raw.Length >= 2 ? raw[2..].ToString() : "";
                    if (pre.Contains('+')) { added++; Emit(DiffLineKind.Added, null, null, body); }
                    else if (pre.Contains('-')) { removed++; Emit(DiffLineKind.Removed, null, null, body); }
                    else Emit(DiffLineKind.Context, null, null, body);
                    continue;
                }
            }

            if (inHunk && (oldLeft > 0 || newLeft > 0))
            {
                var c = raw.Length > 0 ? raw[0] : ' ';
                var body = raw.Length > 0 ? raw[1..].ToString() : "";
                if (c == '+')
                {
                    added++; newLeft--;
                    Emit(DiffLineKind.Added, null, newNo++, body);
                }
                else if (c == '-')
                {
                    removed++; oldLeft--;
                    Emit(DiffLineKind.Removed, oldNo++, null, body);
                }
                else
                {
                    oldLeft--; newLeft--;
                    Emit(DiffLineKind.Context, oldNo++, newNo++, body);
                }
                continue;
            }

            // 본문 밖: hunk 머리글, 바이너리 안내, 다음 파일 시작
            if (raw.StartsWith("@@@"))
            {
                inHunk = true; combined = true;
                Emit(DiffLineKind.Hunk, null, null, raw.ToString());
            }
            else if (raw.StartsWith("@@"))
            {
                var m = HunkRegex().Match(raw.ToString());
                if (!m.Success) { inHunk = false; continue; }
                inHunk = true; combined = false;
                oldNo = Int(m.Groups[1]);
                oldLeft = m.Groups[2].Success ? Int(m.Groups[2]) : 1;
                newNo = Int(m.Groups[3]);
                newLeft = m.Groups[4].Success ? Int(m.Groups[4]) : 1;
                Emit(DiffLineKind.Hunk, null, null, raw.ToString());
            }
            else if (raw.StartsWith("Binary files ") || raw.StartsWith("GIT binary patch"))
            {
                binary = true;
                inHunk = false;
            }
            else if (raw.StartsWith("diff "))
            {
                inHunk = false;
            }
        }

        return new FileDiff(lines, binary, lines.Count < total, total, added, removed);
    }

    private static int Int(Group g) => int.Parse(g.Value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    /// <summary>
    /// 나란히 보기용 행으로 바꾼다. 연속된 삭제/추가 덩어리는 위에서부터 짝지어 같은 행에 놓고,
    /// 남는 쪽은 반대편을 비운다. Hunk/Meta 줄은 가로 전체 머리글 행이 된다.
    /// </summary>
    public static IReadOnlyList<SideBySideRow> BuildSideBySide(IReadOnlyList<DiffLine> lines)
    {
        var rows = new List<SideBySideRow>(lines.Count);
        var rem = new List<DiffLine>();
        var add = new List<DiffLine>();

        void Flush()
        {
            var n = Math.Max(rem.Count, add.Count);
            for (var i = 0; i < n; i++)
                rows.Add(new SideBySideRow(i < rem.Count ? rem[i] : null, i < add.Count ? add[i] : null, null));
            rem.Clear();
            add.Clear();
        }

        foreach (var l in lines)
        {
            switch (l.Kind)
            {
                case DiffLineKind.Removed: rem.Add(l); break;
                case DiffLineKind.Added: add.Add(l); break;
                case DiffLineKind.Context:
                    Flush();
                    rows.Add(new SideBySideRow(l, l, null));
                    break;
                default:
                    Flush();
                    rows.Add(new SideBySideRow(null, null, l));
                    break;
            }
        }
        Flush();
        return rows;
    }
}
