using System.Text;

namespace Foldy.Core.Templates;

public enum TemplateIssueKind
{
    AbsolutePath,
    ParentSegment,
    DriveLetter,
    ReservedName,
    InvalidChar,
    TrailingDotOrSpace,
    EmptyName,
    FileTooLarge,
    BinaryContent,
    ForbiddenFileType,
    TooManyNodes,
    TooDeep,
    PathTooLong,
    DuplicateName,
    MissingVariable,
    SchemaVersion,
}

public sealed record TemplateIssue(TemplateIssueKind Kind, string Path, string Message);

/// <summary>
/// 가져온 템플릿 안전 검사 (설계서 5.10 안전 규칙): 절대 경로, .., 드라이브 문자, Windows 예약 이름,
/// 금지 문자, 실행 파일·바로가기, 64KB 넘는/텍스트가 아닌 파일 내용은 거부한다.
/// 변수 자리({{...}})는 치환 뒤에 다시 검사하므로 원문 검사에서는 건너뛴다.
/// </summary>
public static class TemplateValidator
{
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxNodes = 500;
    public const int MaxDepth = 16;
    public const int MaxPathLength = 240;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³", "CONIN$", "CONOUT$",
    };

    /// <summary>만들지 않는 파일 종류 (실행 파일·스크립트·바로가기).</summary>
    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msp", ".scr", ".pif", ".dll", ".sys", ".cpl", ".hta", ".jar", ".reg",
        ".lnk", ".url", ".appref-ms", ".library-ms", ".scf", ".inf",
    };

    public static bool IsInvalidNameChar(char c) =>
        c < 32 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\\' or '/';

    /// <summary>템플릿 원문 검사 (가져오기·저장 전).</summary>
    public static IReadOnlyList<TemplateIssue> Validate(FolderTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var issues = new List<TemplateIssue>();
        if (template.SchemaVersion is < 1 or > FolderTemplate.CurrentSchemaVersion)
        {
            issues.Add(new(TemplateIssueKind.SchemaVersion, "", Foldy.Core.Localization.Loc.F("tv.schema", template.SchemaVersion)));
        }

        var count = 0;
        Walk(template.Tree, "", 0, issues, ref count, raw: true);
        return issues;
    }

    private static void Walk(IList<TemplateNode> nodes, string parent, int depth, List<TemplateIssue> issues, ref int count, bool raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            count++;
            if (count == MaxNodes + 1)
            {
                issues.Add(new(TemplateIssueKind.TooManyNodes, parent, Foldy.Core.Localization.Loc.F("tv.tooMany", MaxNodes)));
            }

            var path = parent.Length == 0 ? n.Name : parent + "/" + n.Name;
            var segments = ValidateRelativePath(n.Name, raw, path, issues);
            // 조건(when)이 다른 같은 이름은 함께 만들어지지 않으므로 허용 (언어별 .gitignore 등)
            if (!seen.Add(n.Name + "\0" + n.When))
            {
                issues.Add(new(TemplateIssueKind.DuplicateName, path, Foldy.Core.Localization.Loc.F("tv.duplicate", n.Name)));
            }

            var d = depth + Math.Max(segments, 1);
            if (d > MaxDepth)
            {
                issues.Add(new(TemplateIssueKind.TooDeep, path, Foldy.Core.Localization.Loc.F("tv.tooDeep", MaxDepth)));
                continue;
            }

            if (n.File is not null)
            {
                ValidateFile(n, path, issues);
                if (n.Children.Count > 0)
                {
                    issues.Add(new(TemplateIssueKind.InvalidChar, path, Foldy.Core.Localization.Loc.T("tv.fileChildren")));
                }
            }
            else
            {
                Walk(n.Children, path, d, issues, ref count, raw);
            }
        }
    }

    private static void ValidateFile(TemplateNode n, string path, List<TemplateIssue> issues)
    {
        var name = LastSegment(n.Name);
        var ext = Path.GetExtension(StripPlaceholders(name));
        if (ext.Length > 0 && ForbiddenExtensions.Contains(ext))
        {
            issues.Add(new(TemplateIssueKind.ForbiddenFileType, path, Foldy.Core.Localization.Loc.F("tv.executable", ext)));
        }

        if (Encoding.UTF8.GetByteCount(n.File!) > MaxFileBytes)
        {
            issues.Add(new(TemplateIssueKind.FileTooLarge, path, Foldy.Core.Localization.Loc.T("tv.fileTooBig")));
        }

        if (!IsText(n.File!))
        {
            issues.Add(new(TemplateIssueKind.BinaryContent, path, Foldy.Core.Localization.Loc.T("tv.textOnly")));
        }
    }

    /// <summary>제어 문자(탭·줄바꿈 제외)가 없으면 텍스트로 본다.</summary>
    public static bool IsText(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (var c in content)
        {
            if (c < 32 && c is not '\t' and not '\n' and not '\r')
            {
                return false;
            }

            if (c is '￾' or '￿')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 상대 경로(<c>/</c> 구분) 검사. 문제를 <paramref name="issues"/>에 넣고 단계 수를 돌려준다.
    /// <paramref name="raw"/>가 참이면 변수 자리는 검사하지 않는다.
    /// </summary>
    public static int ValidateRelativePath(string name, bool raw, string reportPath, ICollection<TemplateIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new(TemplateIssueKind.EmptyName, reportPath, Foldy.Core.Localization.Loc.T("tv.emptyName")));
            return 0;
        }

        var text = raw ? StripPlaceholders(name, "x") : name; // 변수 자리는 평범한 글자로 보고 시작 부분 검사
        if (text.StartsWith('/') || text.StartsWith('\\') || name.StartsWith("//", StringComparison.Ordinal))
        {
            issues.Add(new(TemplateIssueKind.AbsolutePath, reportPath, Foldy.Core.Localization.Loc.T("tv.absolute")));
            return 0;
        }

        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':')
        {
            issues.Add(new(TemplateIssueKind.DriveLetter, reportPath, Foldy.Core.Localization.Loc.T("tv.driveLetter")));
            return 0;
        }

        var segs = name.Split('/', '\\');
        foreach (var rawSeg in segs)
        {
            var seg = raw ? StripPlaceholders(rawSeg) : rawSeg;
            var hadPlaceholder = raw && seg.Length != rawSeg.Length;
            if (seg.Length == 0)
            {
                if (!hadPlaceholder)
                {
                    issues.Add(new(TemplateIssueKind.EmptyName, reportPath, Foldy.Core.Localization.Loc.T("tv.emptySegment")));
                }

                continue;
            }

            ValidateSegment(seg, hadPlaceholder, reportPath, issues);
        }

        return segs.Length;
    }

    /// <summary>이름 한 단계 검사.</summary>
    public static void ValidateSegment(string seg, bool partial, string reportPath, ICollection<TemplateIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(seg);
        ArgumentNullException.ThrowIfNull(issues);
        if (!partial && seg is "." or "..")
        {
            issues.Add(new(TemplateIssueKind.ParentSegment, reportPath, Foldy.Core.Localization.Loc.T("tv.dots")));
            return;
        }

        if (seg.Contains("..", StringComparison.Ordinal) && seg.Trim('.').Length == 0)
        {
            issues.Add(new(TemplateIssueKind.ParentSegment, reportPath, Foldy.Core.Localization.Loc.T("tv.dotdot")));
            return;
        }

        foreach (var c in seg)
        {
            if (IsInvalidNameChar(c))
            {
                issues.Add(new(c == ':' ? TemplateIssueKind.DriveLetter : TemplateIssueKind.InvalidChar, reportPath, Foldy.Core.Localization.Loc.F("tv.badChar", (c < 32 ? "\\x" + ((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture) : c.ToString()))));
                return;
            }
        }

        if (!partial)
        {
            if (seg.EndsWith('.') || seg.EndsWith(' ') || seg.StartsWith(' '))
            {
                issues.Add(new(TemplateIssueKind.TrailingDotOrSpace, reportPath, Foldy.Core.Localization.Loc.T("tv.spaces")));
            }

            if (IsReservedName(seg))
            {
                issues.Add(new(TemplateIssueKind.ReservedName, reportPath, Foldy.Core.Localization.Loc.F("tv.reserved", seg)));
            }
        }
    }

    /// <summary>CON, NUL, COM1 ... (확장자가 붙어도 예약: CON.txt).</summary>
    public static bool IsReservedName(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var s = segment.TrimEnd(' ', '.');
        var dot = s.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot >= 0 ? s[..dot] : s).TrimEnd(' ');
        return Reserved.Contains(stem);
    }

    /// <summary>치환을 마친 이름 한 단계가 안전한지 (계획 단계에서 사용).</summary>
    public static IReadOnlyList<TemplateIssue> ValidateResolvedSegment(string segment, string reportPath)
    {
        var list = new List<TemplateIssue>();
        if (string.IsNullOrWhiteSpace(segment))
        {
            list.Add(new(TemplateIssueKind.EmptyName, reportPath, Foldy.Core.Localization.Loc.T("tv.emptyName")));
        }
        else
        {
            ValidateSegment(segment, partial: false, reportPath, list);
        }

        return list;
    }

    public static bool IsForbiddenFileName(string fileName) => ForbiddenExtensions.Contains(Path.GetExtension(fileName));

    private static string StripPlaceholders(string s, string replacement = "")
    {
        if (!s.Contains("{{", StringComparison.Ordinal))
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var open = s.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(s, i, s.Length - i);
                break;
            }

            var close = s.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                sb.Append(s, i, s.Length - i);
                break;
            }

            sb.Append(s, i, open - i).Append(replacement);
            i = close + 2;
        }

        return sb.ToString();
    }

    private static string LastSegment(string name)
    {
        var i = name.LastIndexOfAny(['/', '\\']);
        return i < 0 ? name : name[(i + 1)..];
    }
}
