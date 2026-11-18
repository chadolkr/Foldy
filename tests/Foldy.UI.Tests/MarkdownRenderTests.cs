using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Foldy.Presentation.Tabs;
using Foldy.UI.Markdown;

namespace Foldy.UI.Tests;

public sealed class MarkdownRenderTests : IDisposable
{
    private const string Readme = """
        # Foldy

        빠른 **듀얼 패널** 파일 관리자. ~~느린~~ 아님. 각주도 있다[^1].

        ![로고](assets/logo.png)

        ![원격](https://example.com/badge.png)

        | 이름 | 크기 |
        |:-----|-----:|
        | a.txt | 1 KB |
        | `b.md` | 2 KB |

        ```csharp
        public static int Main() => 0; // 끝
        ```

        - [x] 완료
        - [ ] 남음

        > 인용문

        <div onclick="alert(1)">raw</div>

        ---

        [^1]: 각주 본문.
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-md-render-" + Guid.NewGuid().ToString("N"));

    public MarkdownRenderTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "assets"));
        WritePng(Path.Combine(_dir, "assets", "logo.png"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Sample_readme_produces_expected_block_types() => Sta.Run(() =>
    {
        var ast = MarkdownParsing.Parse(Readme);
        var headings = MarkdownOutline.Extract(ast);
        var renderer = new MarkdownFlowRenderer(new MarkdownRenderOptions { BaseFolder = _dir, LoadImagesSynchronously = true }, headings);
        var doc = renderer.Render(ast);
        var blocks = doc.Blocks.ToList();

        // 제목
        blocks[0].Should().BeOfType<Paragraph>().Which.FontWeight.Should().Be(FontWeights.SemiBold);
        renderer.Anchors.Should().ContainKey("foldy");

        // 굵게·취소선
        var para = (Paragraph)blocks[1];
        AllInlines(para.Inlines).OfType<Span>().Should().Contain(s => s.FontWeight == FontWeights.Bold);
        AllInlines(para.Inlines).OfType<Span>().Should().Contain(s => s.TextDecorations == TextDecorations.Strikethrough);

        // 로컬 상대 이미지 → Image, 원격 이미지 → 차단(자리 표시)
        var image = AllInlines(((Paragraph)blocks[2]).Inlines).OfType<InlineUIContainer>().Single().Child.Should().BeOfType<Image>().Subject;
        image.Source.Should().NotBeNull();
        renderer.HasBlockedImages.Should().BeTrue();
        AllInlines(((Paragraph)blocks[3]).Inlines).OfType<InlineUIContainer>().Should().BeEmpty();

        // 표: 머리 1줄 + 본문 2줄, 오른쪽 정렬 열
        var table = blocks.OfType<Table>().Single();
        table.Columns.Should().HaveCount(2);
        var rows = table.RowGroups[0].Rows;
        rows.Should().HaveCount(3);
        ((Paragraph)rows[1].Cells[1].Blocks.FirstBlock).TextAlignment.Should().Be(TextAlignment.Right);

        // 코드 블록: 헤더(복사 버튼) + 강조된 고정폭 문단
        var code = blocks.OfType<Section>().First(s => s.Blocks.FirstBlock is BlockUIContainer);
        var header = (DockPanel)((BlockUIContainer)code.Blocks.FirstBlock).Child;
        FindButtons(header).Select(b => b.Content).Should().Contain("복사");
        var codePara = (Paragraph)code.Blocks.LastBlock;
        codePara.Inlines.OfType<Run>().Should().Contain(r => r.Text == "public" && r.ReadLocalValue(TextElement.ForegroundProperty) != DependencyProperty.UnsetValue);
        new TextRange(codePara.ContentStart, codePara.ContentEnd).Text.Should().Contain("// 끝");

        // 체크박스 목록 (글머리표 없음)
        var list = blocks.OfType<List>().Single();
        list.MarkerStyle.Should().Be(TextMarkerStyle.None);
        TextOf(list).Should().Contain("☑ 완료").And.Contain("☐ 남음");

        // 원문 HTML은 글자 그대로 (실행 안 함)
        blocks.OfType<Paragraph>().Select(TextOf).Should().Contain(t => t.Contains("<div onclick=", StringComparison.Ordinal));

        // 구분선, 각주
        blocks.OfType<BlockUIContainer>().Should().ContainSingle();
        renderer.Anchors.Should().ContainKey("fn-1").And.ContainKey("fnref-1");
        TextOf(blocks.Last()).Should().Contain("1. 각주 본문.");
    });

    [Fact]
    public void Remote_images_load_when_allowed_without_blocking() => Sta.Run(() =>
    {
        var ast = MarkdownParsing.Parse("![x](https://example.invalid/a.png)");
        var renderer = new MarkdownFlowRenderer(new MarkdownRenderOptions { AllowRemoteImages = true }, []);
        var doc = renderer.Render(ast);
        renderer.HasBlockedImages.Should().BeFalse();
        AllInlines(((Paragraph)doc.Blocks.FirstBlock).Inlines).OfType<InlineUIContainer>().Should().ContainSingle();
    });

    [Fact]
    public void Mermaid_without_renderer_is_plain_code_block() => Sta.Run(() =>
    {
        var ast = MarkdownParsing.Parse("```mermaid\ngraph TD; A-->B\n```");
        var renderer = new MarkdownFlowRenderer(new MarkdownRenderOptions(), []);
        var doc = renderer.Render(ast);
        renderer.MermaidBlocks.Should().BeEmpty();
        TextOf(doc.Blocks.FirstBlock).Should().Contain("A-->B");
    });

    [Fact]
    public void Mermaid_with_renderer_is_lazy_and_falls_back_on_error() => Sta.Run(async () =>
    {
        var fake = new FakeMermaid();
        var ast = MarkdownParsing.Parse("```mermaid\ngraph TD; A-->B\n```\n\n```mermaid\nbroken\n```");
        var renderer = new MarkdownFlowRenderer(new MarkdownRenderOptions { Mermaid = fake }, []);
        renderer.Render(ast);

        renderer.MermaidBlocks.Should().HaveCount(2);
        fake.Calls.Should().Be(0); // 그리기 전에는 렌더러를 부르지 않는다

        await renderer.MermaidBlocks[0].RenderAsync(fake, dark: false);
        renderer.MermaidBlocks[0].ShowingDiagram.Should().BeTrue();

        await renderer.MermaidBlocks[1].RenderAsync(fake, dark: false);
        renderer.MermaidBlocks[1].ShowingDiagram.Should().BeFalse();
    });

    [Fact]
    public void Theme_palette_switches_live() => Sta.Run(() =>
    {
        var doc = MarkdownFlowRenderer.CreateDocument(dark: false);
        var light = ((SolidColorBrush)doc.Resources[MarkdownPalette.Keyword]).Color;
        MarkdownPalette.Apply(doc.Resources, dark: true);
        ((SolidColorBrush)doc.Resources[MarkdownPalette.Keyword]).Color.Should().NotBe(light);
    });

    [Fact]
    public void View_loads_file_follows_md_links_and_scrolls_to_anchor() => Sta.Run(async () =>
    {
        var main = Path.Combine(_dir, "README.md");
        var other = Path.Combine(_dir, "other.md");
        var filler = string.Join("\n\n", Enumerable.Range(0, 120).Select(i => $"문단 {i}"));
        await File.WriteAllTextAsync(main, $"# 처음\n\n[다른 문서](other.md#끝)\n\n{filler}\n\n## 마지막 제목\n\n끝 문단\n\n{filler}");
        await File.WriteAllTextAsync(other, $"# 다른\n\n{filler}\n\n## 끝\n\n{filler}");

        var vm = new MarkdownTabViewModel(main);
        var view = new MarkdownView();
        var window = new Window
        {
            Content = view,
            Width = 600,
            Height = 400,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        var loaded = 0;
        view.DocumentLoaded += (_, _) => loaded++;
        view.DataContext = vm;
        window.Show();
        try
        {
            await WaitFor(() => loaded >= 1);
            view.Headings.Select(h => h.Anchor).Should().Equal("처음", "마지막-제목");

            // 앵커 이동: 제목이 보이는 영역 맨 위 근처로
            view.ScrollToAnchor("마지막-제목").Should().BeTrue();
            await Dispatcher.Yield(DispatcherPriority.Background);
            var viewer = (FlowDocumentScrollViewer)view.FindName("Viewer");
            var target = viewer.Document.Blocks.OfType<Paragraph>().Single(p => TextOf(p) == "마지막 제목");
            var rect = target.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            rect.Top.Should().BeInRange(0, 40);

            // 상대 .md 링크 → 같은 탭에서 열고 #끝으로 이동
            var link = AllInlines(viewer.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines)).OfType<Hyperlink>().Single();
            link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            await WaitFor(() => loaded >= 2);
            vm.FilePath.Should().Be(other);
            vm.CanGoBack.Should().BeTrue();
            var end = viewer.Document.Blocks.OfType<Paragraph>().Single(p => TextOf(p) == "끝");
            end.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top.Should().BeInRange(0, 40);

            // 파일이 바뀌면 자동으로 다시 읽는다
            await File.WriteAllTextAsync(other, "# 바뀐 문서\n");
            await WaitFor(() => loaded >= 3);
            view.Headings.Single().Text.Should().Be("바뀐 문서");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Folder_links_raise_event() => Sta.Run(async () =>
    {
        var main = Path.Combine(_dir, "README.md");
        await File.WriteAllTextAsync(main, "[자산](assets/)");
        var vm = new MarkdownTabViewModel(main);
        var view = new MarkdownView();
        var window = new Window { Content = view, Width = 300, Height = 200, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var loaded = false;
        string? requested = null;
        view.DocumentLoaded += (_, _) => loaded = true;
        view.FolderLinkRequested += (_, e) =>
        {
            requested = e.Path;
            e.Handled = true;
        };
        view.DataContext = vm;
        window.Show();
        try
        {
            await WaitFor(() => loaded);
            var viewer = (FlowDocumentScrollViewer)view.FindName("Viewer");
            AllInlines(((Paragraph)viewer.Document.Blocks.FirstBlock).Inlines).OfType<Hyperlink>().Single().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            requested.Should().Be(Path.Combine(_dir, "assets"));
        }
        finally
        {
            window.Close();
        }
    });

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("조건을 기다리다 시간 초과");
            }

            await Task.Delay(20);
        }
    }

    private static IEnumerable<Inline> AllInlines(IEnumerable<Inline> inlines)
    {
        foreach (var i in inlines)
        {
            yield return i;
            if (i is Span s)
            {
                foreach (var c in AllInlines(s.Inlines))
                {
                    yield return c;
                }
            }
        }
    }

    private static IEnumerable<Button> FindButtons(Panel panel) =>
        panel.Children.OfType<Panel>().SelectMany(FindButtons).Concat(panel.Children.OfType<Button>());

    private static string TextOf(TextElement e) => new TextRange(e.ContentStart, e.ContentEnd).Text.Trim();

    private static void WritePng(string path)
    {
        var bmp = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 3 * 4], 16);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private sealed class FakeMermaid : IMermaidRenderer
    {
        public int Calls { get; private set; }

        public Task<ImageSource> RenderAsync(string code, bool dark, CancellationToken cancellationToken)
        {
            Calls++;
            if (code.Contains("broken", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Parse error");
            }

            var bmp = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
            return Task.FromResult<ImageSource>(bmp);
        }
    }
}
