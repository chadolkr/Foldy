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
    /// <summary>문서 그림의 예시 프로젝트 경로 (앱 언어로).</summary>
    private static string DemoProj => @"D:\" + Foldy.Core.Localization.Loc.T("demo.projectsDir");

    private void DocFixStatus()
    {
        IndexerText.Text = Foldy.Core.Localization.Loc.F("sw.idx.ready", 1_284_513);
        CopyEngineText.Text = Foldy.Core.Localization.Loc.T("sw.copyRust");
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

    private async Task DocShotWindowAsync(string name, int settleMs = 500, Func<FrameworkElement, IEnumerable<FrameworkElement?>>? focus = null)
    {
        await DocSettleAsync(settleMs);
        DocFixStatus();
        var root = (FrameworkElement)Content;
        DocSave(name, DocRender(root, Background, 2), 2);
        DocSaveFocus(name, root, focus);
    }

    private async Task DocShotDialogAsync(string name, Window win, int settleMs = 700, Func<FrameworkElement, IEnumerable<FrameworkElement?>>? focus = null)
    {
        win.UpdateLayout();
        await DocSettleAsync(settleMs);
        var root = VisualTreeHelper.GetChild(win, 0) as FrameworkElement ?? (FrameworkElement)win.Content; // 안쪽 여백(Margin)까지 포함하려고 창의 맨 위 요소를 찍는다
        DocSave(name, DocRender(root, win.Background, 2), 2);
        DocSaveFocus(name, root, focus);
    }

    /// <summary>
    /// 짧은 소개가 다가갈 곳 (사용자 2026-11-22: "중요한 곳에 포커싱하면서 줌인되어야 하는데 기계적으로 중앙에 줌인된다").
    /// 그 장면의 기능을 보여 주는 실제 화면 요소들을 합친 사각형을 그림 안 비율로 &lt;이름&gt;.focus.json 에 적는다. 언어마다 찍으니 위치도 언어마다 맞다.
    /// </summary>
    private void DocSaveFocus(string name, FrameworkElement root, Func<FrameworkElement, IEnumerable<FrameworkElement?>>? focus)
    {
        if (focus is null)
        {
            return;
        }

        Rect? union = null;
        foreach (var e in focus(root))
        {
            if (e is null || !e.IsVisible || e.ActualWidth < 1 || e.ActualHeight < 1)
            {
                continue;
            }

            var r = e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
            r.Intersect(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            if (r.IsEmpty)
            {
                continue;
            }

            union = union is { } u ? Rect.Union(u, r) : r;
        }

        if (union is not { } f)
        {
            _docFailures.Add($"{name}: 짧은 소개 초점 요소를 찾지 못함");
            return;
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var json = string.Format(inv, "{{\"x\":{0:0.####},\"y\":{1:0.####},\"w\":{2:0.####},\"h\":{3:0.####}}}\n",
            f.X / root.ActualWidth, f.Y / root.ActualHeight, f.Width / root.ActualWidth, f.Height / root.ActualHeight);
        File.WriteAllText(Path.Combine(_docOut, name + ".focus.json"), json);
    }

    /// <summary>이름(x:Name)으로 찾기.</summary>
    private static FrameworkElement? DocNamed(DependencyObject root, string name) =>
        DocFind<FrameworkElement>(root).FirstOrDefault(e => e.Name == name);

    /// <summary>보이는 것 중 가장 큰 그 종류의 요소 (목록·트리 등).</summary>
    private static T? DocLargest<T>(DependencyObject root) where T : FrameworkElement =>
        DocFind<T>(root).Where(e => e.IsVisible).OrderByDescending(e => e.ActualWidth * e.ActualHeight).FirstOrDefault();

    /// <summary>글이 그것과 같은(또는 그것으로 시작하는) 단추.</summary>
    private static System.Windows.Controls.Primitives.ButtonBase? DocButton(DependencyObject root, string text) =>
        DocFind<System.Windows.Controls.Primitives.ButtonBase>(root).FirstOrDefault(b => b.IsVisible && DocText(b).StartsWith(text, StringComparison.Ordinal));

    private static string DocText(DependencyObject e) => e switch
    {
        ContentControl { Content: string c } => c,
        TextBlock t => t.Text,
        _ => string.Join(" ", DocFind<TextBlock>(e).Select(t => t.Text)),
    };

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
            await DocGoAsync(right, DocShotsSandbox.Downloads(r), Foldy.Core.Localization.Loc.T("cf.demo.minutes"));
            DocActivate(left);
            await DocShotWindowAsync("main", 1500, root => [DocFind<TextBlock>(root).FirstOrDefault(t => t.Inlines.OfType<System.Windows.Documents.Run>().FirstOrDefault()?.Text == "F5 ") is { } f5 ? DocAncestorButton(f5) : null,
                DocFind<TextBlock>(root).FirstOrDefault(t => t.Inlines.OfType<System.Windows.Documents.Run>().FirstOrDefault()?.Text == "F6 ") is { } f6 ? DocAncestorButton(f6) : null]); // 소개: 두 창 사이로 끌거나 F5 복사·F6 이동
        });

        // UX 점검용: 작은 노트북 화면(1366×768)과 다크 테마
        await DocSceneAsync("review-small", async () =>
        {
            var (w0, h0) = (Width, Height);
            Width = 1366;
            Height = 728;
            await DocShotWindowAsync("review-small", 1200);
            (Width, Height) = (w0, h0);
        });

        await DocSceneAsync("review-large", async () =>
        {
            App.UiScale = 1.3;
            App.ApplyScale(this);
            await DocShotWindowAsync("review-large", 1200);
            App.UiScale = 1.0;
            App.ApplyScale(this);
            await DocSettleAsync(400);
        });

        await DocSceneAsync("review-dark", async () =>
        {
            Foldy.UI.Theming.ThemeManager.SetMode(Foldy.UI.Theming.FoldyTheme.Dark);
            await DocShotWindowAsync("review-dark", 1200);
            Foldy.UI.Theming.ThemeManager.SetMode(Foldy.UI.Theming.FoldyTheme.Light);
            await DocSettleAsync(400);
        });

        await DocSceneAsync("preview-image", async () =>
        {
            DocFolderTab(left);
            await DocGoAsync(right, DocShotsSandbox.Trip(r), Foldy.Core.Localization.Loc.T("demo.p1jpg"), tabIndex: 1);
            DocActivate(right);
            RightPane.TogglePreview();
            await DocShotWindowAsync("preview-image", 1800);
            RightPane.TogglePreview();
        });

        await DocSceneAsync("preview-markdown", async () =>
        {
            await DocGoAsync(left, DocShotsSandbox.Documents(r), Foldy.Core.Localization.Loc.T("demo.guideMd"), tabIndex: 1);
            DocActivate(left);
            LeftPane.TogglePreview();
            await DocShotWindowAsync("preview-markdown", 1800);
            LeftPane.TogglePreview();
        });

        await DocSceneAsync("images-grid", async () =>
        {
            await DocGoAsync(right, DocShotsSandbox.Trip(r), Foldy.Core.Localization.Loc.T("demo.p2jpg"), tabIndex: 1);
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
            var tab = Foldy.Presentation.Shell.ShellViewModel.OpenMarkdown(left, Path.Combine(DocShotsSandbox.Documents(r), Foldy.Core.Localization.Loc.T("demo.guideMd")));
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
            await DocShotWindowAsync("git-changes", 1200, root => [DocNamed(root, "BranchBtn"), DocNamed(root, "PullBtn"), DocNamed(root, "StashBtn")]); // 소개: 받기·올리기·커밋을 버튼으로

            await git.ShowBlameAsync("src/app.py");
            await DocShotWindowAsync("git-blame", 1200);
            git.ClosePanel();

            git.ShowHistoryTab();
            await DocSettleAsync(600);
            var commit = git.Commits.FirstOrDefault(c => c.Node.Subject.Contains(Foldy.Core.Localization.Loc.T("demo.c10find"), StringComparison.Ordinal)) ?? git.Commits.ElementAtOrDefault(2);
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
            var tab = Shell.StartSearch(right, Foldy.Core.Localization.Loc.T("demo.q.report"));
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
            await DocShotWindowAsync("analysis", 2500, root => [DocLargest<Foldy.UI.Analysis.TreemapControl>(root)]); // 소개: 큰 상자가 큰 폴더
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
            var item = vm.Templates.First(t => t.Template.Id == "document-workflow"); // 일반 문서 관리 (사용자 2026-11-21: 개발용 말고)
            vm.SelectedTemplate = item;
            if (vm.Customizer.Variables.FirstOrDefault(v => v.Key == "프로젝트") is { } nameInput) // i18n:skip (템플릿 변수 키)
            {
                nameInput.Value = Foldy.Core.Localization.Loc.T("demo.plan");
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

        // 짧은 소개용 PARA (사용자 2026-11-21: "폴더 관리 기능 영상을 PARA 타입으로"): 템플릿 고르기 → 뒤죽박죽 폴더를 PARA 네 칸으로
        await DocSceneAsync("para", async () =>
        {
            var vm = new Foldy.Presentation.Templates.TemplateDialogViewModel(_docLibrary!, Path.Combine(r, "Projects"));
            var item = vm.Templates.First(t => t.Template.Id == "para");
            vm.SelectedTemplate = item;
            vm.SelectedTab = Foldy.Presentation.Templates.TemplateDialogTab.Preview;
            var win = DocOpen(new Foldy.UI.Templates.TemplatePickerWindow { DataContext = vm });
            foreach (var lb in DocFind<ListBox>(win))
            {
                if (lb.Items.Contains(item))
                {
                    lb.ScrollIntoView(item);
                }
            }

            await DocShotDialogAsync("para-template", win, 1000, root => [DocLargest<TreeView>(root)]); // 소개: 만들어질 폴더 미리보기
            win.Close();

            // 뒤죽박죽 문서함 (가짜 파일): 진행 중인 일·계속 관리하는 일·참고 자료·오래된 것이 섞여 있다
            var messy = Path.Combine(r, Foldy.Core.Localization.Loc.T("demo.inbox"));
            Directory.CreateDirectory(messy);
            foreach (var (name, daysAgo) in new[]
            {
                (Foldy.Core.Localization.Loc.T("demo.messy1"), 3), (Foldy.Core.Localization.Loc.T("demo.messy2"), 5), (Foldy.Core.Localization.Loc.T("demo.messy3"), 1),
                (Foldy.Core.Localization.Loc.T("demo.messy4"), 40), (Foldy.Core.Localization.Loc.T("demo.messy5"), 90), (Foldy.Core.Localization.Loc.T("demo.messy6"), 2),
                (Foldy.Core.Localization.Loc.T("demo.messy7"), 60), (Foldy.Core.Localization.Loc.T("demo.messy8"), 50),
                (Foldy.Core.Localization.Loc.T("demo.messy9"), 700), (Foldy.Core.Localization.Loc.T("demo.messy10"), 900),
            })
            {
                var f = Path.Combine(messy, name);
                File.WriteAllText(f, Foldy.Core.Localization.Loc.T("demo.fake"));
                var t = DateTime.Now.AddDays(-daysAgo);
                File.SetLastWriteTime(f, t);
                File.SetLastAccessTime(f, t);
                File.SetCreationTime(f, t);
            }

            var org = new Foldy.Presentation.Templates.OrganizeViewModel(messy, new WpfDispatcher(Dispatcher), templates: _docLibrary!.Templates, journalFolder: Path.Combine(App.DataDir, "jobs"));
            org.SelectedMode = org.Modes.First(m => m.Mode == Foldy.Core.Organize.OrganizeMode.ByTemplate);
            org.SelectedTemplate = org.Templates.FirstOrDefault(t => t.Id == "para") ?? org.SelectedTemplate;
            var win2 = DocOpen(new Foldy.UI.Templates.OrganizeWindow { DataContext = org });
            await org.PlanCommand.ExecuteAsync(null);
            await DocShotDialogAsync("para-organize", win2, 1200, root => [DocLargest<ItemsControl>(root)]); // 소개: 어디로 옮길지 계획
            win2.Close();
        });

        await DocSceneAsync("wizard", async () =>
        {
            var vm = new Foldy.Presentation.Templates.FolderWizardViewModel(Foldy.Core.Templates.TemplateLibrary.LoadWizard(), _docLibrary!, Path.Combine(r, "Projects"));
            var win = DocOpen(new Foldy.UI.Templates.FolderWizardWindow { DataContext = vm });
            vm.SelectPurpose("work");
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
            var job = new JobViewModel(Foldy.Core.Localization.Loc.T("demo.copyJob"), new WpfDispatcher(Dispatcher)) { MascotKind = Foldy.Presentation.Mascot.JobKind.Copy };
            job.Detail = Foldy.Core.Localization.Loc.T("demo.copyDetail");
            Shell.Jobs.Insert(0, job);
            job.Report(new FileOpProgress(450, 1000, Foldy.Core.Localization.Loc.T("demo.p1jpg")));
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
            // 화면이 작은 PC(CI 러너 등)에서도 그림 밖으로 나가지 않게 자른다
            var cx = Math.Min(380 * 2, Math.Max(0, full.PixelWidth - 2));
            var cy = Math.Clamp((int)(origin.Y * 2), 0, Math.Max(0, full.PixelHeight - 2));
            var crop = new CroppedBitmap(full, new Int32Rect(
                cx, cy, Math.Max(1, Math.Min(920 * 2, full.PixelWidth - cx)), Math.Max(1, Math.Min((int)(JobsPanel.ActualHeight * 2), full.PixelHeight - cy))));
            DocSave("jobs-mascot", crop, 1);
            Shell.Jobs.Remove(job);
        });

        await DocSceneAsync("palette", async () =>
        {
            DocActivate(left);
            ShowPalette();
            PaletteInput.Text = Foldy.Core.Localization.Loc.T("sw.qs.folder");
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
            var g = (GuideWindow)DocOpen(new GuideWindow(Foldy.Core.Localization.Loc.T("sw.c.desktop")) { Height = 520 });
            g.Say(Foldy.Core.Localization.Loc.T("cf.dt.hello"));
            g.Say(Foldy.Core.Localization.Loc.T("demo.dtFound"));
            _ = g.AskAsync(Foldy.Core.Localization.Loc.T("demo.dtPlan"),
                ("go", Foldy.Core.Localization.Loc.T("cf.log.go")), ("detail", Foldy.Core.Localization.Loc.T("cf.details")), ("later", Foldy.Core.Localization.Loc.T("sw.later")));
            await DocShotDialogAsync("guide", g, 500, root => [DocNamed(root, "Messages") is Panel m && m.Children.Count > 0 ? m.Children[^1] as FrameworkElement : null, DocNamed(root, "Choices")]); // 소개: 묻는 말과 단추
            g.Close();
        });

        await DocSceneAsync("memo", async () =>
        {
            var note = new Foldy.Core.Vault.LockedNote { Markdown = Foldy.Core.Localization.Loc.T("demo.lockedNote") };
            var path = Path.Combine(r, Foldy.Core.Localization.Loc.T("demo.lockedName") + Foldy.Core.Vault.SecureZip.MemoSuffix);
            var m = (MemoWindow)DocOpen(new MemoWindow(path, note, "doc-shot"));
            await DocShotDialogAsync("memo", m, 900, root => [DocNamed(root, "Editor"), DocNamed(root, "LockIcon")]); // 소개: 잠긴 메모의 글
            m.Close();
        });

        await DocSceneAsync("setup", async () =>
        {
            var g = (GuideWindow)DocOpen(new GuideWindow(Foldy.Core.Localization.Loc.T("cf.fr.title")) { Width = 720, Height = 640, FontSize = 18, EchoChoices = false });
            g.Say(Foldy.Core.Localization.Loc.T("demo.hello"));
            foreach (var (label, scale) in new[] { (Foldy.Core.Localization.Loc.T("cf.fr.normal"), 1.0), (Foldy.Core.Localization.Loc.T("cf.fr.large"), 1.15), (Foldy.Core.Localization.Loc.T("cf.fr.larger"), 1.3), (Foldy.Core.Localization.Loc.T("cf.fr.largest"), 1.5) })
            {
                g.ShowContent(FirstRunTour.SampleList(label, scale));
            }

            _ = g.AskAsync(Foldy.Core.Localization.Loc.T("cf.fr.size"), ("1.0", Foldy.Core.Localization.Loc.T("cf.fr.normal")), ("1.15", Foldy.Core.Localization.Loc.T("cf.fr.large")), ("1.3", Foldy.Core.Localization.Loc.T("cf.fr.larger")), ("1.5", Foldy.Core.Localization.Loc.T("cf.fr.largest")), ("next", Foldy.Core.Localization.Loc.T("cf.fr.ok")));
            await DocShotDialogAsync("setup", g, 600);
            g.Close();
        });

        // 업데이트 진행 창에 쓰는 농구 그림: 미리 그린 장면이 실제로 만들어지는지 (CI 화면 점검에서도 확인)
        await DocSceneAsync("update-anim", async () =>
        {
            var dir = UpdateProgressWindow.RenderFrames() ?? throw new InvalidOperationException("업데이트 진행 창 그림을 만들지 못함");
            foreach (var (from, to) in new[] { ("work_120.png", "update-anim-work.png"), ("done_024.png", "update-anim-done.png") })
            {
                var src = Path.Combine(dir, from);
                if (!File.Exists(src))
                {
                    throw new FileNotFoundException("업데이트 진행 창 그림이 없음", src);
                }

                File.Copy(src, Path.Combine(_docOut, to), overwrite: true);
            }

            await Task.CompletedTask;
        });

        // Foldy 확인 창 (Windows 기본 메시지 상자 대신, 테마를 따른다)
        await DocSceneAsync("dialog", async () =>
        {
            var d = DocOpen(Foldy.UI.Dialogs.FoldyDialog.Preview(Foldy.Core.Localization.Loc.T("sw.bin.empty"), Foldy.Core.Localization.Loc.T("demo.binAsk"),
                Foldy.UI.Dialogs.DialogKind.Danger, [Foldy.Core.Localization.Loc.T("sw.bin.emptyYes"), Foldy.Core.Localization.Loc.T("dlg.cancel")], 1));
            await DocShotDialogAsync("dialog", d, 500);
            d.Close();
        });

        // AI 연결 (MCP): 문서용이라 가짜 경로·가짜 앱 상태 (사용자 PC의 경로가 나오지 않게, 설정 파일도 건드리지 않는다)
        await DocSceneAsync("mcp", async () =>
        {
            Foldy.Core.Mcp.McpClientApp Fake(string name, Foldy.Core.Mcp.McpConfigFormat f, bool installed) => new(name, $@"C:\Users\me\{name}.json", f, installed);
            var apps = new[]
            {
                Fake("Claude Desktop", Foldy.Core.Mcp.McpConfigFormat.McpServers, true),
                Fake("Claude Code", Foldy.Core.Mcp.McpConfigFormat.McpServersStdio, true),
                Fake("Codex", Foldy.Core.Mcp.McpConfigFormat.CodexToml, true),
                Fake("Gemini CLI", Foldy.Core.Mcp.McpConfigFormat.McpServers, false),
            };
            var w = (McpWindow)DocOpen(new McpWindow(apps, @"C:\Program Files\Foldy\Foldy.exe", () => true, _ => { }, a => a.Name == "Claude Desktop", preview: true) { Height = 900 });
            await DocShotDialogAsync("mcp", w, 600, root => [DocButton(root, Foldy.Core.Localization.Loc.T("mcp.connect")) ?? DocButton(root, Foldy.Core.Localization.Loc.T("mcp.apps"))]); // 소개: [AI에 연결] 한 번

            // 화면 점검: 다크·레트로에서도 글자가 보이는지 (2026-11-21: 다크에서 체크 상자 글자가 검정이라 안 보였다)
            foreach (var (theme, name) in new[] { (Foldy.UI.Theming.FoldyTheme.Dark, "review-mcp-dark"), (Foldy.UI.Theming.FoldyTheme.Retro, "review-mcp-retro") })
            {
                Foldy.UI.Theming.ThemeManager.SetMode(theme);
                await DocShotDialogAsync(name, w, 800);
            }

            Foldy.UI.Theming.ThemeManager.SetMode(Foldy.UI.Theming.FoldyTheme.Light);
            w.Close();
        });

        await DocSceneAsync("bigdelete", async () =>
        {
            var g = (GuideWindow)DocOpen(new GuideWindow(Services.DeleteFlows.Title));
            _ = Services.DeleteFlows.RunAsync(g, new Foldy.Presentation.Services.BigDeleteQuestion(
                [DemoProj + @"\node_modules"], new Foldy.Core.FileSystem.DeleteCount(182_406, 1_240_000_000, false), true));
            await DocShotDialogAsync("bigdelete", g, 500);
            g.Close();
        });

        // 지워도 되는 폴더 찾기 (가짜 경로·크기: 문서용)
        await DocSceneAsync("rebuildable", async () =>
        {
            var g = (GuideWindow)DocOpen(new GuideWindow(Foldy.Core.Localization.Loc.T("dev.rb.title")) { Width = 760, Height = 620 });
            Foldy.Core.Cleanup.RebuildableKind K(string n) => Foldy.Core.Cleanup.RebuildableFolders.Defaults.First(k => k.Name == n);
            var now = DateTime.UtcNow;
            var items = new[]
            {
                new Foldy.Core.Cleanup.RebuildableFolder(DemoProj + @"\" + Foldy.Core.Localization.Loc.T("demo.shop") + @"\node_modules", K("node_modules"), 1_840_000_000, now.AddDays(-120), true, null),
                new Foldy.Core.Cleanup.RebuildableFolder(DemoProj + @"\" + Foldy.Core.Localization.Loc.T("demo.tools") + @"\bin", K("bin"), 620_000_000, now.AddDays(-200), true, null),
                new Foldy.Core.Cleanup.RebuildableFolder(DemoProj + @"\" + Foldy.Core.Localization.Loc.T("demo.backup2024") + @"\.git", K(".git"), 410_000_000, now.AddDays(-400), false, Foldy.Core.Localization.Loc.T("rb.gitWarn")),
                new Foldy.Core.Cleanup.RebuildableFolder(DemoProj + @"\" + Foldy.Core.Localization.Loc.T("demo.backup2024") + @"\__history", K("__history"), 96_000_000, now.AddDays(-400), true, null),
                new Foldy.Core.Cleanup.RebuildableFolder(DemoProj + @"\" + Foldy.Core.Localization.Loc.T("demo.shop") + @"\.git", K(".git"), 52_000_000, now.AddDays(-1), false, Foldy.Core.Localization.Loc.T("demo.gitRecent1")),
            };
            g.Say(Foldy.Core.Localization.Loc.F("dev.rb.found", 5, "2.8 GB"));
            _ = g.PickAsync(Foldy.Core.Localization.Loc.T("dev.rb.pick"),
                items.Select(f => (f, $"{Foldy.Presentation.Files.FileItem.FormatBytes(f.Size)}  ·  {Path.GetRelativePath(DemoProj, f.Path)}  ·  {f.Kind.Why}", f.Selected)), f => f.Warning,
                ("recycle", Foldy.Core.Localization.Loc.T("ops.toRecycle")), ("skip", Foldy.Core.Localization.Loc.T("cf.keep")));
            await DocShotDialogAsync("rebuildable", g, 600, root => [DocNamed(root, "Messages") is Panel m && m.Children.Count > 0 ? m.Children[^1] as FrameworkElement : null]); // 소개: 지워도 되는 폴더 목록
            g.Close();
        });

        // 빠른 검색 (Ctrl+Space): 바로 찾는 것(기능·설정), 파일(냄새 잘 맡는 개), 계산·단위 변환
        foreach (var (scene, query, wait) in new[] { ("quicksearch", Foldy.Core.Localization.Loc.T("demo.q.report"), 1500), ("quicksearch-settings", Foldy.Core.Localization.Loc.T("demo.q.disp"), 600), ("quicksearch-calc", Foldy.Core.Localization.Loc.T("demo.q.calc"), 600), ("quicksearch-talk", Foldy.Core.Localization.Loc.T("demo.q.talk"), 900) })
        {
            await DocSceneAsync(scene, async () =>
            {
                _startApps ??= [];
                var qs = BuildQuickSearch(Shell.ActivePane.ActiveFolder?.Path, Foldy.Core.Localization.Loc.T("demo.qsFooter"));
                qs.KeepOpen = true;
                ThemeManager.Attach(qs);
                qs.Show();
                qs.SetQuery(query);
                await DocShotDialogAsync(scene, qs, wait, root => [qs.TopResult]); // 소개: 맨 위 가장 알맞은 결과
                qs.KeepOpen = false;
                qs.Close();
            });
        }

        await DocSceneAsync("tour", async () =>
        {
            // 쇼츠처럼 보는 짧은 소개: 두 번째 장면(Ctrl+Space)을 중간쯤에서
            var g = (ShortsWindow)DocOpen(new ShortsWindow());
            await DocSettleAsync(300);
            for (var i = ShortsWindow.Scenes.Length; i >= 0; i--)
            {
                g.Go(i); // 장면마다 (화면 점검용 tour-0 … tour-N, 문서에는 tour = 두 번째 장면)
                await DocShotDialogAsync(i == 1 ? "tour" : $"tour-{i}", g, 2500);
            }

            g.Close();
        });

        await DocSceneAsync("about", async () =>
        {
            var win = DocOpen(new AboutWindow());
            await DocShotDialogAsync("about", win, 500);
            win.Close();
        });
    }

    /// <summary>요소를 품은 단추.</summary>
    private static FrameworkElement? DocAncestorButton(DependencyObject e)
    {
        for (var p = e; p is not null; p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
        {
            if (p is System.Windows.Controls.Primitives.ButtonBase b)
            {
                return b;
            }
        }

        return e as FrameworkElement;
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
