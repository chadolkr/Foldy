using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Foldy.UI.Templates;

/// <summary>
/// 사용법 안내문용 가벼운 Markdown 표시 (제목, 인용, 목록, 표, 코드 블록, **굵게**, `코드`).
/// HTML은 실행하지 않고 글자로 보여준다. 내장 Markdown 뷰어(5.9)가 들어오면 그쪽으로 바꿔 끼울 수 있다.
/// 사용: &lt;FlowDocumentScrollViewer t:SimpleMarkdown.Text="{Binding Guide}" /&gt;
/// </summary>
public static partial class SimpleMarkdown
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(SimpleMarkdown), new PropertyMetadata(null, OnTextChanged));

    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineToken();

    [GeneratedRegex(@"^\d+\.\s+", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedItem();

    public static string? GetText(DependencyObject d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return (string?)d.GetValue(TextProperty);
    }

    public static void SetText(DependencyObject d, string? value)
    {
        ArgumentNullException.ThrowIfNull(d);
        d.SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FlowDocumentScrollViewer viewer)
        {
            viewer.Document = Render(e.NewValue as string ?? "");
        }
    }

    public static FlowDocument Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var doc = new FlowDocument { PagePadding = new Thickness(4, 2, 12, 12), TextAlignment = TextAlignment.Left };
        doc.SetResourceReference(FlowDocument.FontFamilyProperty, "Font.UI");
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "Brush.Text");
        doc.SetResourceReference(FlowDocument.BackgroundProperty, "Brush.SurfaceAlt");
        doc.FontSize = 13;

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List? list = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.Trim();
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                list = null;
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i]);
                }

                var p = new Paragraph(new Run(string.Join("\n", code))) { Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 4, 0, 8), FontSize = 12 };
                p.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
                p.SetResourceReference(TextElement.BackgroundProperty, "Brush.Surface");
                doc.Blocks.Add(p);
                continue;
            }

            if (t.StartsWith('|'))
            {
                list = null;
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                {
                    rows.Add(lines[i].Trim());
                }

                i--;
                doc.Blocks.Add(Table(rows));
                continue;
            }

            if (t.Length == 0)
            {
                list = null;
                continue;
            }

            if (t.StartsWith('#'))
            {
                list = null;
                var level = t.TakeWhile(c => c == '#').Count();
                var p = Inline(t[level..].Trim());
                p.FontSize = level switch { 1 => 20, 2 => 15.5, _ => 13.5 };
                p.FontWeight = FontWeights.SemiBold;
                p.Margin = new Thickness(0, level == 1 ? 0 : 14, 0, 6);
                doc.Blocks.Add(p);
                continue;
            }

            if (t.StartsWith('>'))
            {
                list = null;
                var p = Inline(t.TrimStart('>').Trim());
                p.Padding = new Thickness(10, 6, 10, 6);
                p.BorderThickness = new Thickness(3, 0, 0, 0);
                p.SetResourceReference(Block.BorderBrushProperty, "Brush.Accent");
                p.SetResourceReference(TextElement.BackgroundProperty, "Brush.Surface");
                doc.Blocks.Add(p);
                continue;
            }

            var ordered = OrderedItem().Match(t);
            if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal) || ordered.Success)
            {
                var marker = ordered.Success ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                if (list is null || list.MarkerStyle != marker)
                {
                    list = new List { MarkerStyle = marker, Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(20, 0, 0, 0) };
                    doc.Blocks.Add(list);
                }

                var text = ordered.Success ? t[ordered.Length..] : t[2..];
                var p = Inline(text);
                p.Margin = new Thickness(0, 1, 0, 1);
                list.ListItems.Add(new ListItem(p));
                continue;
            }

            list = null;
            var para = Inline(t);
            para.Margin = new Thickness(0, 0, 0, 8);
            doc.Blocks.Add(para);
        }

        return doc;
    }

    private static Paragraph Inline(string text)
    {
        var p = new Paragraph();
        var pos = 0;
        foreach (Match m in InlineToken().Matches(text))
        {
            if (m.Index > pos)
            {
                p.Inlines.Add(new Run(text[pos..m.Index]));
            }

            var tok = m.Value;
            if (tok.StartsWith("**", StringComparison.Ordinal))
            {
                p.Inlines.Add(new Bold(new Run(tok[2..^2])));
            }
            else
            {
                var r = new Run(tok[1..^1]);
                r.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
                r.SetResourceReference(TextElement.BackgroundProperty, "Brush.Surface");
                p.Inlines.Add(r);
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
        {
            p.Inlines.Add(new Run(text[pos..]));
        }

        return p;
    }

    private static Table Table(List<string> rows)
    {
        static string[] Cells(string row) => row.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10), BorderThickness = new Thickness(0, 1, 0, 0) };
        table.SetResourceReference(Block.BorderBrushProperty, "Brush.Border");
        var header = Cells(rows[0]);
        for (var c = 0; c < header.Length; c++)
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(c == 0 ? 1 : 2, GridUnitType.Star) });
        }

        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = Cells(rows[r]);
            if (r == 1 && cells.All(c => c.Trim(':', '-', ' ').Length == 0))
            {
                continue; // |---|---|
            }

            var row = new TableRow();
            for (var c = 0; c < header.Length; c++)
            {
                var p = Inline(c < cells.Length ? cells[c] : "");
                p.Margin = new Thickness(0);
                if (r == 0)
                {
                    p.FontWeight = FontWeights.SemiBold;
                }

                var cell = new TableCell(p) { Padding = new Thickness(6, 5, 10, 5), BorderThickness = new Thickness(0, 0, 0, 1) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "Brush.Border");
                if (r == 0)
                {
                    cell.SetResourceReference(TableCell.BackgroundProperty, "Brush.Surface");
                }

                row.Cells.Add(cell);
            }

            group.Rows.Add(row);
        }

        return table;
    }
}
