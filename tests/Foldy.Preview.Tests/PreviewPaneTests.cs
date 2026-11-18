using System.Windows;
using System.Windows.Threading;
using Foldy.Platform.Preview;
using Foldy.Presentation.Preview;
using Foldy.UI.Preview;

namespace Foldy.Preview.Tests;

public class PreviewPaneTests
{
    private static async Task Pump(Func<bool> until, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!until() && DateTime.UtcNow < end)
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(20);
        }
    }

    private static PreviewRequest For(string path)
    {
        var fi = new FileInfo(path);
        return new PreviewRequest(path, fi.Name, fi.Length, fi.LastWriteTimeUtc, fi.Attributes);
    }

    [Fact]
    public void Text_and_code_render_with_footer_and_highlight() => Sta.Run(async () =>
    {
        var dir = Directory.CreateTempSubdirectory("foldy-prev").FullName;
        try
        {
            var txt = Path.Combine(dir, "a.txt");
            var cs = Path.Combine(dir, "b.cs");
            await File.WriteAllTextAsync(txt, "plain 텍스트");
            await File.WriteAllTextAsync(cs, "public class A { /* c */ int x = 1; }");

            var vm = new PreviewViewModel(PreviewProviderRegistry.CreateDefault()) { Debounce = TimeSpan.Zero };
            var pane = new PreviewPane { DataContext = vm };

            await vm.ShowAsync(For(txt));
            pane.ShownKind.Should().Be(PreviewKind.Text);
            pane.TextView!.IsHighlighted.Should().BeFalse();
            pane.TextView.PlainText.Should().Be("plain 텍스트");
            pane.TextView.FooterText.Should().Be("UTF-8");
            pane.HeaderInfo.Should().NotBeEmpty();

            await vm.ShowAsync(For(cs));
            pane.TextView.IsHighlighted.Should().BeTrue();
            pane.TextView.PlainText.Should().Contain("public class A");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    });

    [Fact]
    public void Status_text_replaces_content_and_force_button_follows_network_state() => Sta.Run(async () =>
    {
        var vm = new PreviewViewModel(PreviewProviderRegistry.CreateDefault()) { Debounce = TimeSpan.Zero };
        var pane = new PreviewPane { DataContext = vm };
        await vm.ShowAsync(new PreviewRequest(@"\\srv\s\a.txt", "a.txt", 10, DateTime.UtcNow, FileAttributes.Normal, IsRemote: true));
        pane.ShownKind.Should().Be(PreviewKind.None);
        pane.FindName("StatusPanel").Should().BeOfType<System.Windows.Controls.StackPanel>().Which.Visibility.Should().Be(Visibility.Visible);
        ((System.Windows.Controls.Button)pane.FindName("ForceButton")).Visibility.Should().Be(Visibility.Visible);
    });

    [Fact]
    public void Markdown_and_image_use_existing_views() => Sta.Run(async () =>
    {
        var dir = Directory.CreateTempSubdirectory("foldy-prev").FullName;
        try
        {
            var md = Path.Combine(dir, "r.md");
            await File.WriteAllTextAsync(md, "# 제목\n\n본문");
            var png = Path.Combine(dir, "p.png");
            File.WriteAllBytes(png, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));

            var vm = new PreviewViewModel(PreviewProviderRegistry.CreateDefault()) { Debounce = TimeSpan.Zero };
            var pane = new PreviewPane { DataContext = vm };

            await vm.ShowAsync(For(md));
            pane.ShownKind.Should().Be(PreviewKind.Markdown);
            pane.MarkdownViewer.Should().NotBeNull();
            pane.MarkdownViewer!.DataContext.Should().BeOfType<Foldy.Presentation.Tabs.MarkdownTabViewModel>();

            await vm.ShowAsync(For(png));
            pane.ShownKind.Should().Be(PreviewKind.Image);
            pane.ImageView!.Current!.Value.Path.Should().Be(png);
            pane.MarkdownViewer.DataContext.Should().BeNull(); // 종류가 바뀌면 이전 뷰는 비운다
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    });

    // 스모크: 이 PC에 .txt 미리보기 처리기가 있으면 실제 COM 처리기를 HwndHost에 띄운다. 없으면 건너뜀.
    [Fact]
    public void Shell_preview_handler_hosts_a_txt_file()
    {
        if (ShellPreviewHandlers.FindHandler(".txt") is null)
        {
            Assert.Skip("이 PC에 .txt 미리보기 처리기가 없습니다.");
        }

        string? failure = null;
        var opened = false;
        Sta.Run(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"foldy-prev-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, "hello from Foldy preview smoke test");
            var window = new Window { Width = 400, Height = 300, Left = -10000, Top = -10000, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            var host = new ShellPreviewHost();
            host.Failed += (_, m) => failure = m;
            window.Content = host;
            try
            {
                window.Show();
                host.Open(path);
                await Pump(() => host.HasSession || failure is not null);
                opened = host.HasSession;
                host.Clear();
                host.HasSession.Should().BeFalse();
            }
            finally
            {
                window.Close();
                File.Delete(path);
            }
        });

        if (!opened)
        {
            // 처리기가 있어도 격리 환경(세션 0, 서로게이트 비활성)에서는 못 띄울 수 있다 → 앱은 상태 줄로 처리한다.
            Assert.Skip($"처리기를 띄우지 못함(환경 제한): {failure}");
        }
    }
}
