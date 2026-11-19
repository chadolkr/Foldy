using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Foldy.App.Services;
using Foldy.Core.Threading;
using Foldy.Presentation.Jobs;
using Foldy.Presentation.Panes;
using Foldy.Presentation.Services;
using Foldy.Presentation.Tabs;
using Foldy.UI.Theming;
using Serilog;

namespace Foldy.App;

/// <summary>
/// 문서용 화면 캡처: <c>Foldy.exe --doc-screenshots &lt;폴더&gt; [--doc-root C:\FoldyDemo]</c> (환경 변수 FOLDY_DATA_DIR 필수).
/// 가짜 샘플 폴더(DocShotsSandbox)에서 장면마다 PNG를 한 장씩 저장하고 종료한다. build/doc-screenshots.ps1 이 실행한다.
/// 모던 라이트 테마, 내용 영역 1600×1000. 대화상자는 모달 없이 띄워서 찍는다.
/// </summary>
public partial class ShellWindow
{
    private string _docOut = "";
    private readonly List<string> _docFailures = [];
    private Foldy.Core.Templates.TemplateLibrary? _docLibrary;

    private static string DocRoot => DocShotsSandbox.RootOf(App.Options);

    internal async Task RunDocShotsAsync(string outDir)
    {
        _docOut = Path.GetFullPath(outDir);
        Directory.CreateDirectory(_docOut);
        try
        {
            await DocSetupAsync();
            await DocScenesAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "문서 캡처 실패");
            _docFailures.Add("치명적: " + ex);
        }

        await File.WriteAllTextAsync(Path.Combine(_docOut, "_result.txt"), _docFailures.Count == 0 ? "ok" : string.Join("\n", _docFailures));
        Application.Current.Shutdown(_docFailures.Count == 0 ? 0 : 1);
    }

    private async Task DocSetupAsync()
    {
        // 내용 영역을 정확히 1600×1000 으로
        WindowState = WindowState.Normal;
        Left = 0;
        Top = 0;
        var root = (FrameworkElement)Content;
        for (var i = 0; i < 4; i++)
        {
            Width += 1600 - root.ActualWidth;
            Height += 1000 - root.ActualHeight;
            await DocSettleAsync(250);
        }

        Activate();
        DocFixStatus();
        _docLibrary = new Foldy.Core.Templates.TemplateLibrary(Path.Combine(DocRoot, "_remote", "templates")).Reload();
        File.SetAttributes(Path.Combine(DocRoot, "_remote"), FileAttributes.Directory | FileAttributes.Hidden);
        await DocSettleAsync(1500);
    }

    // ───────────── 도우미 ─────────────

    private async Task DocSettleAsync(int ms = 700)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static FolderTabViewModel DocFolderTab(PaneViewModel pane, int index = 0)
    {
        var t = pane.Tabs.OfType<FolderTabViewModel>().ElementAt(index);
        pane.ActiveTab = t;
        return t;
    }

    private async Task DocGoAsync(PaneViewModel pane, string path, string? focus = null, int tabIndex = 0)
    {
        var t = DocFolderTab(pane, tabIndex);
        if (!string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            await t.NavigateAsync(path);
        }

        if (focus is not null)
        {
            await t.RefreshAsync(focus);
        }

        await DocSettleAsync(600);
    }

    private void DocActivate(PaneViewModel pane)
    {
        Shell.Activate(pane);
        (pane == Shell.Left ? LeftPane : RightPane).FocusList();
    }

    /// <summary>한쪽 패널만 넓게 보이기(Git·분석·도움말처럼 넓은 화면이 필요한 장면). 둘 다 false 면 1:1 로 되돌린다.</summary>
    private void DocWide(bool leftOnly, bool rightOnly = false)
    {
        LeftPane.Visibility = rightOnly ? Visibility.Collapsed : Visibility.Visible;
        RightPane.Visibility = leftOnly ? Visibility.Collapsed : Visibility.Visible;
        LeftColumn.MinWidth = rightOnly ? 0 : 320;
        RightColumn.MinWidth = leftOnly ? 0 : 320;
        LeftColumn.Width = new GridLength(rightOnly ? 0 : 1, GridUnitType.Star);
        RightColumn.Width = new GridLength(leftOnly ? 0 : 1, GridUnitType.Star);
    }

    /// <summary>문서 그림은 색인 서비스와 Rust 복사 엔진이 갖춰진 보통 설치 상태로 보인다 (검색 자체는 폴더 훑기로 한다).</summary>
    private void DocFixStatus()
    {
        IndexerText.Text = "검색: 색인 사용";
        CopyEngineText.Text = "복사 엔진: Rust";
    }

    private static RenderTargetBitmap DocRender(FrameworkElement root, Brush? background, int scale)
    {
        var dpi = 96.0 * scale;
        var w = (int)Math.Round(root.ActualWidth * scale);
        var h = (int)Math.Round(root.ActualHeight * scale);
        var bmp = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
        {
            dc.DrawRectangle(background ?? Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }

        bmp.Render(bg);
        bmp.Render(root);
        return bmp;
    }

    private void DocSave(string name, BitmapSource bmp, int scale)
    {
        BitmapSource final = scale > 1 ? new TransformedBitmap(bmp, new ScaleTransform(1.0 / scale, 1.0 / scale)) : bmp;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(final));
        using var fs = File.Create(Path.Combine(_docOut, name + ".png"));
        enc.Save(fs);
    }

    private async Task DocShotWindowAsync(string name, int settleMs = 500)
    {
        await DocSettleAsync(settleMs);
        DocFixStatus();
        var root = (FrameworkElement)Content;
        DocSave(name, DocRender(root, Background, 2), 2);
    }

    private async Task DocShotDialogAsync(string name, Window win, int settleMs = 700)
    {
        win.UpdateLayout();
        await DocSettleAsync(settleMs);
        var root = VisualTreeHelper.GetChild(win, 0) as FrameworkElement ?? (FrameworkElement)win.Content; // 안쪽 여백(Margin)까지 포함하려고 창의 맨 위 요소를 찍는다
        DocSave(name, DocRender(root, win.Background, 2), 2);
    }

    private Window DocOpen(Window win)
    {
        win.Owner = this;
        win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeManager.Attach(win);
        win.ShowActivated = true;
        win.Show();
        return win;
    }

    private async Task DocSceneAsync(string name, Func<Task> body)
    {
        try
        {
            await body();
            Log.Information("문서 장면 완료: {Name}", name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "문서 장면 실패: {Name}", name);
            _docFailures.Add($"{name}: {ex.Message}");
        }
    }

    // ───────────── 장면 ─────────────

    private async Task DocScenesAsync()
    {
        var r = DocRoot;
        var left = Shell.Left;
        var right = Shell.Right;

        await DocSceneAsync("main", async () =>
        {
            await DocGoAsync(left, DocShotsSandbox.Repo(r), "README.md");
            await DocGoAsync(right, DocShotsSandbox.Downloads(r), "회의록_최종_v2.docx");
            DocActivate(left);
            await DocShotWindowAsync("main", 1500);
        });

        await DocSceneAsync("preview-image", async () =>
        {
            DocFolderTab(left);
            await DocGoAsync(right, DocShotsSandbox.Trip(r), "성산일출봉.jpg", tabIndex: 1);
            DocActivate(right);
            RightPane.TogglePreview();
            await DocShotWindowAsync("preview-image", 1800);
            RightPane.TogglePreview();
        });

        await DocSceneAsync("preview-markdown", async () =>
        {
            await DocGoAsync(left, DocShotsSandbox.Documents(r), "프로젝트_안내.md", tabIndex: 1);
            DocActivate(left);
            LeftPane.TogglePreview();
            await DocShotWindowAsync("preview-markdown", 1800);
            LeftPane.TogglePreview();
        });

        await DocSceneAsync("images-grid", async () =>
        {
            await DocGoAsync(right, DocShotsSandbox.Trip(r), "한라산.jpg", tabIndex: 1);
            DocWide(leftOnly: false, rightOnly: true);
            DocActivate(right);
            RightPane.ToggleImages();
            await DocShotWindowAsync("images-grid", 3500);
            RightPane.ToggleImages();
            DocWide(leftOnly: false);
        });

        await DocSceneAsync("markdown", async () =>
        {
            DocWide(leftOnly: true);
            DocActivate(left);
            var tab = Foldy.Presentation.Shell.ShellViewModel.OpenMarkdown(left, Path.Combine(DocShotsSandbox.Documents(r), "프로젝트_안내.md"));
            await DocShotWindowAsync("markdown", 2000);
            left.CloseTab(tab);
            DocWide(leftOnly: false);
        });

        await DocSceneAsync("git", async () =>
        {
            DocFolderTab(left);
            DocWide(leftOnly: true);
            DocActivate(left);
            var git = OpenGitTab(left, DocShotsSandbox.Repo(r), activate: true);
            await git.RefreshAsync();
            git.ShowChangesTab();
            await DocSettleAsync(800);
            var change = git.ChangeRows.FirstOrDefault(c => c.Item?.Path == "src/app.py") ?? git.ChangeRows.FirstOrDefault(c => c.Item is not null);
            git.SelectedChange = change;
            await git.PanelLoadingTask;
            await DocShotWindowAsync("git-changes", 1200);

            await git.ShowBlameAsync("src/app.py");
            await DocShotWindowAsync("git-blame", 1200);
            git.ClosePanel();

            git.ShowHistoryTab();
            await DocSettleAsync(600);
            var commit = git.Commits.FirstOrDefault(c => c.Node.Subject.Contains("완료 표시", StringComparison.Ordinal)) ?? git.Commits.ElementAtOrDefault(2);
            git.SelectedCommit = commit;
            await git.CommitFilesLoading;
            git.SelectedCommitFile = git.CommitFiles.FirstOrDefault();
            await git.PanelLoadingTask;
            await DocShotWindowAsync("git-history", 1500);
            left.CloseTab(git);
            DocWide(leftOnly: false);
        });

        await DocSceneAsync("search", async () =>
        {
            await DocGoAsync(right, r, null, tabIndex: 0);
            DocWide(leftOnly: false, rightOnly: true);
            DocActivate(right);
            var tab = Shell.StartSearch(right, "보고서");
            for (var i = 0; i < 40 && (tab is null || tab.IsBusy || tab.Items.Count == 0); i++)
            {
                await Task.Delay(250);
            }

            await DocShotWindowAsync("search", 1200);
            if (tab is not null)
            {
                right.CloseTab(tab);
            }

            DocWide(leftOnly: false);
        });

        await DocSceneAsync("analysis", async () =>
        {
            DocFolderTab(left);
            DocWide(leftOnly: true);
            DocActivate(left);
            var coordinator = new Foldy.Analysis.AnalysisCoordinator(null, localDirectory: Path.Combine(App.DataDir, "analysis"));
            var tab = new Foldy.Presentation.Analysis.AnalysisTabViewModel(coordinator, r);
            left.AddTab(tab, activate: true, assignColor: true);
            await DocSettleAsync(600);
            await tab.AnalyzeAsync();
            await DocShotWindowAsync("analysis", 2500);
            left.CloseTab(tab);
            DocWide(leftOnly: false);
        });

        // README 첫 화면: 왼쪽 = 폴더 분석, 오른쪽 = Git 화면 (변경 내용 보기)
        await DocSceneAsync("hero", async () =>
        {
            DocFolderTab(left);
            DocFolderTab(right);
            DocWide(leftOnly: false);
            SidebarExpanded = false;
            LeftPane.TreeVisible = false;
            RightPane.TreeVisible = false;
            var coordinator = new Foldy.Analysis.AnalysisCoordinator(null, localDirectory: Path.Combine(App.DataDir, "analysis"));
            var analysis = new Foldy.Presentation.Analysis.AnalysisTabViewModel(coordinator, DocShotsSandbox.Space(r));
            left.AddTab(analysis, activate: true, assignColor: true);
            await DocSettleAsync(600);
            await analysis.AnalyzeAsync();

            var git = OpenGitTab(right, DocShotsSandbox.Repo(r), activate: true);
            await git.RefreshAsync();
            git.ShowChangesTab();
            await DocSettleAsync(800);
            git.SelectedChange = git.ChangeRows.FirstOrDefault(c => c.Item?.Path == "src/app.py") ?? git.ChangeRows.FirstOrDefault(c => c.Item is not null);
            await git.PanelLoadingTask;
            DocActivate(right);
            await DocShotWindowAsync("hero", 2500);
            right.CloseTab(git);
            left.CloseTab(analysis);
            SidebarExpanded = true;
            LeftPane.TreeVisible = true;
            RightPane.TreeVisible = true;
        });

        await DocSceneAsync("settings", async () =>
        {
            var vm = new Foldy.Presentation.Settings.SettingsViewModel(_settings, _keyMap);
            var win = new Foldy.UI.Settings.SettingsWindow(vm);
            DocOpen(win);
            win.SelectCategory(0);
            await DocShotDialogAsync("settings-general", win);
            win.SelectCategory(1);
            await DocShotDialogAsync("settings-appearance", win);
            win.SelectCategory(4);
            await DocShotDialogAsync("settings-copy", win);
            win.SelectCategory(6);
            await DocShotDialogAsync("settings-keys", win);
            win.Close();
        });

        await DocSceneAsync("template-picker", async () =>
        {
            var vm = new Foldy.Presentation.Templates.TemplateDialogViewModel(_docLibrary!, Path.Combine(r, "Projects"));
            var item = vm.Templates.First(t => t.Template.Id == "java-project");
            vm.SelectedTemplate = item;
            if (vm.Customizer.Variables.FirstOrDefault(v => v.Key == "프로젝트") is { } nameInput)
            {
                nameInput.Value = "payment-service";
            }

            vm.SelectedTab = Foldy.Presentation.Templates.TemplateDialogTab.Preview;
            var win = DocOpen(new Foldy.UI.Templates.TemplatePickerWindow { DataContext = vm });
            foreach (var lb in DocFind<ListBox>(win))
            {
                if (lb.Items.Contains(item))
                {
                    lb.ScrollIntoView(item);
                }
            }

            await DocShotDialogAsync("template-picker", win, 1000);
            vm.SelectedTab = Foldy.Presentation.Templates.TemplateDialogTab.Guide;
            await DocShotDialogAsync("template-guide", win, 600);
            win.Close();
        });

        await DocSceneAsync("wizard", async () =>
        {
            var vm = new Foldy.Presentation.Templates.FolderWizardViewModel(Foldy.Core.Templates.TemplateLibrary.LoadWizard(), _docLibrary!, Path.Combine(r, "Projects"));
            var win = DocOpen(new Foldy.UI.Templates.FolderWizardWindow { DataContext = vm });
            vm.SelectPurpose("dev");
            await DocShotDialogAsync("wizard-purpose", win, 800);
            vm.NextCommand.Execute(null);
            foreach (var q in vm.Questions)
            {
                q.Select(q.Choices[0].Value.Id);
            }

            await DocShotDialogAsync("wizard-questions", win, 600);
            vm.NextCommand.Execute(null);
            await DocShotDialogAsync("wizard-recommend", win, 800);
            win.Close();
        });

        await DocSceneAsync("organize", async () =>
        {
            var org = new Foldy.Presentation.Templates.OrganizeViewModel(DocShotsSandbox.Downloads(r), new WpfDispatcher(Dispatcher), templates: _docLibrary!.Templates, journalFolder: Path.Combine(App.DataDir, "jobs"));
            var win = DocOpen(new Foldy.UI.Templates.OrganizeWindow { DataContext = org });
            await org.PlanCommand.ExecuteAsync(null);
            await DocShotDialogAsync("organize", win, 1200);
            win.Close();

            var proj = new Foldy.Presentation.Templates.OrganizeViewModel(DocShotsSandbox.Repo(r), new WpfDispatcher(Dispatcher), templates: _docLibrary!.Templates, journalFolder: Path.Combine(App.DataDir, "jobs"));
            var win2 = DocOpen(new Foldy.UI.Templates.OrganizeWindow { DataContext = proj });
            await proj.PlanCommand.ExecuteAsync(null);
            await DocShotDialogAsync("organize-project-folder", win2, 1000);
            win2.Close();
        });

        await DocSceneAsync("sftp", async () =>
        {
            var win = DocOpen(new SftpConnectWindow(_sftp));
            await DocShotDialogAsync("sftp-connect", win, 600);
            win.Close();
        });

        await DocSceneAsync("jobs", async () =>
        {
            DocFolderTab(left);
            DocActivate(left);
            var job = new JobViewModel("'제주여행' 복사", new WpfDispatcher(Dispatcher)) { MascotKind = Foldy.Presentation.Mascot.JobKind.Copy };
            job.Detail = "성산일출봉.jpg 외 11개";
            Shell.Jobs.Insert(0, job);
            job.Report(new FileOpProgress(450, 1000, "성산일출봉.jpg"));
            await DocSettleAsync(300);
            foreach (var m in DocFind<Foldy.UI.Mascot.MascotView>(JobsList))
            {
                m.PauseWhenInactive = false;
                m.BytesPerSecond = 180_000_000;
            }

            Activate();
            for (var i = 0; i < 12; i++)
            {
                await DocSettleAsync(500);
                if (DocFind<Foldy.UI.Mascot.MascotView>(JobsList).Any(m => m.CurrentFrame.Visible))
                {
                    break;
                }
            }

            await DocSettleAsync(1500);
            await DocSettleAsync(300);
            DocFixStatus();
            var full = DocRender((FrameworkElement)Content, Background, 2);
            var origin = JobsPanel.TranslatePoint(new Point(0, 0), (FrameworkElement)Content);
            var crop = new CroppedBitmap(full, new Int32Rect(
                (int)(380 * 2), (int)(origin.Y * 2), 920 * 2, (int)(JobsPanel.ActualHeight * 2)));
            DocSave("jobs-mascot", crop, 1);
            Shell.Jobs.Remove(job);
        });

        await DocSceneAsync("palette", async () =>
        {
            DocActivate(left);
            ShowPalette();
            PaletteInput.Text = "폴더";
            Palette.StaysOpen = true;
            await DocSettleAsync(800);
            var main = DocRender((FrameworkElement)Content, Background, 2);
            if (Palette.Child is FrameworkElement child)
            {
                var pop = DocRender(child, Brushes.Transparent, 2);
                var origin = PanesGrid.TranslatePoint(new Point(0, 0), (FrameworkElement)Content);
                var x = origin.X + ((PanesGrid.ActualWidth - child.ActualWidth) / 2);
                var y = origin.Y + ((PanesGrid.ActualHeight - child.ActualHeight) / 2);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(main, new Rect(0, 0, main.PixelWidth / 2.0, main.PixelHeight / 2.0));
                    dc.DrawImage(pop, new Rect(x, y, child.ActualWidth, child.ActualHeight));
                }

                var comp = new RenderTargetBitmap(main.PixelWidth, main.PixelHeight, 192, 192, PixelFormats.Pbgra32);
                comp.Render(dv);
                DocSave("palette", comp, 2);
            }

            Palette.StaysOpen = false;
            Palette.IsOpen = false;
        });

        await DocSceneAsync("help", async () =>
        {
            DocWide(leftOnly: true);
            DocActivate(left);
            var before = left.Tabs.Count;
            ShowHelp();
            await DocSettleAsync(2000);
            await DocShotWindowAsync("help", 800);
            if (left.Tabs.Count > before && left.ActiveTab is { } t)
            {
                left.CloseTab(t);
            }

            DocWide(leftOnly: false);
        });

        await DocSceneAsync("guide", async () =>
        {
            var g = (GuideWindow)DocOpen(new GuideWindow("바탕화면 정리") { Height = 520 });
            g.Say("방 청소해 주듯 바탕화면을 정리해 드릴게요. 버리는 건 하나도 없어요.");
            g.Say("한동안 쓰지 않은 파일이 128개 있어요 (문서 52개, 이미지 41개, 압축 20개, 기타 15개). 바로 가기와 최근 7일 안에 쓴 파일은 그대로 둘게요.");
            _ = g.AskAsync("바탕화면에 \"바탕화면 보관\" 폴더를 만들고, 그 안에 달별·종류별로 넣어 둘게요. 마음에 안 들면 [되돌리기] 한 번이면 원래대로예요.",
                ("go", "이대로 정리"), ("detail", "자세히 보기"), ("later", "나중에"));
            await DocShotDialogAsync("guide", g, 500);
            g.Close();
        });

        await DocSceneAsync("about", async () =>
        {
            var win = DocOpen(new AboutWindow());
            await DocShotDialogAsync("about", win, 500);
            win.Close();
        });
    }

    private static IEnumerable<T> DocFind<T>(DependencyObject root) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t)
            {
                yield return t;
            }

            foreach (var d in DocFind<T>(c))
            {
                yield return d;
            }
        }
    }
}
