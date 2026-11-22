using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace Foldy.UI.Markdown;

/// <summary>렌더 설정.</summary>
public sealed class MarkdownRenderOptions
{
    /// <summary>상대 경로(이미지·링크)의 기준 폴더 = 문서가 있는 폴더.</summary>
    public string BaseFolder { get; init; } = "";

    /// <summary>원격(http) 이미지를 불러올지. 기본은 차단 → "이미지 불러오기" 버튼으로 허용.</summary>
    public bool AllowRemoteImages { get; init; }

    /// <summary>링크를 눌렀을 때 (원래 URL 문자열 그대로). 분류는 MarkdownLinks.Classify.</summary>
    public Action<string>? LinkClicked { get; init; }

    /// <summary>Mermaid 렌더러. null이면 mermaid 블록은 코드로만 보인다.</summary>
    public IMermaidRenderer? Mermaid { get; init; }

    /// <summary>다크 색 벌 사용 여부 (보통 ThemeManager.IsDark).</summary>
    public bool Dark { get; init; }

    /// <summary>테스트용: 이미지를 같은 스레드에서 바로 읽는다.</summary>
    public bool LoadImagesSynchronously { get; init; }

    /// <summary>메모리에 있는 그림 (잠긴 메모: images/그림-1.png). 값이 있으면 디스크를 보지 않는다.</summary>
    public Func<string, byte[]?>? InMemoryImage { get; init; }
}

/// <summary>
/// Markdig AST → WPF FlowDocument (설계서 5.9). 웹뷰 없이 FlowDocument만 쓰며, 색·글꼴은 모두
/// DynamicResource(Brush.*, Font.*, Md.*) 참조라 테마를 바꾸면 다시 그리지 않아도 바로 바뀐다.
/// 원문 HTML은 실행하지 않고 글자로만 보여 준다. UI 스레드에서 만든다 (파싱은 MarkdownParsing.Parse를 백그라운드에서).
/// </summary>
public sealed class MarkdownFlowRenderer
{
    public const double BaseFontSize = 14;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly double[] HeadingSizes = [26, 21, 18, 16, 14, 13];
    private const long MaxImageBytes = 32L * 1024 * 1024;

    private readonly MarkdownRenderOptions _options;
    private readonly Dictionary<HeadingBlock, MarkdownHeading> _headings = [];
    private readonly Dictionary<string, TextElement> _anchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MermaidBlock> _mermaid = [];

    public MarkdownFlowRenderer(MarkdownRenderOptions options, IReadOnlyList<MarkdownHeading> headings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(headings);
        _options = options;
        foreach (var h in headings)
        {
            if (h.Source is not null)
            {
                _headings[h.Source] = h;
            }
        }
    }

    /// <summary>원격 이미지를 하나라도 막았는지 → 뷰가 "이미지 불러오기" 띠를 보인다.</summary>
    public bool HasBlockedImages { get; private set; }

    /// <summary>앵커 → 요소 (제목 슬러그, 각주 "fn-1", 각주 참조 "fnref-1").</summary>
    public IReadOnlyDictionary<string, TextElement> Anchors => _anchors;

    public IReadOnlyList<MermaidBlock> MermaidBlocks => _mermaid;

    /// <summary>테마 토큰을 건 빈 문서.</summary>
    public static FlowDocument CreateDocument(bool dark)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(28, 18, 28, 28),
            FontSize = BaseFontSize,
            LineHeight = double.NaN,
            TextAlignment = TextAlignment.Left,
            IsHyphenationEnabled = false,
        };
        MarkdownPalette.Apply(doc.Resources, dark);
        doc.SetResourceReference(FlowDocument.BackgroundProperty, "Brush.Window");
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "Brush.Text");
        doc.SetResourceReference(FlowDocument.FontFamilyProperty, "Font.UI");
        return doc;
    }

    /// <summary>문서 전체를 한 번에 그린다 (작은 파일, 테스트).</summary>
    public FlowDocument Render(MarkdownDocument document)
    {
        var doc = CreateDocument(_options.Dark);
        foreach (var b in RenderBlocks(document))
        {
            doc.Blocks.Add(b);
        }

        return doc;
    }

    /// <summary>최상위 블록을 하나씩 만든다. 큰 문서는 뷰가 몇 개씩 나눠 이어 붙인다.</summary>
    public IEnumerable<WpfBlock> RenderBlocks(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var block in document)
        {
            var wpf = RenderBlock(block, tight: false);
            if (wpf is not null)
            {
                yield return wpf;
            }
        }
    }

    // ───────────────────────────── 블록 ─────────────────────────────

    private WpfBlock? RenderBlock(Markdig.Syntax.Block block, bool tight) => block switch
    {
        HeadingBlock h => RenderHeading(h),
        ParagraphBlock p => RenderParagraph(p, tight),
        QuoteBlock q => RenderQuote(q),
        ListBlock l => RenderList(l),
        FencedCodeBlock f => RenderCode(f, f.Info),
        CodeBlock c => RenderCode(c, null),
        ThematicBreakBlock => RenderRule(),
        Markdig.Extensions.Tables.Table t => RenderTable(t),
        HtmlBlock html => RenderRawHtml(html),
        FootnoteGroup fg => RenderFootnotes(fg),
        LinkReferenceDefinitionGroup or LinkReferenceDefinition or BlankLineBlock => null,
        ContainerBlock container => RenderContainer(container, tight),
        LeafBlock leaf => leaf.Inline is not null ? RenderInlineParagraph(leaf.Inline, tight) : PlainParagraph(LinesText(leaf)),
        _ => null,
    };

    private Section RenderContainer(ContainerBlock container, bool tight)
    {
        var section = new Section();
        foreach (var child in container)
        {
            var b = RenderBlock(child, tight);
            if (b is not null)
            {
                section.Blocks.Add(b);
            }
        }

        return section;
    }

    private Paragraph RenderHeading(HeadingBlock h)
    {
        var p = new Paragraph
        {
            FontSize = HeadingSizes[Math.Clamp(h.Level, 1, 6) - 1],
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, h.Level <= 2 ? 18 : 14, 0, 8),
        };
        if (h.Level <= 2)
        {
            p.BorderThickness = new Thickness(0, 0, 0, 1);
            p.Padding = new Thickness(0, 0, 0, 4);
            p.SetResourceReference(WpfBlock.BorderBrushProperty, "Brush.Border");
        }

        AddInlines(p.Inlines, h.Inline);
        if (_headings.TryGetValue(h, out var info))
        {
            p.Tag = info;
            _anchors.TryAdd(info.Anchor, p);
        }

        return p;
    }

    private Paragraph RenderParagraph(ParagraphBlock block, bool tight) =>
        block.Inline is null ? new Paragraph() : RenderInlineParagraph(block.Inline, tight);

    private Paragraph RenderInlineParagraph(ContainerInline inline, bool tight)
    {
        var p = new Paragraph { Margin = tight ? new Thickness(0) : new Thickness(0, 0, 0, 10) };
        AddInlines(p.Inlines, inline);
        return p;
    }

    private Section RenderQuote(QuoteBlock q)
    {
        var s = RenderContainer(q, tight: false);
        s.BorderThickness = new Thickness(3, 0, 0, 0);
        s.Padding = new Thickness(12, 2, 0, 2);
        s.Margin = new Thickness(0, 0, 0, 10);
        s.SetResourceReference(WpfBlock.BorderBrushProperty, "Brush.BorderStrong");
        s.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextSecondary");
        if (s.Blocks.LastBlock is { } last)
        {
            last.Margin = new Thickness(0);
        }

        return s;
    }

    private List RenderList(ListBlock l)
    {
        var tight = !l.IsLoose;
        var list = new List
        {
            MarkerStyle = l.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, tight ? 10 : 4),
            Padding = new Thickness(24, 0, 0, 0),
        };
        if (l.IsOrdered && int.TryParse(l.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) && start > 0)
        {
            list.StartIndex = start;
        }

        var allTasks = true;
        foreach (var child in l)
        {
            if (child is not ListItemBlock itemBlock)
            {
                continue;
            }

            var item = new ListItem();
            foreach (var b in itemBlock)
            {
                var wpf = RenderBlock(b, tight);
                if (wpf is not null)
                {
                    item.Blocks.Add(wpf);
                }
            }

            if (!IsTaskItem(itemBlock))
            {
                allTasks = false;
            }

            if (!tight)
            {
                item.Margin = new Thickness(0, 0, 0, 4);
            }

            list.ListItems.Add(item);
        }

        if (allTasks && list.ListItems.Count > 0)
        {
            // 체크박스 목록은 글머리표 대신 ☐/☑만
            list.MarkerStyle = TextMarkerStyle.None;
            list.Padding = new Thickness(6, 0, 0, 0);
        }

        return list;
    }

    private static bool IsTaskItem(ListItemBlock item) =>
        item.Count > 0 && item[0] is ParagraphBlock { Inline.FirstChild: TaskList };

    private Section RenderCode(LeafBlock block, string? info)
    {
        var language = info?.Trim().Split(' ', 2)[0];
        if (string.IsNullOrEmpty(language))
        {
            language = null;
        }

        var code = LinesText(block);
        var section = new Section { Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(1) };
        section.SetResourceReference(WpfBlock.BackgroundProperty, MarkdownPalette.CodeBackground);
        section.SetResourceReference(WpfBlock.BorderBrushProperty, "Brush.Border");

        // 헤더: 언어 이름 + 복사 버튼 (+ mermaid면 다이어그램 보기)
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 4, 4, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        var copy = SmallButton(Foldy.Core.Localization.Loc.T("ops.copy"));
        copy.ToolTip = Foldy.Core.Localization.Loc.T("md.copyCode");
        copy.Click += (_, _) => CopyToClipboard(copy, code);
        buttons.Children.Add(copy);
        header.Children.Add(buttons);
        var label = new TextBlock { Text = language ?? "", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        header.Children.Add(label);
        section.Blocks.Add(new BlockUIContainer(header) { Margin = new Thickness(0) });

        var p = new Paragraph { Margin = new Thickness(10, 2, 10, 8), FontSize = 13 };
        p.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
        var isMermaid = string.Equals(language, "mermaid", StringComparison.OrdinalIgnoreCase);
        foreach (var token in CodeHighlighter.Tokenize(code, isMermaid ? null : language))
        {
            var run = new Run(token.Text);
            if (token.Kind != CodeTokenKind.Text)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, MarkdownPalette.KeyFor(token.Kind));
            }

            p.Inlines.Add(run);
        }

        section.Blocks.Add(p);

        if (isMermaid && _options.Mermaid is { } mermaid)
        {
            var toggle = SmallButton(Foldy.Core.Localization.Loc.T("md.viewDiagram"));
            buttons.Children.Insert(0, toggle);
            var mb = new MermaidBlock(code, section, p, toggle);
            toggle.Click += async (_, _) => await mb.ToggleAsync(mermaid, _options.Dark);
            _mermaid.Add(mb);
        }

        return section;
    }

    private static BlockUIContainer RenderRule()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
        line.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
        return new BlockUIContainer(line);
    }

    private WpfTable RenderTable(Markdig.Extensions.Tables.Table t)
    {
        var table = new WpfTable { CellSpacing = 0, BorderThickness = new Thickness(1, 1, 0, 0), Margin = new Thickness(0, 0, 0, 12) };
        table.SetResourceReference(WpfBlock.BorderBrushProperty, "Brush.Border");
        var columns = Math.Max(t.ColumnDefinitions.Count, t.OfType<Markdig.Extensions.Tables.TableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max());
        for (var i = 0; i < columns; i++)
        {
            table.Columns.Add(new TableColumn { Width = GridLength.Auto });
        }

        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        foreach (var rowBlock in t.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var row = new WpfTableRow();
            if (rowBlock.IsHeader)
            {
                row.FontWeight = FontWeights.SemiBold;
                row.SetResourceReference(TextElement.BackgroundProperty, MarkdownPalette.CodeBackground);
            }

            for (var i = 0; i < rowBlock.Count; i++)
            {
                if (rowBlock[i] is not Markdig.Extensions.Tables.TableCell cellBlock)
                {
                    continue;
                }

                var cell = new WpfTableCell
                {
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4, 8, 4),
                    ColumnSpan = Math.Max(1, cellBlock.ColumnSpan),
                };
                cell.SetResourceReference(WpfTableCell.BorderBrushProperty, "Brush.Border");
                var colIndex = cellBlock.ColumnIndex >= 0 ? cellBlock.ColumnIndex : i;
                var align = colIndex < t.ColumnDefinitions.Count ? t.ColumnDefinitions[colIndex].Alignment : null;
                foreach (var b in cellBlock)
                {
                    var wpf = RenderBlock(b, tight: true);
                    if (wpf is null)
                    {
                        continue;
                    }

                    if (wpf is Paragraph para)
                    {
                        para.TextAlignment = align switch
                        {
                            TableColumnAlign.Center => TextAlignment.Center,
                            TableColumnAlign.Right => TextAlignment.Right,
                            _ => TextAlignment.Left,
                        };
                    }

                    cell.Blocks.Add(wpf);
                }

                if (cell.Blocks.Count == 0)
                {
                    cell.Blocks.Add(new Paragraph());
                }

                row.Cells.Add(cell);
            }

            group.Rows.Add(row);
        }

        return table;
    }

    /// <summary>원문 HTML 블록: 실행·해석하지 않고 고정폭 글자로.</summary>
    private static Paragraph RenderRawHtml(HtmlBlock html)
    {
        var p = PlainParagraph(LinesText(html));
        p.FontSize = 12.5;
        p.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
        p.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextSecondary");
        return p;
    }

    private Section RenderFootnotes(FootnoteGroup group)
    {
        var section = new Section { FontSize = 12.5, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0), Margin = new Thickness(0, 16, 0, 0) };
        section.SetResourceReference(WpfBlock.BorderBrushProperty, "Brush.Border");
        section.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextSecondary");
        foreach (var fn in group.OfType<Footnote>())
        {
            var body = RenderContainer(fn, tight: true);
            var first = body.Blocks.FirstBlock as Paragraph;
            if (first is null)
            {
                first = new Paragraph();
                body.Blocks.InsertBefore(body.Blocks.FirstBlock, first);
            }

            first.Inlines.InsertBefore(first.Inlines.FirstInline, new Run(fn.Order.ToString(CultureInfo.InvariantCulture) + ". "));
            body.Margin = new Thickness(0, 0, 0, 4);
            _anchors.TryAdd("fn-" + fn.Order.ToString(CultureInfo.InvariantCulture), body);
            section.Blocks.Add(body);
        }

        return section;
    }

    // ───────────────────────────── 인라인 ─────────────────────────────

    private void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null)
        {
            return;
        }

        for (var c = container.FirstChild; c is not null; c = c.NextSibling)
        {
            var wpf = RenderInline(c);
            if (wpf is not null)
            {
                target.Add(wpf);
            }
        }
    }

    private WpfInline? RenderInline(Markdig.Syntax.Inlines.Inline inline)
    {
        switch (inline)
        {
            case LiteralInline lit:
                return new Run(lit.Content.ToString());
            case CodeInline code:
            {
                var run = new Run(code.Content) { FontSize = BaseFontSize - 1 };
                run.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
                run.SetResourceReference(TextElement.BackgroundProperty, MarkdownPalette.CodeBackground);
                return run;
            }

            case LineBreakInline lb:
                return lb.IsHard ? new LineBreak() : new Run(" ");
            case HtmlEntityInline ent:
                return new Run(ent.Transcoded.ToString());
            case HtmlInline html:
                return RenderInlineHtml(html.Tag);
            case TaskList task:
                return new Run(task.Checked ? "☑" : "☐");
            case FootnoteLink fl:
                return RenderFootnoteLink(fl);
            case AutolinkInline auto:
            {
                var url = auto.IsEmail && !auto.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? "mailto:" + auto.Url : auto.Url;
                return MakeLink(url, [new Run(auto.Url)]);
            }

            case LinkInline { IsImage: true } img:
                return RenderImage(img);
            case LinkInline link:
            {
                var span = new Span();
                AddInlines(span.Inlines, link);
                return MakeLink(link.GetDynamicUrl?.Invoke() ?? link.Url ?? "", [.. span.Inlines], link.Title);
            }

            case EmphasisInline em:
                return RenderEmphasis(em);
            case ContainerInline other:
            {
                var span = new Span();
                AddInlines(span.Inlines, other);
                return span;
            }

            default:
                return null;
        }
    }

    private Span RenderEmphasis(EmphasisInline em)
    {
        var span = new Span();
        AddInlines(span.Inlines, em);
        switch (em.DelimiterChar)
        {
            case '*' or '_':
                if (em.DelimiterCount >= 2)
                {
                    span.FontWeight = FontWeights.Bold;
                }

                if (em.DelimiterCount is 1 or 3)
                {
                    span.FontStyle = FontStyles.Italic;
                }

                break;
            case '~':
                if (em.DelimiterCount >= 2)
                {
                    span.TextDecorations = TextDecorations.Strikethrough;
                }
                else
                {
                    span.BaselineAlignment = BaselineAlignment.Subscript;
                    span.FontSize = BaseFontSize * 0.8;
                }

                break;
            case '^':
                span.BaselineAlignment = BaselineAlignment.Superscript;
                span.FontSize = BaseFontSize * 0.8;
                break;
            case '+':
                span.TextDecorations = TextDecorations.Underline;
                break;
            case '=':
                span.SetResourceReference(TextElement.BackgroundProperty, MarkdownPalette.Mark);
                break;
        }

        return span;
    }

    /// <summary>인라인 HTML: &lt;br&gt;만 줄바꿈으로, 나머지는 태그 글자 그대로 (실행하지 않음).</summary>
    private static WpfInline RenderInlineHtml(string tag)
    {
        var t = tag.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        if (t is "<br>" or "<br/>")
        {
            return new LineBreak();
        }

        var run = new Run(tag);
        run.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextSecondary");
        return run;
    }

    private Hyperlink RenderFootnoteLink(FootnoteLink fl)
    {
        var order = fl.Footnote.Order.ToString(CultureInfo.InvariantCulture);
        if (fl.IsBackLink)
        {
            return MakeLink("#fnref-" + order, [new Run(" ↩")]);
        }

        var link = MakeLink("#fn-" + order, [new Run(order)]);
        link.BaselineAlignment = BaselineAlignment.Superscript;
        link.FontSize = BaseFontSize * 0.75;
        _anchors.TryAdd("fnref-" + order, link);
        return link;
    }

    private Hyperlink MakeLink(string url, WpfInline[] content, string? title = null)
    {
        var link = new Hyperlink { Tag = url, ToolTip = string.IsNullOrEmpty(title) ? url : title + "\n" + url };
        foreach (var c in content)
        {
            link.Inlines.Add(c);
        }

        link.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent");
        link.Click += (_, e) =>
        {
            e.Handled = true;
            _options.LinkClicked?.Invoke(url);
        };
        return link;
    }

    // ───────────────────────────── 이미지 ─────────────────────────────

    private WpfInline RenderImage(LinkInline img)
    {
        var alt = MarkdownParsing.PlainText(img);
        var url = (img.Url ?? "").Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!_options.AllowRemoteImages)
            {
                HasBlockedImages = true;
                return Placeholder("🖼 " + (alt.Length > 0 ? alt : Foldy.Core.Localization.Loc.T("md.remoteImage")) + Foldy.Core.Localization.Loc.T("md.blocked"), url);
            }

            return ImageHost(url, alt, ct => DownloadAsync(url, ct));
        }

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0 && url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) && !url[..comma].Contains("svg", StringComparison.OrdinalIgnoreCase))
            {
                var data = url[(comma + 1)..];
                return ImageHost(url, alt, _ => Task.FromResult(Convert.FromBase64String(data)));
            }

            return Placeholder("🖼 " + alt, Foldy.Core.Localization.Loc.T("md.unsupportedImage"));
        }

        if (_options.InMemoryImage?.Invoke(url) is { } mem)
        {
            return ImageHost(url, alt, _ => Task.FromResult(mem));
        }

        var target = MarkdownLinks.Classify(url, _options.BaseFolder, _ => false);
        if (target.Kind is not (MarkdownLinkKind.File or MarkdownLinkKind.Markdown))
        {
            return Placeholder("🖼 " + alt, url);
        }

        var path = target.Path;
        if (MarkdownLinks.IsNetworkPath(path))
        {
            // 문서를 열기만 해도 그림을 받으러 다른 컴퓨터에 접속하면 로그인 정보(NTLM)가 그쪽으로 간다
            return Placeholder("🖼 " + (alt.Length > 0 ? alt : Foldy.Core.Localization.Loc.T("md.networkImage")), Foldy.Core.Localization.Loc.T("md.networkImageBlocked") + path);
        }

        if (string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            return Placeholder("🖼 " + (alt.Length > 0 ? alt : Path.GetFileName(path)) + " (SVG)", path);
        }

        if (!File.Exists(path))
        {
            return Placeholder("🖼 " + (alt.Length > 0 ? alt : Path.GetFileName(path)) + Foldy.Core.Localization.Loc.T("md.missingFile"), path);
        }

        return ImageHost(path, alt, ct => File.ReadAllBytesAsync(path, ct));
    }

    private static Run Placeholder(string text, string tooltip)
    {
        var run = new Run("[" + text + "]") { ToolTip = tooltip };
        run.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextSecondary");
        return run;
    }

    /// <summary>자리를 먼저 만들고 바이트는 백그라운드에서 읽어 디코드한 뒤 채운다.</summary>
    private InlineUIContainer ImageHost(string source, string alt, Func<CancellationToken, Task<byte[]>> load)
    {
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            ToolTip = alt.Length > 0 ? alt + "\n" + source : source,
        };
        var host = new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom };

        if (_options.LoadImagesSynchronously)
        {
            try
            {
                SetBitmap(image, Decode(load(CancellationToken.None).GetAwaiter().GetResult()));
            }
            catch (Exception e) when (IsImageError(e))
            {
                image.ToolTip = Foldy.Core.Localization.Loc.T("md.imageFailed") + source;
            }

            return host;
        }

        var dispatcher = image.Dispatcher;
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await load(CancellationToken.None).ConfigureAwait(false);
                var bmp = Decode(bytes);
                await dispatcher.InvokeAsync(() => SetBitmap(image, bmp));
            }
            catch (Exception e) when (IsImageError(e))
            {
                await dispatcher.InvokeAsync(() => image.ToolTip = Foldy.Core.Localization.Loc.T("md.imageFailed") + source);
            }
        });
        return host;
    }

    private static bool IsImageError(Exception e) =>
        e is IOException or UnauthorizedAccessException or NotSupportedException or FormatException
            or HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException;

    private static void SetBitmap(Image image, BitmapSource bmp)
    {
        image.Source = bmp;
        // 원래 크기보다 키우지 않는다 (96dpi 기준 픽셀 크기)
        image.MaxWidth = bmp.PixelWidth;
    }

    private static BitmapImage Decode(byte[] bytes)
    {
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(bytes, writable: false);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxImageBytes)
        {
            throw new NotSupportedException(Foldy.Core.Localization.Loc.T("md.imageTooBig"));
        }

        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = MaxImageBytes };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Foldy-MarkdownViewer");
        return http;
    }

    // ───────────────────────────── 기타 ─────────────────────────────

    private static Button SmallButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(8, 1, 8, 1),
        Margin = new Thickness(4, 0, 0, 0),
        FontSize = 11,
        Focusable = false,
        Cursor = System.Windows.Input.Cursors.Arrow,
    };

    private static void CopyToClipboard(Button button, string text)
    {
        try
        {
            Clipboard.SetText(text);
            button.Content = Foldy.Core.Localization.Loc.T("md.copied");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 다른 프로그램이 클립보드를 잡고 있음
            button.Content = Foldy.Core.Localization.Loc.T("md.copyFailed");
        }
    }

    private static Paragraph PlainParagraph(string text) => new(new Run(text)) { Margin = new Thickness(0, 0, 0, 10) };

    private static string LinesText(LeafBlock block)
    {
        var lines = block.Lines;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append(lines.Lines[i].Slice.AsSpan());
        }

        return sb.ToString();
    }
}
