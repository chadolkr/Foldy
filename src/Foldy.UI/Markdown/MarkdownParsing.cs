using System.Text;
using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Foldy.UI.Markdown;

/// <summary>Markdig 파이프라인 (GFM: 표, 체크박스, 취소선, 자동 링크, 각주). 파이프라인은 스레드 안전.</summary>
public static class MarkdownParsing
{
    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseFootnotes()
        .Build();

    /// <summary>파싱. UI 스레드가 아닌 곳(Task.Run)에서 부른다.</summary>
    public static MarkdownDocument Parse(string text) => Markdig.Markdown.Parse(text, Pipeline);

    /// <summary>인라인을 꾸밈 없는 글자로 (제목 목차, 앵커, 대체 텍스트용).</summary>
    public static string PlainText(Inline? inline)
    {
        var sb = new StringBuilder();
        AppendPlain(sb, inline);
        return sb.ToString().Trim();
    }

    private static void AppendPlain(StringBuilder sb, Inline? inline)
    {
        switch (inline)
        {
            case null:
                return;
            case LiteralInline lit:
                sb.Append(lit.Content.AsSpan());
                break;
            case CodeInline code:
                sb.Append(code.Content);
                break;
            case LineBreakInline:
                sb.Append(' ');
                break;
            case HtmlEntityInline ent:
                sb.Append(ent.Transcoded.AsSpan());
                break;
            case AutolinkInline auto:
                sb.Append(auto.Url);
                break;
            case TaskList:
            case FootnoteLink:
            case HtmlInline:
                break;
            case ContainerInline container:
                for (var c = container.FirstChild; c is not null; c = c.NextSibling)
                {
                    AppendPlain(sb, c);
                }

                break;
        }
    }
}

/// <summary>목차 항목. Anchor는 GitHub 방식 슬러그(#링크 대상), Line은 0부터 센 원문 줄.</summary>
public sealed record MarkdownHeading(int Level, string Text, string Anchor, int Line)
{
    internal HeadingBlock? Source { get; init; }

    /// <summary>목차 들여쓰기 (단계당 12px).</summary>
    public double Indent => (Level - 1) * 12;
}

/// <summary>제목 목록(목차) 추출과 앵커 슬러그 생성.</summary>
public static class MarkdownOutline
{
    public static IReadOnlyList<MarkdownHeading> Extract(MarkdownDocument document)
    {
        var slugger = new HeadingSlugger();
        var list = new List<MarkdownHeading>();
        foreach (var h in document.Descendants<HeadingBlock>())
        {
            var text = MarkdownParsing.PlainText(h.Inline);
            list.Add(new MarkdownHeading(h.Level, text, slugger.Next(text), h.Line) { Source = h });
        }

        return list;
    }
}

/// <summary>
/// GitHub 방식 앵커: 소문자, 글자·숫자·'-'·'_'만 남기고 공백은 '-'. 같은 이름이 또 나오면 -1, -2를 붙인다.
/// 한글은 글자로 취급해 그대로 남는다.
/// </summary>
public sealed class HeadingSlugger
{
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

    public static string Slugify(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                sb.Append(ch);
            }
            else if (ch == ' ')
            {
                sb.Append('-');
            }
        }

        return sb.ToString();
    }

    public string Next(string text)
    {
        var slug = Slugify(text);
        if (_seen.TryGetValue(slug, out var n))
        {
            _seen[slug] = n + 1;
            var candidate = $"{slug}-{n + 1}";
            _seen.TryAdd(candidate, 0);
            return candidate;
        }

        _seen[slug] = 0;
        return slug;
    }
}
