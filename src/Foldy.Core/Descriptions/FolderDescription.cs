using System.Text;

namespace Foldy.Core.Descriptions;

/// <summary>
/// 폴더 설명 (설계서 5.13, <c>.folder_description.md</c>). UTF-8 Markdown, 맨 위에 선택적 YAML 머리말
/// (title, tags, template). 모르는 머리말 항목은 <see cref="ExtraFrontMatter"/>에 그대로 보존한다.
/// </summary>
public sealed class FolderDescription
{
    public const string FileName = ".folder_description.md";

    public string? Title { get; set; }

    public IList<string> Tags { get; set; } = [];

    /// <summary>폴더 템플릿으로 만든 경우 템플릿 id (경로바 배지·파일명 규칙 안내).</summary>
    public string? Template { get; set; }

    public string Body { get; set; } = "";

    /// <summary>모르는 머리말 줄 (원문 그대로).</summary>
    public IList<string> ExtraFrontMatter { get; set; } = [];

    /// <summary>설명 띠·툴팁·"설명" 컬럼에 쓰는 한 줄: 제목, 없으면 본문 첫 줄(# 제거).</summary>
    public string Headline
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title))
            {
                return Title!.Trim();
            }

            foreach (var line in Body.Split('\n'))
            {
                var t = line.Trim().TrimStart('#').Trim();
                if (t.Length > 0)
                {
                    return t;
                }
            }

            return "";
        }
    }

    /// <summary>접힌 설명 띠의 첫 문단 (제목 줄 제외, 최대 <paramref name="maxChars"/>자).</summary>
    public string FirstParagraph(int maxChars = 200)
    {
        var sb = new StringBuilder();
        foreach (var raw in Body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (t.StartsWith('#'))
            {
                if (sb.Length > 0)
                {
                    break;
                }

                continue;
            }

            if (t.Length == 0)
            {
                if (sb.Length > 0)
                {
                    break;
                }

                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(t);
            if (sb.Length >= maxChars)
            {
                break;
            }
        }

        return sb.Length > maxChars ? sb.ToString(0, maxChars - 1) + "…" : sb.ToString();
    }
}

/// <summary>머리말 파서. 간단한 YAML 부분집합만 다룬다 (key: value, [a, b] 목록, "- a" 목록).</summary>
public static class FolderDescriptionParser
{
    public static FolderDescription Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var d = new FolderDescription();
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 2 || lines[0].Trim() != "---")
        {
            d.Body = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            return d;
        }

        var end = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() is "---" or "...")
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            d.Body = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            return d;
        }

        string? listKey = null;
        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (listKey is not null && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                if (listKey == "tags")
                {
                    d.Tags.Add(Unquote(StripComment(trimmed[2..])));
                }
                else
                {
                    d.ExtraFrontMatter.Add(line);
                }

                continue;
            }

            listKey = null;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
            {
                d.ExtraFrontMatter.Add(line);
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = StripComment(line[(colon + 1)..]).Trim();
            switch (key)
            {
                case "title":
                    d.Title = Unquote(value);
                    break;
                case "template":
                    d.Template = Unquote(value);
                    break;
                case "tags":
                    if (value.Length == 0)
                    {
                        listKey = "tags";
                    }
                    else
                    {
                        foreach (var t in value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            d.Tags.Add(Unquote(t));
                        }
                    }

                    break;
                default:
                    d.ExtraFrontMatter.Add(line);
                    if (value.Length == 0)
                    {
                        listKey = key;
                    }

                    break;
            }
        }

        d.Body = string.Join('\n', lines.Skip(end + 1)).TrimStart('\n');
        return d;
    }

    public static string Serialize(FolderDescription d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var sb = new StringBuilder();
        var hasFront = !string.IsNullOrWhiteSpace(d.Title) || d.Tags.Count > 0 || !string.IsNullOrWhiteSpace(d.Template) || d.ExtraFrontMatter.Count > 0;
        if (hasFront)
        {
            sb.Append("---\n");
            if (!string.IsNullOrWhiteSpace(d.Title))
            {
                sb.Append("title: ").Append(Quote(d.Title!.Trim())).Append('\n');
            }

            if (d.Tags.Count > 0)
            {
                sb.Append("tags: [").Append(string.Join(", ", d.Tags.Where(t => t.Length > 0).Select(Quote))).Append("]\n");
            }

            if (!string.IsNullOrWhiteSpace(d.Template))
            {
                sb.Append("template: ").Append(Quote(d.Template!.Trim())).Append('\n');
            }

            foreach (var l in d.ExtraFrontMatter)
            {
                sb.Append(l).Append('\n');
            }

            sb.Append("---\n");
        }

        sb.Append(d.Body.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (sb.Length > 0 && sb[^1] != '\n')
        {
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>"a, b #c" 형식의 태그 입력칸 → 목록.</summary>
    public static IList<string> ParseTags(string? input) =>
        string.IsNullOrWhiteSpace(input)
            ? []
            : input.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.TrimStart('#')).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string StripComment(string v)
    {
        // 따옴표 밖의 " #"부터는 주석
        var inQuote = false;
        for (var i = 0; i < v.Length; i++)
        {
            if (v[i] is '"' or '\'')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && v[i] == '#' && i > 0 && char.IsWhiteSpace(v[i - 1]))
            {
                return v[..i].TrimEnd();
            }
        }

        return v;
    }

    private static string Unquote(string v)
    {
        v = v.Trim();
        return v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')) ? v[1..^1] : v;
    }

    private static string Quote(string v) =>
        v.IndexOfAny([':', '#', ',', '[', ']', '{', '}', '"', '\'']) >= 0 || v != v.Trim()
            ? "\"" + v.Replace("\"", "'", StringComparison.Ordinal) + "\""
            : v;
}
