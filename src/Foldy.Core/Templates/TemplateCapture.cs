using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Foldy.Core.Descriptions;

namespace Foldy.Core.Templates;

public sealed class TemplateCaptureOptions
{
    public int MaxDepth { get; init; } = 5;

    public int MaxItems { get; init; } = 500;

    /// <summary>내용까지 넣을 파일 (상대 경로 기준). 64KB 이하 텍스트만 실제로 들어간다.</summary>
    public Func<string, bool>? IncludeFileContent { get; init; }
}

/// <summary>날짜처럼 보이는 이름 조각 → 변수 제안.</summary>
public sealed record DateSuggestion(string RelativePath, string Original, string Replacement);

public sealed class TemplateCaptureResult
{
    public required FolderTemplate Template { get; init; }

    public List<DateSuggestion> DateSuggestions { get; } = [];

    /// <summary>MaxItems·MaxDepth에 걸려 잘렸는지.</summary>
    public bool Truncated { get; set; }

    public List<string> SkippedFiles { get; } = [];
}

/// <summary>"템플릿으로 저장": 기존 폴더 구조를 템플릿으로 (설계서 5.10 내 템플릿 만들기).</summary>
public static partial class TemplateCapture
{
    [GeneratedRegex(@"(?<!\d)(\d{4})-(\d{2})-(\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<!\d)(\d{4})(\d{2})(\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CompactDate();

    public static TemplateCaptureResult Capture(string folder, string name, TemplateCaptureOptions? options = null)
    {
        options ??= new TemplateCaptureOptions();
        var root = new DirectoryInfo(folder);
        var t = new FolderTemplate
        {
            Id = MakeId(name),
            Name = name,
            Description = $"{root.Name} 폴더 구조에서 만든 템플릿",
        };
        var result = new TemplateCaptureResult { Template = t };
        var count = 0;
        Walk(root, "", 1, t.Tree, options, result, ref count);

        // 폴더 설명이 있으면 사용법으로 가져온다
        var descPath = Path.Combine(folder, FolderDescription.FileName);
        if (File.Exists(descPath))
        {
            var d = FolderDescriptionParser.Parse(File.ReadAllText(descPath));
            t.Guide = d.Body;
        }

        CollectDates(t.Tree, "", result.DateSuggestions);
        return result;
    }

    private static void Walk(DirectoryInfo dir, string rel, int depth, IList<TemplateNode> output, TemplateCaptureOptions o, TemplateCaptureResult result, ref int count)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = dir.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var e in entries.OrderBy(x => x is FileInfo).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            // 숨김·시스템·링크는 따라가지 않는다
            if ((e.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
            {
                continue;
            }

            if (count >= o.MaxItems)
            {
                result.Truncated = true;
                return;
            }

            var childRel = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
            if (e is DirectoryInfo d)
            {
                count++;
                var node = new TemplateNode { Name = e.Name };
                output.Add(node);
                if (depth < o.MaxDepth)
                {
                    Walk(d, childRel, depth + 1, node.Children, o, result, ref count);
                }
                else if (HasSubItems(d))
                {
                    result.Truncated = true;
                }
            }
            else if (e is FileInfo f && o.IncludeFileContent?.Invoke(childRel) == true)
            {
                if (f.Length > TemplateValidator.MaxFileBytes || TemplateValidator.IsForbiddenFileName(f.Name))
                {
                    result.SkippedFiles.Add(childRel);
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(f.FullName, Encoding.UTF8);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.SkippedFiles.Add(childRel);
                    continue;
                }

                if (!TemplateValidator.IsText(text) || text.Contains('�', StringComparison.Ordinal))
                {
                    result.SkippedFiles.Add(childRel);
                    continue;
                }

                count++;
                output.Add(new TemplateNode { Name = e.Name, File = text });
            }
        }
    }

    private static bool HasSubItems(DirectoryInfo d)
    {
        try
        {
            return d.EnumerateFileSystemInfos().Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CollectDates(IList<TemplateNode> nodes, string rel, List<DateSuggestion> output)
    {
        foreach (var n in nodes)
        {
            var path = rel.Length == 0 ? n.Name : rel + "/" + n.Name;
            var replaced = SuggestDateVariables(n.Name);
            if (!string.Equals(replaced, n.Name, StringComparison.Ordinal))
            {
                output.Add(new DateSuggestion(path, n.Name, replaced));
            }

            CollectDates(n.Children, path, output);
        }
    }

    /// <summary>2026-11-18 → {{date:yyyy-MM-dd}}, 20261118 → {{date:yyyyMMdd}} (실제 날짜일 때만).</summary>
    public static string SuggestDateVariables(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var s = IsoDate().Replace(name, m => IsDate(m) ? "{{date:yyyy-MM-dd}}" : m.Value);
        return CompactDate().Replace(s, m => IsDate(m) ? "{{date:yyyyMMdd}}" : m.Value);
    }

    private static bool IsDate(Match m) =>
        DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        && d.Year is >= 1900 and <= 2200;

    /// <summary>사용자가 고른 제안만 적용한다 (상대 경로 기준).</summary>
    public static void ApplyDateSuggestions(FolderTemplate template, IEnumerable<DateSuggestion> accepted)
    {
        ArgumentNullException.ThrowIfNull(template);
        var map = accepted.ToDictionary(a => a.RelativePath, a => a.Replacement, StringComparer.Ordinal);
        void Walk(IList<TemplateNode> nodes, string rel)
        {
            foreach (var n in nodes)
            {
                var path = rel.Length == 0 ? n.Name : rel + "/" + n.Name;
                if (map.TryGetValue(path, out var r))
                {
                    n.Name = r;
                }

                Walk(n.Children, path);
            }
        }

        Walk(template.Tree, "");
    }

    public static string MakeId(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var id = sb.ToString().Trim('-');
        return id.Length == 0 ? "my-template" : id;
    }
}
