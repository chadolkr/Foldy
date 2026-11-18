using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using Foldy.App.Services;
using Foldy.Copy.Native;
using Foldy.Core.Threading;
using Foldy.Indexer.Client;
using Foldy.Platform.Shell;
using Foldy.Presentation.Jobs;
using Foldy.Presentation.Panes;
using Foldy.Presentation.Persistence;
using Foldy.Presentation.Services;
using Foldy.Presentation.Shell;
using Foldy.Presentation.Tabs;
using Foldy.Presentation.Tree;
using Foldy.Providers.Local;
using Foldy.UI.Shell;
using Foldy.UI.Theming;
using Foldy.UI.Views;
using Serilog;

namespace Foldy.App;

/// <summary>
/// 메인 창: 좌우 패널 + 작업 패널 + 기능키 줄 + 상태바 (DESIGN.md 화면 구조). 조립 루트로서 서비스를 만들고 잇는다.
/// </summary>
public partial class ShellWindow : Window, IPaneHost
{
    private readonly DriveQueueRegistry _queues;
    private readonly LocalProvider _provider;
    private readonly TabEnvironment _env;
    private readonly DriveService _drives;
    private readonly string _sessionPath = Path.Combine(App.DataDir, "session.json");
    private readonly string _settingsPath = Path.Combine(App.DataDir, "settings.json");
    private readonly List<PaletteCommand> _commands = [];
    private IndexerClient? _indexer;
    private readonly SftpSessions _sftp;
    private readonly Foldy.Git.GitService _git = new();
    private Foldy.Core.Templates.TemplateLibrary? _templates;
    private Foldy.Core.Descriptions.FolderDescriptionService? _descriptions;
    private readonly FileSearchService _search;
    private readonly AppUpdater _updater;
    private AppSettings _settings;
    private HwndSource? _hwndSource;

    public ShellWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Migrate(JsonFileStore.Load(_settingsPath, () => new AppSettings()));
        _queues = new DriveQueueRegistry(nativeIo: new NativeIoCancel(Foldy.Platform.SystemInfo.Drives.CurrentNativeThreadId, id => Foldy.Platform.SystemInfo.Drives.CancelSynchronousIo(id)));
        _provider = new LocalProvider(_queues);
        _drives = new DriveService(_queues);
        var ui = new WpfDispatcher(Dispatcher);
        _sftp = new SftpSessions();
        _env = new TabEnvironment(new ProviderRouter(_provider, _sftp.Provider), ui)
        {
            TypeName = TypeNames.Get,
            Watcher = new FolderWatchService(),
            ShowHidden = () => _settings.ShowHidden,
            Git = new GitStatusService(_git),
            FolderSizes = new IndexerFolderSizes(() => _indexer),
        };
        Shell = new ShellViewModel(_env, new ShellServices(
            new NativeCopyFileOperationService(
                new ShellFileOperationService(() => OwnerHandle),
                options: new NativeCopyFileOperationOptions { ResolveConflict = ConflictWindow.AskAsync }),
            new ShellOpenFileService(),
            new TerminalService(() => _settings, (p, sudo) => _sftp.OpenTerminal(p, sudo))))
        {
            Settings = _settings,
        };

        _updater = new AppUpdater(typeof(ShellWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        _updater.Changed += () => Dispatcher.BeginInvoke(UpdateUpdateButton);
        _search = new FileSearchService(() => _indexer, () => _settings.ShowHidden);
        Shell.Search = _search;
        Title = $"Foldy {typeof(ShellWindow).Assembly.GetName().Version?.ToString(3)}";
        ApplyDensity();
        ThemeManager.Attach(this);

        var session = JsonFileStore.Load(_sessionPath, () => new SessionState());
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        RestorePlacement(session);
        LeftPane.Attach(this, Shell.Left);
        RightPane.Attach(this, Shell.Right);
        RightPane.TreeVisible = false; // 두 패널 모두 트리를 두면 목록이 좁아진다 (Ctrl+B로 전환)
        Shell.Restore(session, home);
        LeftPane.SelectionInfoChanged += (_, _) => UpdateSelectionText();
        RightPane.SelectionInfoChanged += (_, _) => UpdateSelectionText();

        Shell.PropertyChanged += OnShellChanged;
        Shell.SaveRequested += s => SaveSession(s);
        Shell.PaneActivated += p => UpdateSelectionText();
        Shell.Jobs.CollectionChanged += OnJobsChanged;
        JobsList.ItemsSource = Shell.Jobs;

        BuildCommands();
        PreviewKeyDown += OnWindowKeyDown;
        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += (_, _) =>
        {
            _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwndSource?.AddHook(WndProc);
        };
    }

    public ShellViewModel Shell { get; }

    public IntPtr OwnerHandle => new WindowInteropHelper(this).Handle;

    public ShellContextMenu ShellMenu { get; } = new();

    public IconCache Icons { get; } = new();

    public FolderTreeViewModel CreateTree() => new(_provider, _drives.GetDrivesFast());

    public FrameworkElement? CreateTabView(TabBase tab, PaneViewModel pane) => tab switch
    {
        MarkdownTabViewModel md => CreateDocumentView(md, pane),
        Foldy.Presentation.Git.GitTabViewModel git => new Foldy.UI.Git.GitView { DataContext = git },
        _ => null,
    };

    public void OpenGit(PaneViewModel pane, string repoRoot)
    {
        var existing = pane.Tabs.OfType<Foldy.Presentation.Git.GitTabViewModel>().FirstOrDefault(t => string.Equals(t.RepoRoot, repoRoot, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            pane.ActiveTab = existing;
            return;
        }

        var vm = new Foldy.Presentation.Git.GitTabViewModel(_git, repoRoot);
        pane.AddTab(vm, activate: true, index: pane.ActiveTab is null ? null : pane.Tabs.IndexOf(pane.ActiveTab) + 1, assignColor: true);
    }

    private static Foldy.UI.Markdown.MarkdownView CreateDocumentView(MarkdownTabViewModel tab, PaneViewModel pane)
    {
        var view = new Foldy.UI.Markdown.MarkdownView { DataContext = tab };
        view.FolderLinkRequested += (_, e) =>
        {
            pane.NewFolderTab(e.Path);
            e.Handled = true;
        };
        return view;
    }

    private PaneView ActiveView => Shell.ActivePane == Shell.Left ? LeftPane : RightPane;

    // ───────────── 시작·종료 ─────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ActiveView.FocusList();
        if (App.Options.OpenPath is { } open && Shell.ActivePane.ActiveFolder is { } tab)
        {
            _ = tab.NavigateAsync(open);
        }

        RefreshDrives();
        if (!App.Options.IsBench && App.Options.Screenshot is null)
        {
            _updater.Start(TimeSpan.FromMinutes(1)); // 실행 1분 뒤 + 6시간마다 (설계서 10.5)
        }

        await ProbeEnginesAsync();
        if (App.Options.Screenshot is { } shot)
        {
            await Task.Delay(2500);
            SaveScreenshot(shot);
            Application.Current.Shutdown(0);
        }
    }

    /// <summary>검증용 화면 캡처 (--screenshot 경로).</summary>
    private void SaveScreenshot(string path)
    {
        var root = (FrameworkElement)Content;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        var bg = new System.Windows.Media.DrawingVisual();
        using (var dc = bg.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }

        bmp.Render(bg);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private void RefreshDrives()
    {
        var fast = _drives.GetDrivesFast();
        Shell.Left.SetDrives(fast);
        Shell.Right.SetDrives(fast);
        foreach (var d in fast)
        {
            _ = FillDriveAsync(d);
        }
    }

    private async Task FillDriveAsync(DriveItem d)
    {
        var full = await _drives.FillDetailsAsync(d, CancellationToken.None);
        Shell.Left.UpdateDrive(full);
        Shell.Right.UpdateDrive(full);
    }

    private async Task ProbeEnginesAsync()
    {
        var (available, reason) = await Task.Run(() => (NativeCopyEngine.IsAvailable, NativeCopyEngine.UnavailableReason));
        CopyEngineText.Text = available ? "복사 엔진: Rust" : "복사 엔진: 기본";
        CopyEngineText.ToolTip = available ? "foldy_copy.dll 사용 가능 (M3에서 병렬 복사로 전환)" : reason;
        _indexer = await IndexerClient.TryConnectAsync(timeout: TimeSpan.FromMilliseconds(300));
        await _search.RefreshStateAsync();
        if (_indexer is not null)
        {
            _ = Shell.Left.ActiveFolder?.RefreshFolderSizesAsync();
            _ = Shell.Right.ActiveFolder?.RefreshFolderSizesAsync();
        }

        IndexerText.Text = _indexer is null ? "검색: 폴더 훑기 (색인 서비스 없음)" : _search.IsIndexed ? "검색: 색인 사용" : "검색: 색인 준비 중";
        IndexerText.ToolTip = "색인 서비스(foldy-index)가 관리자 권한으로 실행 중이면 전체 드라이브를 즉시 검색합니다.";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveSession(Shell.Capture(null, double.NaN));
        foreach (var t in Shell.Left.Tabs.Concat(Shell.Right.Tabs))
        {
            t.Close();
        }

        _updater.Dispose();
        _queues.DisposeAsync().AsTask().Wait(500);
        if (_indexer is not null)
        {
            _indexer.DisposeAsync().AsTask().Wait(200);
        }
    }

    private void SaveSession(SessionState s)
    {
        try
        {
            var placement = RestoreBounds.IsEmpty
                ? new WindowPlacement(Left, Top, Width, Height, WindowState == WindowState.Maximized)
                : new WindowPlacement(RestoreBounds.Left, RestoreBounds.Top, RestoreBounds.Width, RestoreBounds.Height, WindowState == WindowState.Maximized);
            var total = LeftColumn.ActualWidth + RightColumn.ActualWidth;
            var split = total > 0 ? LeftColumn.ActualWidth / total : 0.5;
            JsonFileStore.Save(_sessionPath, s with { Window = placement, SplitRatio = split });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "세션 저장 실패");
        }
    }

    private void RestorePlacement(SessionState s)
    {
        if (s.Window is { } w && w.Width > 300 && w.Height > 200)
        {
            // 화면 밖이면 기본 위치
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (vs.IntersectsWith(new Rect(w.Left, w.Top, w.Width, w.Height)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = w.Left;
                Top = w.Top;
                Width = w.Width;
                Height = w.Height;
            }

            if (w.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        var ratio = Math.Clamp(double.IsNaN(s.SplitRatio) ? 0.5 : s.SplitRatio, 0.2, 0.8);
        LeftColumn.Width = new GridLength(ratio, GridUnitType.Star);
        RightColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
    }

    private void SaveSettings()
    {
        Shell.Settings = _settings;
        try
        {
            JsonFileStore.Save(_settingsPath, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "설정 저장 실패");
        }
    }

    // ───────────── 창 메시지 (셸 메뉴 하위 메뉴, 드라이브 변경) ─────────────

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x0219;
        if (ShellMenu.HandleWindowMessage(msg, wParam, lParam, out var result))
        {
            handled = true;
            return result;
        }

        if (msg == WM_DEVICECHANGE)
        {
            Dispatcher.BeginInvoke(RefreshDrives);
        }

        return IntPtr.Zero;
    }

    // ───────────── 상태 표시 ─────────────

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Status))
        {
            StatusText.Text = Shell.Status;
        }
        else if (e.PropertyName == nameof(ShellViewModel.ActivePane))
        {
            UpdateSelectionText();
        }
    }

    private void UpdateSelectionText() => SelectionText.Text = ActiveView.SelectionSummary();

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (JobViewModel j in e.NewItems)
            {
                j.PropertyChanged += (_, _) => UpdateTaskbar();
            }
        }

        JobsPanel.Visibility = Shell.Jobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTaskbar();
    }

    /// <summary>작업표시줄 진행률 (설계서 2장): 진행 파랑, 오류 빨강, 끝나면 없음.</summary>
    private void UpdateTaskbar()
    {
        var running = Shell.Jobs.Where(j => !j.IsFinished).ToList();
        JobsHeader.Text = running.Count > 0 ? $"작업 {running.Count}개 진행 중" : "작업";
        if (running.Count == 0)
        {
            Taskbar.ProgressState = Shell.Jobs.Any(j => j.State == JobState.Failed) && !IsActive ? TaskbarItemProgressState.Error : TaskbarItemProgressState.None;
            return;
        }

        var known = running.Where(j => j.Progress >= 0).ToList();
        if (known.Count == 0 || known.All(j => j.Progress == 0))
        {
            Taskbar.ProgressState = TaskbarItemProgressState.Indeterminate;
        }
        else
        {
            Taskbar.ProgressState = TaskbarItemProgressState.Normal;
            Taskbar.ProgressValue = known.Average(j => j.Progress);
        }
    }

    private void OnCancelJob(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: JobViewModel j })
        {
            j.Cancel();
        }
    }

    private void OnClearJobs(object sender, RoutedEventArgs e) => Shell.ClearFinishedJobs();

    // ───────────── 명령 ─────────────

    private sealed record PaletteCommand(string Id, string Title, string Gesture, Action Run, Key Key = Key.None, ModifierKeys Mods = ModifierKeys.None, bool AllowInText = false);

    private void BuildCommands()
    {
        void Add(string id, string title, Action run, Key key = Key.None, ModifierKeys mods = ModifierKeys.None, string? gesture = null, bool inText = false) =>
            _commands.Add(new PaletteCommand(id, title, gesture ?? Gesture(key, mods), run, key, mods, inText));

        var activeTab = () => Shell.ActivePane.ActiveFolder;
        Add("file.copy", "반대 패널로 복사", () => _ = Shell.CopyToOtherAsync(), Key.F5);
        Add("file.move", "반대 패널로 이동", () => _ = Shell.MoveToOtherAsync(), Key.F6);
        Add("file.newFolder", "새 폴더", NewFolder, Key.F7);
        Add("folder.fromTemplate", "템플릿으로 새 폴더…", NewFromTemplate, Key.N, ModifierKeys.Control | ModifierKeys.Shift);
        Add("folder.wizard", "폴더 위저드…", OpenWizard);
        Add("folder.organize", "이 폴더 정리하기…", () => OpenOrganize(Shell.ActivePane.ActiveFolder?.Path));
        Add("folder.description", "폴더 설명 편집…", EditDescription);
        Add("view.images", "이미지만 보기 켜기/끄기", () => ActiveView.ToggleImages(), Key.I, ModifierKeys.Control | ModifierKeys.Shift);
        Add("git.open", "Git 뷰 열기", () => { if (Shell.ActivePane.ActiveFolder?.Git is { } g) OpenGit(Shell.ActivePane, g.RepoRoot); else Shell.Status = "Git 저장소가 아닙니다"; });
        Add("file.rename", "이름 바꾸기", () => ActiveView.BeginRename(), gesture: "F2");
        Add("file.delete", "휴지통으로 삭제", () => _ = Shell.DeleteSelectionAsync(false), gesture: "Del");
        Add("file.deletePermanent", "영구 삭제", () => _ = Shell.DeleteSelectionAsync(true), gesture: "Shift+Del");
        Add("pane.switch", "다른 패널로 이동", () => { Shell.SwitchPane(); ActiveView.FocusList(); }, Key.Tab);
        Add("tab.new", "새 탭", () => ActiveView.NewTab(), Key.T, ModifierKeys.Control);
        Add("tab.close", "탭 닫기", () => { if (Shell.ActivePane.ActiveTab is { } t) Shell.ActivePane.CloseTab(t); }, Key.W, ModifierKeys.Control);
        Add("tab.next", "다음 탭", () => Shell.ActivePane.ActivateRelative(1), Key.Tab, ModifierKeys.Control);
        Add("tab.prev", "이전 탭", () => Shell.ActivePane.ActivateRelative(-1), Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);
        Add("search.focus", "파일 이름 검색", () => ActiveView.FocusSearch(), Key.F, ModifierKeys.Control, inText: true);
        Add("nav.path", "경로 입력", () => ActiveView.BeginPathEdit(), Key.L, ModifierKeys.Control, inText: true);
        Add("nav.refresh", "새로 고침", () => _ = activeTab()?.RefreshAsync(), Key.R, ModifierKeys.Control);
        Add("nav.back", "뒤로", () => _ = activeTab()?.GoBackAsync(), Key.Left, ModifierKeys.Alt);
        Add("nav.forward", "앞으로", () => _ = activeTab()?.GoForwardAsync(), Key.Right, ModifierKeys.Alt);
        Add("nav.up", "위로", () => _ = activeTab()?.GoUpAsync(), Key.Up, ModifierKeys.Alt);
        Add("terminal.open", "터미널 열기 (기본 셸)", () => Shell.OpenTerminal(TerminalKind.Default, false), Key.Oem3, ModifierKeys.Control, "Ctrl+`");
        Add("terminal.admin", "관리자 터미널 열기", () => Shell.OpenTerminal(TerminalKind.Default, true), Key.Oem3, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+`");
        Add("terminal.cmd", "명령 프롬프트 열기", () => Shell.OpenTerminal(TerminalKind.Cmd, false));
        Add("terminal.cmdAdmin", "관리자 명령 프롬프트 열기", () => Shell.OpenTerminal(TerminalKind.Cmd, true));
        Add("view.hidden", "숨김 파일 보기 켜기/끄기", ToggleHidden, Key.H, ModifierKeys.Control);
        Add("view.tree", "폴더 트리 보이기/숨기기", () => ActiveView.TreeVisible = !ActiveView.TreeVisible, Key.B, ModifierKeys.Control);
        Add("view.themeSystem", "테마: 시스템 설정 따르기", () => SetTheme(FoldyTheme.System));
        Add("view.themeLight", "테마: 라이트", () => SetTheme(FoldyTheme.Light));
        Add("view.themeDark", "테마: 다크", () => SetTheme(FoldyTheme.Dark));
        Add("view.themeRetro", "테마: 레트로", () => SetTheme(FoldyTheme.Retro));
        Add("sftp.connect", "SFTP 서버에 연결…", ConnectSftp, Key.O, ModifierKeys.Control | ModifierKeys.Shift);
        Add("indexer.install", "색인 서비스 설치 (관리자, 전체 드라이브 즉시 검색)", () => RunIndexerSetup(install: true));
        Add("indexer.uninstall", "색인 서비스 제거 (관리자)", () => RunIndexerSetup(install: false));
        Add("app.palette", "모든 명령 보기", ShowPalette, Key.P, ModifierKeys.Control | ModifierKeys.Shift, inText: true);
        Add("app.help", "도움말 (사용 설명서)", ShowHelp, Key.F1, inText: true);
        Add("app.about", "Foldy 정보", ShowAbout);
        Add("app.checkUpdate", "업데이트 확인", CheckUpdateNow);
    }

    private static string Gesture(Key key, ModifierKeys mods)
    {
        if (key == Key.None)
        {
            return "";
        }

        var parts = new List<string>();
        if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
        parts.Add(key switch { Key.Oem3 => "`", Key.Left => "←", Key.Right => "→", Key.Up => "↑", _ => key.ToString() });
        return string.Join("+", parts);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var inText = Keyboard.FocusedElement is TextBox;
        foreach (var c in _commands)
        {
            if (c.Key == key && c.Mods == mods && (!inText || c.AllowInText))
            {
                c.Run();
                e.Handled = true;
                return;
            }
        }
    }

    private async void NewFolder()
    {
        if (Shell.ActivePane.ActiveFolder is { } tab && await Shell.NewFolderAsync(tab) is not null)
        {
            ActiveView.BeginRename(); // 새 폴더에 포커스가 간 뒤 바로 이름 입력
        }
    }

    private Foldy.Core.Templates.TemplateLibrary Templates => _templates ??= new Foldy.Core.Templates.TemplateLibrary().Reload();

    private async void NewFromTemplate()
    {
        if (Shell.ActivePane.ActiveFolder is not { } tab || Foldy.Presentation.Navigation.PathUtil.IsSftp(tab.Path))
        {
            return;
        }

        var vm = new Foldy.Presentation.Templates.TemplateDialogViewModel(Templates, tab.Path);
        if (Foldy.UI.Templates.TemplateDialogs.ShowTemplatePicker(this, vm) == true && vm.CreatedTopFolder is { } top)
        {
            await tab.NavigateAsync(top);
        }
    }

    private void OpenWizard()
    {
        if (Shell.ActivePane.ActiveFolder is not { } tab || Foldy.Presentation.Navigation.PathUtil.IsSftp(tab.Path))
        {
            return;
        }

        var wiz = new Foldy.Presentation.Templates.FolderWizardViewModel(Foldy.Core.Templates.TemplateLibrary.LoadWizard(), Templates, tab.Path);
        wiz.OrganizeRequested += (_, folder) => Dispatcher.BeginInvoke(() => OpenOrganize(folder));
        Foldy.UI.Templates.TemplateDialogs.ShowFolderWizard(this, wiz);
        if (wiz.Result?.TopFolder is { } top)
        {
            _ = tab.NavigateAsync(top);
        }
        else
        {
            _ = tab.RefreshAsync();
        }
    }

    private void OpenOrganize(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || Foldy.Presentation.Navigation.PathUtil.IsSftp(folder))
        {
            return;
        }

        var org = new Foldy.Presentation.Templates.OrganizeViewModel(folder, new WpfDispatcher(Dispatcher), templates: Templates.Templates);
        Foldy.UI.Templates.TemplateDialogs.ShowOrganize(this, org);
        _ = Shell.RefreshFoldersAsync([folder]);
    }

    private void EditDescription()
    {
        if (Shell.ActivePane.ActiveFolder is not { } tab || Foldy.Presentation.Navigation.PathUtil.IsSftp(tab.Path))
        {
            return;
        }

        _descriptions ??= new Foldy.Core.Descriptions.FolderDescriptionService(new Foldy.Core.Descriptions.JsonFolderNoteStore(Path.Combine(App.DataDir, "folder_notes.json")));
        var ed = new Foldy.Presentation.Templates.DescriptionEditorViewModel(_descriptions, tab.Path);
        ed.Load();
        Foldy.UI.Templates.TemplateDialogs.ShowDescriptionEditor(this, ed);
    }

    private async void CheckUpdateNow()
    {
        Shell.Status = "업데이트 확인 중…";
        var found = await _updater.CheckAsync(CancellationToken.None);
        Shell.Status = found || _updater.ReadyVersion is not null
            ? $"Foldy {_updater.ReadyVersion} 업데이트가 준비됐습니다"
            : _updater.LastError is { } err ? $"업데이트를 확인하지 못했습니다: {err}" : "최신 버전입니다";
    }

    private void UpdateUpdateButton()
    {
        if (_updater.ReadyVersion is { } v)
        {
            UpdateButton.Content = $"Foldy {v} · 재시작하고 업데이트";
            UpdateButton.Visibility = Visibility.Visible;
        }
    }

    private void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        var running = Shell.Jobs.Any(j => !j.IsFinished);
        if (running && MessageBox.Show(this, "진행 중인 작업이 있습니다. 그래도 지금 업데이트할까요?", "Foldy 업데이트", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        if (_updater.LaunchInstaller())
        {
            Close(); // 세션 저장 후 종료, 설치 스크립트가 끝나면 Foldy를 다시 띄운다
        }
        else
        {
            Shell.Status = "업데이트 설치가 취소됐습니다 (관리자 권한 필요)";
        }
    }

    private async void RunIndexerSetup(bool install)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "foldy-index.exe");
        if (install && !File.Exists(exe))
        {
            Shell.Status = "foldy-index.exe가 없습니다 (설치본에서 실행하세요)";
            return;
        }

        try
        {
            using var p = Process.Start(install ? Foldy.Platform.SystemInfo.IndexerService.BuildInstall(exe) : Foldy.Platform.SystemInfo.IndexerService.BuildUninstall());
            if (p is not null)
            {
                await p.WaitForExitAsync();
            }

            Shell.Status = install ? "색인 서비스를 설치했습니다. 첫 색인에 수십 초 걸릴 수 있습니다." : "색인 서비스를 제거했습니다.";
            await Task.Delay(install ? 3000 : 500);
            if (_indexer is not null)
            {
                await _indexer.DisposeAsync();
                _indexer = null;
            }

            await ProbeEnginesAsync();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Shell.Status = "관리자 권한 요청이 취소됐습니다";
        }
    }

    private void ConnectSftp()
    {
        var dlg = new SftpConnectWindow(_sftp) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is { } url)
        {
            Shell.ActivePane.NewFolderTab(url);
        }
    }

    private void OnSftpConnect(object sender, RoutedEventArgs e) => ConnectSftp();

    private void OnMenuTemplate(object sender, RoutedEventArgs e) => NewFromTemplate();

    private void OnMenuWizard(object sender, RoutedEventArgs e) => OpenWizard();

    private void OnMenuOrganize(object sender, RoutedEventArgs e) => OpenOrganize(Shell.ActivePane.ActiveFolder?.Path);

    private void OnMenuDescription(object sender, RoutedEventArgs e) => EditDescription();

    private void OnMenuImages(object sender, RoutedEventArgs e) => ActiveView.ToggleImages();

    private void OnInstallIndexer(object sender, RoutedEventArgs e) => RunIndexerSetup(install: true);

    private void ToggleHidden()
    {
        _settings = _settings with { ShowHidden = !_settings.ShowHidden };
        SaveSettings();
        Shell.Status = _settings.ShowHidden ? "숨김 파일 보이기" : "숨김 파일 숨기기";
        _ = Shell.Left.ActiveFolder?.RefreshAsync();
        _ = Shell.Right.ActiveFolder?.RefreshAsync();
    }

    private void SetTheme(FoldyTheme t)
    {
        ThemeManager.SetMode(t);
        _settings = _settings with { Theme = t.ToString().ToLowerInvariant() };
        SaveSettings();
        ApplyDensity();
    }

    private void ApplyDensity()
    {
        // 행 높이: 촘촘하게 24 / 보통 28 / 널널하게 32 (설계서 6.4). 레트로는 테마 값(20) 유지.
        if (ThemeManager.Mode == FoldyTheme.Retro)
        {
            return;
        }

        var h = _settings.Density switch { "compact" => 24.0, "comfortable" => 32.0, _ => 28.0 };
        Application.Current.Resources["List.RowHeight"] = h;
    }

    // ───────────── 팔레트 ─────────────

    private void ShowPalette()
    {
        PaletteInput.Text = "";
        FilterPalette("");
        Palette.IsOpen = true;
        PaletteInput.Focus();
    }

    private void FilterPalette(string q)
    {
        var list = _commands
            .GroupBy(c => c.Title).Select(g => g.First())
            .Where(c => q.Length == 0 || c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || c.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        PaletteList.ItemsSource = list;
        PaletteList.SelectedIndex = list.Count > 0 ? 0 : -1;
    }

    private void OnPaletteFilter(object sender, TextChangedEventArgs e) => FilterPalette(PaletteInput.Text);

    private void OnPaletteKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                PaletteList.SelectedIndex = Math.Min(PaletteList.Items.Count - 1, PaletteList.SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Up:
                PaletteList.SelectedIndex = Math.Max(0, PaletteList.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Enter:
                RunPaletteSelection();
                e.Handled = true;
                break;
            case Key.Escape:
                Palette.IsOpen = false;
                ActiveView.FocusList();
                e.Handled = true;
                break;
        }
    }

    private void OnPaletteRun(object sender, MouseButtonEventArgs e) => RunPaletteSelection();

    private void RunPaletteSelection()
    {
        if (PaletteList.SelectedItem is PaletteCommand c)
        {
            Palette.IsOpen = false;
            ActiveView.FocusList();
            c.Run();
        }
    }

    // ───────────── 버튼 ─────────────

    private void OnFnRename(object sender, RoutedEventArgs e) => ActiveView.BeginRename();

    private void OnFnCopy(object sender, RoutedEventArgs e) => _ = Shell.CopyToOtherAsync();

    private void OnFnMove(object sender, RoutedEventArgs e) => _ = Shell.MoveToOtherAsync();

    private void OnFnNewFolder(object sender, RoutedEventArgs e) => NewFolder();

    private void OnFnDelete(object sender, RoutedEventArgs e) => _ = Shell.DeleteSelectionAsync(permanent: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);

    private void OnFnPalette(object sender, RoutedEventArgs e) => ShowPalette();

    private void OnMore(object sender, RoutedEventArgs e)
    {
        ThemeSystem.IsChecked = ThemeManager.Mode == FoldyTheme.System;
        ThemeLight.IsChecked = ThemeManager.Mode == FoldyTheme.Light;
        ThemeDark.IsChecked = ThemeManager.Mode == FoldyTheme.Dark;
        ThemeRetro.IsChecked = ThemeManager.Mode == FoldyTheme.Retro;
        ShowHiddenItem.IsChecked = _settings.ShowHidden;
        DensityCompact.IsChecked = _settings.Density == "compact";
        DensityNormal.IsChecked = _settings.Density == "normal";
        DensityComfortable.IsChecked = _settings.Density == "comfortable";
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        MoreMenu.IsOpen = true;
    }

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<FoldyTheme>(tag, out var t))
        {
            SetTheme(t);
        }
    }

    private void OnToggleHidden(object sender, RoutedEventArgs e) => ToggleHidden();

    private void OnDensity(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string d })
        {
            _settings = _settings with { Density = d };
            SaveSettings();
            ApplyDensity();
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        using var _ = Process.Start(new ProcessStartInfo(_settingsPath) { UseShellExecute = true });
    }

    private void OnAbout(object sender, RoutedEventArgs e) => ShowAbout();

    private void ShowAbout() => new AboutWindow { Owner = this }.ShowDialog();

    private void OnHelp(object sender, RoutedEventArgs e) => ShowHelp();

    /// <summary>설명서(help\*.md)를 버전을 채워 %LOCALAPPDATA%\Foldy\help에 펼치고 Markdown 탭으로 연다.</summary>
    private void ShowHelp()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "help");
        var index = Path.Combine(src, "index.md");
        if (!File.Exists(index))
        {
            MessageBox.Show(this, "설명서 파일을 찾을 수 없습니다.\n" + index, "도움말", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var version = typeof(ShellWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        var dst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy", "help");
        try
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dst, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(target, File.ReadAllText(f).Replace("{{version}}", version, StringComparison.Ordinal));
                }
                else
                {
                    File.Copy(f, target, overwrite: true);
                }
            }

            index = Path.Combine(dst, "index.md");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "도움말 복사 실패, 원본을 연다");
        }

        ShellViewModel.OpenMarkdown(Shell.ActivePane, index);
    }
}
