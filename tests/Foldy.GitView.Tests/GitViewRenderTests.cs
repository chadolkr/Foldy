using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Foldy.Git;
using Foldy.Presentation.Git;
using Foldy.UI.Git;

namespace Foldy.GitView.Tests;

/// <summary>GitView XAML 이 테마 토큰으로 로드·배치·렌더되는지 확인하는 스모크 테스트 (STA 스레드).</summary>
public class GitViewRenderTests
{
    private static void Sta(Func<Task> body) => StaHost.Run(body);

    private static List<CommitNode> Log()
    {
        var list = new List<CommitNode>();
        // 병합이 섞인 작은 이력: m(a,b) → a → b → ... 위쪽 40개는 선형
        list.Add(new CommitNode("m1", ["a1", "b1"], "Kim", "k@x", DateTimeOffset.UnixEpoch.AddDays(900), "Merge branch 'feature'",
            [new GitRef("main", GitRefKind.LocalBranch, true), new GitRef("origin/main", GitRefKind.RemoteBranch), new GitRef("v1.0", GitRefKind.Tag)]));
        list.Add(new CommitNode("a1", ["r1"], "Lee", "l@x", DateTimeOffset.UnixEpoch.AddDays(899), "메인 쪽 작업", []));
        list.Add(new CommitNode("b1", ["r1"], "Park", "p@x", DateTimeOffset.UnixEpoch.AddDays(898), "feature 작업", [new GitRef("feature", GitRefKind.LocalBranch)]));
        list.Add(new CommitNode("r1", ["r0"], "Kim", "k@x", DateTimeOffset.UnixEpoch.AddDays(897), "공통 조상", []));
        list.Add(new CommitNode("r0", [], "Kim", "k@x", DateTimeOffset.UnixEpoch.AddDays(896), "첫 커밋", []));
        return list;
    }

    [Theory]
    [InlineData("Modern.Light.xaml")]
    [InlineData("Modern.Dark.xaml")]
    public void View_loads_and_renders_with_theme_tokens(string palette)
    {
        Sta(async () =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Foldy.UI;component/Themes/{palette}") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Foldy.UI;component/Themes/Modern.Controls.xaml") });

            var git = new FakeGit
            {
                Log = Log(),
                Files = [new GitFileEntry("src/App.cs", null, GitChange.None, GitChange.Modified),
                         new GitFileEntry("docs/new.md", null, GitChange.None, GitChange.Untracked),
                         new GitFileEntry("src/Old.cs", "src/Older.cs", GitChange.Renamed, GitChange.None)],
                Ahead = 2,
            };
            var vm = new GitTabViewModel(git, @"C:\repos\Foldy", new FakeCommitFiles());
            await vm.RefreshAsync();
            var view = new Foldy.UI.Git.GitView { DataContext = vm, Width = 1100, Height = 720 };
            var win = new Window { Content = view, Width = 1100, Height = 720, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -3000 };
            win.Show();
            view.Measure(new Size(1100, 720));
            view.Arrange(new Rect(0, 0, 1100, 720));
            view.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Background);

            var bmp = new RenderTargetBitmap(1100, 720, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(view);
            bmp.Width.Should().Be(1100);

            // 커밋을 선택해 아래 칸이 변경 파일로 바뀌는지
            vm.SelectedCommit = vm.Commits[0];
            await vm.CommitFilesLoading;
            view.UpdateLayout();
            var bmp2 = new RenderTargetBitmap(1100, 720, 96, 96, PixelFormats.Pbgra32);
            bmp2.Render(view);

            var dir = Environment.GetEnvironmentVariable("FOLDY_GITVIEW_SHOTS");
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                foreach (var (name, b) in new[] { ("changes", bmp), ("commit", bmp2) })
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(b));
                    using var fs = File.Create(Path.Combine(dir, $"git-{palette[..^5]}-{name}.png"));
                    enc.Save(fs);
                }
            }
            win.Close();
        });
    }
}
