using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Foldy.Git;
using Foldy.Presentation.Git;

namespace Foldy.GitView.Tests;

/// <summary>diff / blame 패널과 새 변경 사항 화면이 세 테마에서 그려지는지 (STA), 그리고 문서용 스크린샷.</summary>
public class GitViewDiffRenderTests
{
    private static void Sta(Func<Task> body) => StaHost.Run(body);

    private static void ApplyTheme(string palette)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Foldy.UI;component/Themes/{palette}") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Foldy.UI;component/Themes/Modern.Controls.xaml") });
    }

    private static async Task<(Window win, Foldy.UI.Git.GitView view)> ShowAsync(GitTabViewModel vm, double w = 1180, double h = 740)
    {
        var view = new Foldy.UI.Git.GitView { DataContext = vm, Width = w, Height = h };
        var win = new Window { Content = view, Width = w, Height = h, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -3000 };
        win.Show();
        view.Measure(new Size(w, h));
        view.Arrange(new Rect(0, 0, w, h));
        view.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Background);
        return (win, view);
    }

    private static RenderTargetBitmap Render(FrameworkElement v, double w, double h)
    {
        v.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        return bmp;
    }

    private static void Save(RenderTargetBitmap b, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    [Theory]
    [InlineData("Modern.Light.xaml")]
    [InlineData("Modern.Dark.xaml")]
    [InlineData("Retro.xaml")]
    public void Diff_blame_and_changes_render_in_every_theme(string palette)
    {
        Sta(async () =>
        {
            ApplyTheme(palette);
            var git = new FakeGit
            {
                Files = [new GitFileEntry("src/App.cs", null, GitChange.None, GitChange.Modified),
                         new GitFileEntry("docs/new.md", null, GitChange.None, GitChange.Untracked),
                         new GitFileEntry("src/Old.cs", null, GitChange.Added, GitChange.None)],
                Ahead = 1,
            };
            git.Diffs["src/App.cs"] = GitDiffParser.Parse(
                "@@ -1,5 +1,6 @@ class App\n using System;\n-var a = 1;\n+var a = 2;\n+var b = 3;\n \tvoid Run() { }\n context\n context\n\\ No newline at end of file\n");
            var now = DateTimeOffset.Now;
            git.Blame =
            [
                new BlameLine(1, "aaaaaaa1", "Kim", now.AddDays(-9), "첫 커밋", "using System;", false, false, 0, true),
                new BlameLine(2, "aaaaaaa1", "Kim", now.AddDays(-9), "첫 커밋", "var a = 1;", false, false, 0, false),
                new BlameLine(3, "bbbbbbb2", "Lee", now.AddHours(-3), "고침", "var b = 3;", false, false, 1, true),
            ];
            var vm = new GitTabViewModel(git, @"C:\repos\Foldy", new FakeCommitFiles());
            await vm.RefreshAsync();
            var (win, view) = await ShowAsync(vm);

            // 변경 사항 목록: 그룹 머리글 3 + 파일 3
            var list = (ListBox)view.FindName("ChangesList");
            list.Items.Count.Should().Be(6);

            vm.SelectedChange = vm.ChangeRows.First(r => r.Item?.Path == "src/App.cs");
            await vm.PanelLoadingTask;
            await Dispatcher.Yield(DispatcherPriority.Background);
            view.UpdateLayout();
            var inline = (ListBox)view.FindName("DiffInlineList");
            inline.Visibility.Should().Be(Visibility.Visible);
            inline.Items.Count.Should().Be(9);
            inline.ItemContainerGenerator.ContainerFromIndex(1).Should().NotBeNull("보이는 줄은 실체화된다");
            Render(view, 1180, 740).Width.Should().Be(1180);

            vm.SideBySide = true;
            view.UpdateLayout();
            var side = (ListBox)view.FindName("DiffSideList");
            side.Visibility.Should().Be(Visibility.Visible);
            inline.Visibility.Should().Be(Visibility.Collapsed);
            side.Items.Count.Should().Be(vm.SideRows.Count);
            Render(view, 1180, 740);

            await vm.ShowBlameAsync("src/App.cs");
            view.UpdateLayout();
            var blame = (ListBox)view.FindName("BlameList");
            blame.Visibility.Should().Be(Visibility.Visible);
            blame.Items.Count.Should().Be(3);
            Render(view, 1180, 740);

            vm.ShowHistoryTab();
            await vm.RefreshAsync();
            view.UpdateLayout();
            Render(view, 1180, 740);
            win.Close();
        });
    }

    [Fact]
    public void Setup_overlay_shows_for_non_repository()
    {
        Sta(async () =>
        {
            ApplyTheme("Modern.Light.xaml");
            var git = new FakeGit { FailStatus = GitResult.Fail(GitFailure.NotARepository, "fatal: not a git repository", 128) };
            var vm = new GitTabViewModel(git, @"C:\work\plain", new FakeCommitFiles());
            await vm.RefreshAsync();
            var (win, view) = await ShowAsync(vm, 700, 500);
            vm.ShowSetup.Should().BeTrue();
            Descendants<Button>(view).Any(b => b.Content as string == Foldy.Core.Localization.Loc.T("git.init") && b.IsVisible).Should().BeTrue();
            Render(view, 700, 500);
            win.Close();
        });
    }

    /// <summary>docs/git-view*.png 를 만든다. FOLDY_GITVIEW_DOCS 에 폴더를 주면 실제 임시 저장소로 그려 저장한다.</summary>
    [Theory]
    [InlineData("Modern.Light.xaml", "git-view.png")]
    [InlineData("Modern.Dark.xaml", "git-view-dark.png")]
    public void Documentation_screenshot(string palette, string fileName)
    {
        var outDir = Environment.GetEnvironmentVariable("FOLDY_GITVIEW_DOCS");
        Assert.SkipWhen(string.IsNullOrEmpty(outDir), "FOLDY_GITVIEW_DOCS 가 없어 건너뜀");
        var svc = new GitService();
        Assert.SkipUnless(svc.IsGitAvailable, "git.exe 가 없어 건너뜀");
        var dir = Path.Combine(Path.GetTempPath(), "foldy-gitshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Sta(async () =>
            {
                async Task G(params string[] a) => await svc.Cli.RunAsync(dir, a, new GitRunOptions { Write = true });
                await svc.InitAsync(dir);
                await G("config", "user.name", "Kim Foldy");
                await G("config", "user.email", "k@example.com");
                await G("config", "commit.gpgsign", "false");
                Directory.CreateDirectory(Path.Combine(dir, "src"));
                Directory.CreateDirectory(Path.Combine(dir, "docs"));
                File.WriteAllText(Path.Combine(dir, "src", "Login.cs"),
                    "using System;\n\nnamespace App;\n\npublic class Login\n{\n    public bool Check(string user, string pw)\n    {\n        if (user == null) return false;\n        return pw.Length > 3;\n    }\n}\n");
                File.WriteAllText(Path.Combine(dir, "README.md"), "# 샘플\n\n로그인 예제입니다.\n");
                File.WriteAllText(Path.Combine(dir, "src", "Util.cs"), "namespace App;\n\npublic static class Util { }\n");
                await svc.StageAsync(dir, ["src/Login.cs", "README.md", "src/Util.cs"]);
                await svc.CommitAsync(dir, "처음 만들기");
                File.WriteAllText(Path.Combine(dir, "src", "Login.cs"),
                    "using System;\n\nnamespace App;\n\npublic class Login\n{\n    public bool Check(string user, string pw)\n    {\n        if (string.IsNullOrWhiteSpace(user)) return false;\n        if (pw is null) return false;\n        return pw.Length >= 8;\n    }\n}\n");
                File.WriteAllText(Path.Combine(dir, "README.md"), "# 샘플\n\n로그인 예제입니다.\n\n## 사용법\n\n비밀번호는 8자 이상이어야 합니다.\n");
                File.Delete(Path.Combine(dir, "src", "Util.cs"));
                File.WriteAllText(Path.Combine(dir, "docs", "notes.md"), "메모\n");
                await svc.StageAsync(dir, ["README.md"]);

                ApplyTheme(palette);
                var vm = new GitTabViewModel(svc, dir);
                await vm.RefreshAsync();
                vm.CommitMessage = "";
                var (win, view) = await ShowAsync(vm, 1180, 700);
                vm.SelectedChange = vm.ChangeRows.First(r => r.Item is { Path: "src/Login.cs" });
                await vm.PanelLoadingTask;
                await Dispatcher.Yield(DispatcherPriority.Background);
                view.UpdateLayout();
                Save(Render(view, 1180, 700), Path.Combine(outDir!, fileName));
                win.Close();
            });
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
