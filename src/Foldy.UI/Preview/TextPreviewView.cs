using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Foldy.Presentation.Files;
using Foldy.Presentation.Preview;
using Foldy.UI.Markdown;
using Foldy.UI.Theming;

namespace Foldy.UI.Preview;

/// <summary>
/// 텍스트·코드 미리보기: 고정폭 글꼴, 읽기 전용. 지원 언어는 Markdown의 CodeHighlighter로 간단히 강조한다
/// (작은 파일만: <see cref="HighlightLimit"/> 문자 이하). 큰 파일은 강조 없이 TextBox (가상화된 텍스트 렌더).
/// 아래 한 줄: 인코딩과 잘렸는지. UI 스레드 전용.
/// </summary>
public sealed class TextPreviewView : Grid
{
    /// <summary>이 길이를 넘으면 강조하지 않는다 (FlowDocument 구성 비용).</summary>
    public const int HighlightLimit = 200_000;

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, D2Coding, Malgun Gothic");

    private readonly TextBox _plain;
    private readonly RichTextBox _rich;
    private readonly TextBlock _footer;

    public TextPreviewView()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _plain = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            FontFamily = Mono,
            FontSize = 12.5,
            Padding = new Thickness(8),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _rich = new RichTextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            FontFamily = Mono,
            FontSize = 12.5,
            Visibility = Visibility.Collapsed,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        foreach (var tb in new Control[] { _plain, _rich })
        {
            tb.SetResourceReference(Control.BackgroundProperty, "Brush.Window");
            tb.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        }

        _footer = new TextBlock { Padding = new Thickness(10, 4, 10, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        _footer.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        SetRow(_footer, 1);

        Children.Add(_plain);
        Children.Add(_rich);
        Children.Add(_footer);

        Loaded += (_, _) => ThemeManager.Changed += ApplyPalette;
        Unloaded += (_, _) => ThemeManager.Changed -= ApplyPalette;
    }

    /// <summary>아래 한 줄 문구 (시험·접근성용).</summary>
    public string FooterText => _footer.Text;

    /// <summary>강조해서 그렸는지 (시험용).</summary>
    public bool IsHighlighted => _rich.Visibility == Visibility.Visible;

    public string PlainText => IsHighlighted
        ? new TextRange(_rich.Document.ContentStart, _rich.Document.ContentEnd).Text
        : _plain.Text;

    public void Show(TextPreviewContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var highlight = content.Text.Length <= HighlightLimit && CodeHighlighter.IsSupported(content.Language);
        _footer.Text = FooterFor(content);
        if (!highlight)
        {
            _rich.Visibility = Visibility.Collapsed;
            _rich.Document = new FlowDocument();
            _plain.Visibility = Visibility.Visible;
            _plain.Text = content.Text;
            _plain.ScrollToHome();
            return;
        }

        _plain.Visibility = Visibility.Collapsed;
        _plain.Text = "";
        _rich.Document = BuildDocument(content.Text, content.Language!);
        ApplyPalette();
        _rich.Visibility = Visibility.Visible;
        _rich.ScrollToHome();
    }

    public void Clear()
    {
        _plain.Text = "";
        _rich.Document = new FlowDocument();
        _footer.Text = "";
    }

    private static string FooterFor(TextPreviewContent c)
    {
        var s = c.EncodingName;
        return c.Truncated
            ? $"{s} · {Foldy.Core.Localization.Loc.F("preview.headOnly", FileItem.FormatBytes(c.TotalBytes))}"
            : s;
    }

    private static FlowDocument BuildDocument(string text, string language)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(8), PageWidth = 100_000 };
        var para = new Paragraph { Margin = new Thickness(0), LineHeight = double.NaN };
        foreach (var token in CodeHighlighter.Tokenize(text.Replace("\r\n", "\n", StringComparison.Ordinal), language))
        {
            var parts = token.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    para.Inlines.Add(new LineBreak());
                }

                if (parts[i].Length == 0)
                {
                    continue;
                }

                var run = new Run(parts[i]);
                if (token.Kind != CodeTokenKind.Text)
                {
                    run.SetResourceReference(TextElement.ForegroundProperty, MarkdownPalette.KeyFor(token.Kind));
                }

                para.Inlines.Add(run);
            }
        }

        doc.Blocks.Add(para);
        return doc;
    }

    private void ApplyPalette() => MarkdownPalette.Apply(_rich.Document.Resources, ThemeManager.IsDark);
}
