using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
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
using Foldy.Presentation.Sidebar;
using Foldy.Presentation.Tabs;
using Foldy.Presentation.Tree;
using Foldy.Providers.Local;
using Foldy.Providers.Sftp;
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
    private IUpdateSource _updater; // 서비스(UAC 없음)를 쓸 수 있으면 OnLoaded에서 ServiceUpdater로 바꾼다
    private AppSettings _settings;
    private HwndSource? _hwndSource;
    private readonly string _quickPath = Path.Combine(App.DataDir, "quickaccess.json");
    private readonly QuickAccess _quick;
    private readonly DispatcherTimer _quickSave;

    public ShellWindow()
    {
        InitializeComponent();
        _hadSettings = File.Exists(_settingsPath); // 설정이 이미 있으면 처음 설치가 아니라 업데이트 (묻지 않고 알리기만)
        _settings = AppSettings.Migrate(JsonFileStore.Load(_settingsPath, () => new AppSettings()));
        Foldy.UI.Markdown.MarkdownView.WriteInFoldy = path =>
        {
            if (!Foldy.Presentation.Shell.ShellViewModel.MarkdownExtensions.Contains(Path.GetExtension(path)))
            {
                return false;
            }

            var w = MemoWindow.OpenMarkdown(path);
            w.Owner = this;
            w.Show();
            return true;
        };
        Foldy.Core.Tags.FileTags.Load(Path.Combine(App.DataDir, "tags-local.json"));
        Foldy.Core.Managed.ManagedFolders.LoadRegistry(Path.Combine(App.DataDir, "managed-folders.json"));
        Foldy.Core.Managed.ManagedFolders.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            LeftPane.RefreshManaged();
            RightPane.RefreshManaged();
        });
        _queues = new DriveQueueRegistry(nativeIo: new NativeIoCancel(Foldy.Platform.SystemInfo.Drives.CurrentNativeThreadId, id => Foldy.Platform.SystemInfo.Drives.CancelSynchronousIo(id)));
        _provider = new LocalProvider(_queues);
        _drives = new DriveService(_queues);
        var ui = new WpfDispatcher(Dispatcher);
        _sftp = new SftpSessions();
        _env = new TabEnvironment(new ProviderRouter(_provider, _sftp.Provider), ui)
        {
            TypeName = TypeNames.Get,
            Watcher = new FolderWatchService(),
            Speed = Foldy.Presentation.Services.DriveSpeedTracker.Shared,
            ShowHidden = () => _settings.ShowHidden,
            Git = new GitStatusService(_git),
            FolderSizes = new IndexerFolderSizes(() => _indexer),
            Loaded = Diagnostics.DiagnosticMode.FolderLoaded,
        };
        Shell = new ShellViewModel(_env, new ShellServices(
            new SftpAwareFileOperationService(
                new NativeCopyFileOperationService(
                    new ShellFileOperationService(() => OwnerHandle),
                    options: new NativeCopyFileOperationOptions
                    {
                        ResolveConflict = ConflictWindow.AskAsync,
                        ThreadsProvider = () => _settings.CopyThreads,
                        // 덮어쓸 파일은 이전 버전으로 옮겨 두고, 끝내 덮어쓰지 못하면(취소·실패) 제자리로 돌려놓는다.
                        // 한 작업에서 여러 파일을 보관할 때 앞 파일이 한도 때문에 먼저 지워지지 않게 정리는 작업 끝에 한 번.
                        BeforeOverwrite = dest =>
                            _settings.KeepPreviousVersions && Foldy.Core.History.VersionHistory.Preserve(dest, prune: false) is { } v
                                ? new Action(() => Foldy.Core.History.VersionHistory.PutBack(v))
                                : null,
                        AfterOverwrites = () => Foldy.Core.History.VersionHistory.PrunePending(),
                    }),
                _sftp.Provider,
                new SftpTransferOptions { ResolveConflict = ConflictWindow.AskTransferAsync }),
            new ShellOpenFileService(),
            new TerminalService(() => _settings, (p, sudo) => _sftp.OpenTerminal(p, sudo))))
        {
            Settings = _settings,
            ConfirmAsync = msg => Task.FromResult(Foldy.UI.Dialogs.FoldyDialog.Confirm(this, Foldy.Core.Localization.Loc.T("dlg.ok"), msg, Foldy.Core.Localization.Loc.T("sw.deleteYes"), danger: true)), // SFTP 삭제처럼 되살릴 수 없는 일
            OpenSpecial = p => SecureFlows.TryOpen(this, p, Shell!),
            AskBigDeleteAsync = q => DeleteFlows.AskBigDeleteAsync(this, q),
            FreeUpSpace = root => OpenAnalysis(root), // 공간이 모자라 멈춘 작업의 [공간 늘리기]
        };

        // Store(MSIX) 판은 Microsoft Store가 업데이트한다: 자체 업데이트(AppUpdater·서비스)는 쓰지 않는다 (docs/store.md)
        _updater = Foldy.Platform.SystemInfo.AppPackage.IsPackaged ? new StoreUpdateSource() : new LegacyUpdateSource(new AppUpdater(AppVersion));
        _updater.Changed += OnUpdaterChanged;
        _search = new FileSearchService(() => App.Options.DocShots is null ? _indexer : null, // 문서 캡처는 색인(전체 드라이브)을 검색에 쓰지 않는다
             () => _settings.ShowHidden);
        Shell.Search = _search;

        // 공통 사이드바: 자주 쓰는 폴더(고정)·최근 폴더·드라이브
        _quick = new QuickAccess(JsonFileStore.Load(_quickPath, () => new QuickAccessState()));
        _quickSave = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _quickSave.Tick += (_, _) => SaveQuickAccess();
        _quick.Changed += () => { _quickSave.Stop(); _quickSave.Start(); };
        _quick.Pinned.CollectionChanged += (_, _) => UpdatePinnedEmpty();
        _env.Visited = p => _quick.Visit(p);
        PinnedList.ItemsSource = _quick.Pinned;
        RecentList.ItemsSource = _quick.Recent;
        SideDrives.ItemsSource = Shell.Left.Drives;
        Title = $"Foldy {typeof(ShellWindow).Assembly.GetName().Version?.ToString(3)}";
        ApplyDensity();
        ApplyUiScale();
        // Windows의 텍스트 크기를 바꾸면 바로 따라간다 (직접 고른 배율이 없을 때)
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_settings.UiScale <= 0)
            {
                ApplyUiScale();
            }
        });
        ThemeManager.Attach(this);
        Foldy.UI.Dialogs.FoldyDialog.Decorate = w =>
        {
            ThemeManager.Attach(w);
            App.ApplyScale(w);
        };

        var session = JsonFileStore.Load(_sessionPath, () => new SessionState());
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        RestorePlacement(session);
        LeftPane.Attach(this, Shell.Left);
        RightPane.Attach(this, Shell.Right);
        // 두 패널 모두 왼쪽에 폴더 트리, 오른쪽에 파일 목록 (Ctrl+B로 숨기기, 가운데 선을 끌어 너비 조절)
        LeftPane.TreeWidth = session.LeftTreeWidth;
        RightPane.TreeWidth = session.RightTreeWidth;
        SidebarExpanded = session.Window is null ? SystemParameters.PrimaryScreenWidth >= 1400 : session.SidebarExpanded;
        UpdatePinnedEmpty();
        Shell.Restore(session, home);
        LeftPane.SelectionInfoChanged += (_, _) => UpdateSelectionText();
        RightPane.SelectionInfoChanged += (_, _) => UpdateSelectionText();

        Shell.PropertyChanged += OnShellChanged;
        Shell.SaveRequested += s => SaveSession(s);
        Shell.PaneActivated += p => UpdateSelectionText();
        Shell.Jobs.CollectionChanged += OnJobsChanged;
        JobsList.ItemsSource = Shell.Jobs;

        BuildCommands();
        UpdateRecycleBinInfo();
        new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => UpdateRecycleBinInfo(), Dispatcher).Start();
        Shell.Jobs.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateRecycleBinInfo);
        Diagnostics.DiagnosticMode.Apply(_settings.DiagnosticUntil);
        UpdateDiagnosticIndicator();
        // 진단 모드는 24시간 뒤 저절로 꺼진다 (켠 채로 앱을 오래 두어도)
        new DispatcherTimer(TimeSpan.FromMinutes(10), DispatcherPriority.Background, (_, _) =>
        {
            if (Diagnostics.DiagnosticMode.IsOn && _settings.DiagnosticUntil is { } u && u <= DateTime.UtcNow)
            {
                Diagnostics.DiagnosticMode.Apply(null);
                UpdateDiagnosticIndicator();
            }
        }, Dispatcher).Start();
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

    public FolderTreeViewModel CreateTree() => new(_env.Provider, _drives.GetDrivesFast());

    public FrameworkElement? CreateTabView(TabBase tab, PaneViewModel pane) => tab switch
    {
        MarkdownTabViewModel md => CreateDocumentView(md, pane),
        Foldy.Presentation.Git.GitTabViewModel git => new Foldy.UI.Git.GitView { DataContext = git },
        Foldy.Presentation.Analysis.AnalysisTabViewModel an => new Foldy.UI.Analysis.AnalysisView { DataContext = an },
        _ => null,
    };

    public void OpenGit(PaneViewModel pane, string repoRoot) => OpenGitTab(pane, repoRoot, activate: true);

    /// <summary>Git 뷰 탭을 열거나 (이미 있으면) 찾아 돌려준다. activate 가 false 면 지금 보던 탭을 그대로 둔다.</summary>
    private Foldy.Presentation.Git.GitTabViewModel OpenGitTab(PaneViewModel pane, string repoRoot, bool activate)
    {
        var existing = pane.Tabs.OfType<Foldy.Presentation.Git.GitTabViewModel>().FirstOrDefault(t => string.Equals(t.RepoRoot, repoRoot, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (activate) pane.ActiveTab = existing;
            return existing;
        }

        var vm = new Foldy.Presentation.Git.GitTabViewModel(_git, repoRoot);
        pane.AddTab(vm, activate: activate, index: pane.ActiveTab is null ? null : pane.Tabs.IndexOf(pane.ActiveTab) + 1, assignColor: true);
        return vm;
    }

    public bool ShowGitBar => _settings.ShowGitBar;

    /// <summary>저장소에 들어왔을 때. "Git 화면 자동 열기" 설정은 뺐다 (좋은 기본값 > 설정): Git 바에서 한 번에 연다.</summary>
    public void GitDetected(PaneViewModel pane, string repoRoot)
    {
    }

    private bool _gitBarBusy;

    /// <summary>Git 바 버튼. 받기/올리기는 화면을 옮기지 않고 백그라운드로 실행하고 결과를 상태바에 보인다.</summary>
    public void GitBarAction(PaneViewModel pane, string repoRoot, Foldy.UI.Views.GitBarCommand command)
    {
        switch (command)
        {
            case Foldy.UI.Views.GitBarCommand.Open:
                OpenGit(pane, repoRoot);
                break;
            case Foldy.UI.Views.GitBarCommand.Commit:
                OpenGitTab(pane, repoRoot, activate: true).ShowChangesTab();
                break;
            default:
                _ = RunGitBarAsync(repoRoot, command == Foldy.UI.Views.GitBarCommand.Pull);
                break;
        }
    }

    private async Task RunGitBarAsync(string repoRoot, bool pull)
    {
        if (_gitBarBusy)
        {
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.git.busy");
            return;
        }

        _gitBarBusy = true;
        var label = pull ? Foldy.Core.Localization.Loc.T("sw.git.pull") : Foldy.Core.Localization.Loc.T("sw.git.push");
        Shell.Status = Foldy.Core.Localization.Loc.F("sw.git.running", label);
        try
        {
            Foldy.Git.GitResult r;
            var done = Foldy.Core.Localization.Loc.F("sw.git.done", label);
            if (pull)
            {
                r = await _git.PullAsync(repoRoot, Foldy.Git.PullMode.Merge);
            }
            else
            {
                var st = await _git.GetStatusAsync(repoRoot);
                var ahead = st.Value?.Ahead ?? 0;
                r = await _git.PushAsync(repoRoot, setUpstream: st.Value is { HasUpstream: false }, forceWithLease: false);
                if (ahead > 0) done += Foldy.Core.Localization.Loc.F("sw.git.commits", ahead);
            }

            Shell.Status = r.Success ? done : Foldy.Core.Localization.Loc.F("sw.git.failed", label, Foldy.Presentation.Git.GitErrorText.Describe(r).Split('\n')[0]);
        }
        finally
        {
            _gitBarBusy = false;
        }

        _ = Shell.Left.ActiveFolder?.RefreshAsync();
        _ = Shell.Right.ActiveFolder?.RefreshAsync();
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

    /// <summary>활성 폴더 탭에서 선택한(없으면 포커스된) 파일을 Git 뷰의 blame 으로 연다.</summary>
    private void GitBlameFocusedFile()
    {
        var pane = Shell.ActivePane;
        if (pane.ActiveFolder is not { Git: { } g } folder)
        {
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.git.pickInRepo");
            return;
        }

        var file = folder.Selection.FirstOrDefault(i => !i.IsFolder)
            ?? folder.Items.OfType<Foldy.Presentation.Files.FileItem>().FirstOrDefault(i => !i.IsFolder && i.Name == folder.FocusName);
        if (file is null)
        {
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.git.pickBlame");
            return;
        }

        var root = g.RepoRoot.TrimEnd('\\', '/');
        if (!file.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.git.outside");
            return;
        }

        var rel = file.FullPath[root.Length..].TrimStart('\\', '/').Replace('\\', '/');
        var gitTab = OpenGitTab(pane, g.RepoRoot, activate: true);
        _ = gitTab.ShowBlameAsync(rel);
    }

    private PaneView ActiveView => Shell.ActivePane == Shell.Left ? LeftPane : RightPane;

    // ───────────── 시작·종료 ─────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ActiveView.FocusList();
        if (App.Options.OpenPath is { } open && Shell.ActivePane.ActiveFolder is { } tab)
        {
            if (File.Exists(open) && SecureFlows.TryOpen(this, open, Shell))
            {
                _ = tab.NavigateAsync(Path.GetDirectoryName(open)!);
            }
            else
            {
                _ = tab.NavigateAsync(open);
            }
        }

        RefreshDrives();
        StartContextHints();
        NoticeIfUpdated();
        var quiet = App.Options.IsBench || App.Options.IsCapture || App.Options.DocShots is not null;
        if (quiet)
        {
            // 성능 측정·화면 캡처: 아무것도 묻거나 보여 주지 않는다
        }
        else if (_hadSettings || FirstRunTour.Shown)
        {
            // 업데이트·다시 설치: 설정은 그대로라 묻지 않는다. 이 버전에서 아직 안 봤으면 짧은 소개만 (사용자 요청 2026-11-21)
            _ = EnsureQuickSearchHotkeyAsync(null); // 묻지 않고 등록만, 겹칠 때만 알린다
            if (!FirstRunTour.ShownForThisVersion)
            {
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(1500);
                    await FirstRunTour.RunAsync(this);
                }, DispatcherPriority.ApplicationIdle);
            }
        }
        else
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(1500);
                await FirstRunTour.FirstRunAsync(this, SetUiScale,
                    t => SetTheme(t switch { "light" => FoldyTheme.Light, "dark" => FoldyTheme.Dark, _ => FoldyTheme.System }),
                    w => EnsureQuickSearchHotkeyAsync(w));
            }, DispatcherPriority.ApplicationIdle);
        }

        if (!App.Options.IsBench && !App.Options.IsCapture && !Foldy.Platform.SystemInfo.AppPackage.IsPackaged)
        {
            _updater.Start(TimeSpan.FromSeconds(5)); // 실행 5초 뒤 + 6시간마다 (짧게 켰다 꺼도 확인되게)
            _ = UseServiceUpdaterAsync(); // 서비스를 쓸 수 있으면 UAC 없는 쪽으로 바꾼다
        }

        await ProbeEnginesAsync();
        if (App.Options.DocShots is { } docDir)
        {
            await RunDocShotsAsync(docDir);
            return;
        }

        if (App.Options.Screenshot is { } shot)
        {
            await Task.Delay(2500);
            SaveScreenshot(shot);
            Application.Current.Shutdown(0);
        }
    }

    private static string AppVersion => typeof(ShellWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private void OnUpdaterChanged() => Dispatcher.BeginInvoke(() =>
    {
        UpdateUpdateButton();
        // 서비스(SYSTEM)는 사용자별 프록시(PAC 스크립트·인증 프록시)를 쓰지 못해 회사망에서 확인이 실패할 수 있다.
        // 그러면 한 번은 앱(사용자 권한, 사용자 프록시 설정)이 직접 확인한다.
        if (!_triedUserProxyFallback && _updater is ServiceUpdater { ReadyVersion: null, LastError: { } e } && IsNetworkError(e))
        {
            _ = TryUserProxyFallbackAsync();
        }
    });

    private bool _triedUserProxyFallback;
    private string? _userProxyError;
    private bool _userProxyUpToDate;

    /// <summary>사용자(현재 로그온) 프록시 설정으로 GitHub에 갈 때 실제로 쓰는 프록시 (PAC 결과 포함). 진단 기록용.</summary>
    private static string DescribeUserProxy()
    {
        try
        {
            var target = new Uri("https://api.github.com/");
            var proxy = System.Net.WebRequest.GetSystemWebProxy();
            var via = proxy.GetProxy(target);
            // 프록시 주소(회사 서버 이름)는 기록하지 않는다: 로그 끝부분이 공개 버그 신고에 붙을 수 있다
            return via is null || via == target ? "직접 연결" : "시스템 프록시 사용";
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            return "프록시 확인 실패: " + ex.GetType().Name;
        }
#pragma warning restore CA1031
    }

    // 서비스 확인이 어떤 이유로든 실패하면(사내 PAC 미사용, GitHub API 한도 403 등) 앱이 사용자 프록시로 직접 확인한다
    private static bool IsNetworkError(string error) => error.Length > 0;

    /// <summary>
    /// 서비스가 네트워크 오류로 확인하지 못하면 사용자 권한(사용자의 PAC·프록시·로그온 자격 증명)으로 직접 확인·다운로드한다.
    /// 찾으면 이번 실행 동안 기존 방식(설치할 때 관리자 확인 한 번)으로 바꾼다. 서명·해시 확인은 같다.
    /// </summary>
    private async Task<bool> TryUserProxyFallbackAsync()
    {
        if (_triedUserProxyFallback || _updater is not ServiceUpdater { LastError: { } err } || !IsNetworkError(err))
        {
            return false;
        }

        _triedUserProxyFallback = true;
        Log.Information("서비스 업데이트 확인 실패({Error}) → 사용자 프록시로 직접 확인 (프록시: {Proxy})", err, DescribeUserProxy());
        var legacy = new LegacyUpdateSource(new AppUpdater(AppVersion));
        bool found;
        try
        {
            found = await legacy.CheckAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning(ex, "사용자 프록시로 업데이트 확인 실패");
            found = false;
        }

        if (!found || legacy.ReadyVersion is null)
        {
            Log.Information("사용자 프록시로도 새 버전 없음/실패: {Error} (프록시: {Proxy})", legacy.LastError, DescribeUserProxy());
            _userProxyError = legacy.LastError;
            _userProxyUpToDate = legacy.LastError is null;
            legacy.Dispose();
            return false;
        }

        _updater.Changed -= OnUpdaterChanged;
        _updater.Dispose();
        _updater = legacy;
        _updater.Changed += OnUpdaterChanged;
        UpdateUpdateButton();
        Shell.Status = Foldy.Core.Localization.Loc.F("sw.upd.legacyReady", legacy.ReadyVersion);
        return true;
    }

    /// <summary>색인 서비스가 업데이트를 지원하면 그쪽(UAC 없음)으로 바꾸고 정상 시작을 알린다. 아니면 AppUpdater(UAC) 유지.</summary>
    private async Task UseServiceUpdaterAsync()
    {
        // 시작 직후에는 색인 연결과 겹쳐 파이프가 바쁠 수 있다. 몇 번 다시 시도하고, 그래도 안 되면 5분마다 다시 본다.
        ServiceUpdater? svc = null;
        for (var i = 0; i < 4 && svc is null; i++)
        {
            if (i > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }

            svc = await ServiceUpdater.TryCreateAsync(timeout: TimeSpan.FromSeconds(3));
        }

        if (svc is null)
        {
            Log.Information("색인 서비스 업데이트에 연결하지 못해 직접 확인 방식(관리자 권한)으로 둔다. 5분 뒤 다시 시도");
            _serviceRetry ??= new DispatcherTimer(TimeSpan.FromMinutes(5), DispatcherPriority.Background, async (_, _) =>
            {
                if (_updater is not ServiceUpdater)
                {
                    await UseServiceUpdaterAsync();
                }
            }, Dispatcher);
            _serviceRetry.Start();
            return;
        }

        _serviceRetry?.Stop();
        Log.Information("색인 서비스 업데이트 사용 (UAC 없음)");

        _updater.Changed -= OnUpdaterChanged;
        _updater.Dispose();
        _updater = svc;
        _updater.Changed += OnUpdaterChanged;
        await svc.ReportStartedAsync(AppVersion);
        _updater.Start(TimeSpan.FromSeconds(5));

        UpdateUpdateButton();
    }

    private DispatcherTimer? _serviceRetry;

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
        CopyEngineText.Text = available ? Foldy.Core.Localization.Loc.T("sw.copyRust") : Foldy.Core.Localization.Loc.T("sw.copyBasic");
        CopyEngineText.ToolTip = available ? Foldy.Core.Localization.Loc.T("sw.copyRustTip") : reason;
        await ConnectIndexerAsync();
    }

    private DispatcherTimer? _indexerRetry;

    /// <summary>
    /// 색인 서비스에 연결한다. 시작 직후에는 서비스가 색인을 불러오거나 업데이트 확인과 겹쳐 바쁠 수 있어,
    /// 짧게 한 번만 시도하면 "서비스 없음"으로 잘못 보인다 (0.3.x 회귀). 넉넉히 몇 번 시도하고, 그래도 안 되면 30초마다 다시 시도한다.
    /// </summary>
    private async Task ConnectIndexerAsync()
    {
        if (_indexer is { IsFaulted: false })
        {
            return;
        }

        for (var i = 0; i < 3 && _indexer is null; i++)
        {
            if (i > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            _indexer = await IndexerClient.TryConnectAsync(timeout: TimeSpan.FromSeconds(3));
        }

        if (_indexer is null)
        {
            var svc = DescribeIndexerService();
            Log.Information("색인 서비스에 연결하지 못함 ({Service}). 30초마다 다시 시도", svc);
            _indexerRetry ??= new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, async (_, _) =>
            {
                if (_indexer is null)
                {
                    await ConnectIndexerAsync();
                }
            }, Dispatcher);
            _indexerRetry.Start();
            IndexerText.Text = Foldy.Core.Localization.Loc.T("sw.idx.crawlOnly");
            IndexerText.ToolTip = Foldy.Core.Localization.Loc.F("sw.idx.noServiceTip", svc);
            await _search.RefreshStateAsync();
            return;
        }

        _indexerRetry?.Stop();
        Log.Information("색인 서비스 연결됨");
        StartCleanupChecks();
        // AI 연결 제안: 켠 뒤 잠시 있다가 한 번 (화면이 자리 잡은 뒤)
        var aiOnce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        aiOnce.Tick += (_, _) =>
        {
            aiOnce.Stop();
            if (!MaybeSuggestDesktop())
            {
                MaybeSuggestAi(); // 한 줄 제안은 한 번에 하나만
            }
        };
        aiOnce.Start();
        await _search.RefreshStateAsync();
        if (_indexer is not null)
        {
            _ = Shell.Left.ActiveFolder?.RefreshFolderSizesAsync();
            _ = Shell.Right.ActiveFolder?.RefreshFolderSizesAsync();
        }

        UpdateIndexText();
        _indexStatusTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, async (_, _) =>
        {
            await _search.RefreshStateAsync();
            UpdateIndexText();
        }, Dispatcher);
        _indexStatusTimer.Start();
        StartSlowCrawl();
    }

    private Foldy.Core.Search.SlowCrawler? _crawler;

    /// <summary>
    /// 색인 없는 드라이브를 천천히 모은다 (사용자 2026-11-21: "회사 드라이브도 부하 주지 않게 살살"). 설정으로 끌 수 있다.
    /// 시험 실행(FOLDY_DATA_DIR)·문서 캡처·성능 측정에서는 하지 않는다(사용자의 네트워크 드라이브를 건드리지 않게).
    /// </summary>
    private void StartSlowCrawl()
    {
        if (!_settings.SlowCrawl || App.Options.IsBench || App.Options.IsCapture || App.Options.DocShots is not null
            || Environment.GetEnvironmentVariable("FOLDY_DATA_DIR") is { Length: > 0 })
        {
            _crawler?.Dispose();
            _crawler = null;
            _search.Crawler = null;
            return;
        }

        if (_crawler is not null)
        {
            return;
        }

        _crawler = new Foldy.Core.Search.SlowCrawler(Path.Combine(App.DataDir, "crawl"), CrawlDrives)
        {
            ShouldPause = () =>
            {
                try
                {
                    return Dispatcher.Invoke(() => Shell.Jobs.Any(j => !j.IsFinished)); // 복사·이동 같은 작업 중엔 쉰다
                }
                catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
                {
                    return true; // 끄는 중
                }
            },
        };
        _crawler.Changed += _ => Dispatcher.BeginInvoke(UpdateIndexText);
        _search.Crawler = _crawler;
        _search.CrawlDrives = CrawlDrives;
        _crawler.Start();
    }

    /// <summary>
    /// 모을 드라이브: 준비된 드라이브 중 색인 서비스가 다루지 않는 것 (네트워크, NTFS가 아님, 클라우드 가상 드라이브, USB·SD 카드). CD만 뺀다.
    /// (2026-11-21 사용자: "D: 색인 기다리는 중이라는데 언제 되지?" → exFAT SD 카드가 목록에서 빠져 있어 영원히 기다리는 중으로 보였다)
    /// </summary>
    private List<string> CrawlDrives()
    {
        var list = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown)
                {
                    continue;
                }

                if (_search.IsIndexedPath(d.Name))
                {
                    continue;
                }

                if (d.DriveType is DriveType.Network or DriveType.Removable || !string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase) || _search.LastStatus is not null)
                {
                    list.Add(d.Name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return list;
    }

    private DispatcherTimer? _indexStatusTimer;

    /// <summary>
    /// 상태 줄의 색인 상태: 만드는 중이면 몇 %, 끝났으면 몇 개. 마우스를 올리면 드라이브마다 (사용자 2026-11-21: "색인이 어느 정도 됐는지 알 수 있니?").
    /// </summary>
    private void UpdateIndexText()
    {
        if (_search.LastStatus is not { } st)
        {
            return;
        }

        IndexerText.Text = st.State switch
        {
            Foldy.Indexer.Client.IndexState.Ready => Foldy.Core.Localization.Loc.F("sw.idx.ready", st.IndexedEntries),
            Foldy.Indexer.Client.IndexState.Indexing => Foldy.Core.Localization.Loc.F("sw.idx.building", st.ProgressPermille / 10),
            _ => Foldy.Core.Localization.Loc.T("sw.idx.preparingAll"),
        };
        var lines = st.Volumes.Select(v => v.State switch
        {
            Foldy.Indexer.Client.IndexState.Ready => Foldy.Core.Localization.Loc.F("sw.vol.ready", v.Volume, v.Entries),
            Foldy.Indexer.Client.IndexState.Indexing => Foldy.Core.Localization.Loc.F("sw.vol.building", v.Volume, v.ProgressPermille / 10),
            _ => Foldy.Core.Localization.Loc.F("sw.vol.preparing", v.Volume),
        }).ToList();
        var indexed = st.Volumes.Select(v => v.Volume.TrimEnd('\\').ToUpperInvariant()).ToHashSet();
        foreach (var d in System.IO.DriveInfo.GetDrives())
        {
            var name = d.Name.TrimEnd('\\').ToUpperInvariant();
            if (!indexed.Contains(name))
            {
                lines.Add(_crawler?.StatusOf(d.Name) is { } cs
                    ? Foldy.Core.Localization.Loc.F("sw.vol.crawl", name, (cs.Running ? Foldy.Core.Localization.Loc.T("sw.crawl.running") : cs.Complete ? Foldy.Core.Localization.Loc.T("sw.crawl.done") : Foldy.Core.Localization.Loc.T("sw.crawl.idle")), Math.Max(0, cs.Found), (d.DriveType == System.IO.DriveType.Network ? Foldy.Core.Localization.Loc.T("sw.drive.network") : Foldy.Core.Localization.Loc.T("sw.drive.notNtfs")))
                    : Foldy.Core.Localization.Loc.F("sw.vol.none", name, (d.DriveType == System.IO.DriveType.Network ? Foldy.Core.Localization.Loc.T("sw.drive.network") : Foldy.Core.Localization.Loc.T("sw.drive.notNtfs"))));
            }
        }

        IndexerText.ToolTip = Foldy.Core.Localization.Loc.T("sw.idx.tipHeader") + string.Join("\n", lines);

        // 사이드바 드라이브마다 한 줄로 (바뀐 것만 바꾼다)
        foreach (var pane in new[] { Shell.Left, Shell.Right })
        {
            foreach (var d in pane.Drives.ToList())
            {
                var text = DriveIndexText(st, d);
                if (text != d.IndexText)
                {
                    pane.UpdateDrive(d with { IndexText = text });
                }
            }
        }
    }

    /// <summary>드라이브 옆 한 줄 (사용자 2026-11-21: "색인 중이라고 표시해 줘").</summary>
    private string DriveIndexText(Foldy.Indexer.Client.StatusReply st, DriveItem d)
    {
        var v = st.Volumes.FirstOrDefault(v => string.Equals(v.Volume.TrimEnd('\\'), d.Letter, StringComparison.OrdinalIgnoreCase));
        if (v is not null)
        {
            return v.State switch
            {
                Foldy.Indexer.Client.IndexState.Ready => Foldy.Core.Localization.Loc.T("sw.drv.ready"),
                Foldy.Indexer.Client.IndexState.Indexing => Foldy.Core.Localization.Loc.F("sw.drv.indexing", v.ProgressPermille / 10),
                _ => Foldy.Core.Localization.Loc.T("sw.drv.preparing"),
            };
        }

        if (_crawler?.StatusOf(d.Root) is { } c)
        {
            return c.Running
                ? Foldy.Core.Localization.Loc.F("sw.drv.crawling", Math.Max(0, c.Found))
                : c.Complete ? Foldy.Core.Localization.Loc.F("sw.drv.crawled", Math.Max(0, c.Found)) : Foldy.Core.Localization.Loc.T("sw.drv.idle");
        }

        if (_crawler is null || d.Kind == DriveKind.CdRom)
        {
            return Foldy.Core.Localization.Loc.T("sw.drv.none");
        }

        // 한 번에 한 드라이브씩 모은다: 무엇을 기다리는지 말한다
        return _crawler.Current() is { } now ? Foldy.Core.Localization.Loc.F("sw.drv.waiting", now.Drive.TrimEnd('\\')) : Foldy.Core.Localization.Loc.T("sw.drv.soon");
    }

    /// <summary>진단용: Windows 서비스 FoldyIndexer의 상태 (없음/실행 중/중지됨 …).</summary>
    private static string DescribeIndexerService()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("FoldyIndexer");
            return "서비스 상태: " + sc.Status;
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            return "서비스가 설치되어 있지 않음";
        }
#pragma warning restore CA1031
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _crawler?.Dispose();
        SaveSession(Shell.Capture(null, double.NaN));
        _quickSave.Stop();
        SaveQuickAccess();
        foreach (var t in Shell.Left.Tabs.Concat(Shell.Right.Tabs))
        {
            t.Close();
        }

        // 종료할 때 설치 (설계서 10.5 가): 진행 중인 작업이 없을 때만, 다시 띄우지 않음
        if (_updater is ServiceUpdater su && !Shell.Jobs.Any(j => !j.IsFinished))
        {
            su.InstallAtExit();
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
            JsonFileStore.Save(_sessionPath, s with
            {
                Window = placement,
                SplitRatio = split,
                SidebarExpanded = SidebarExpanded,
                LeftTreeWidth = LeftPane.TreeWidth,
                RightTreeWidth = RightPane.TreeWidth,
            });
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

    /// <summary>AI 연결 화면 (MCP). 켜고 끄기는 이 창의 설정에 바로 저장한다.</summary>
    private void OpenMcp()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var w = new McpWindow(Foldy.Core.Mcp.McpClientConfig.Known(roaming, local, home), Services.McpRunner.ExePath, () => _settings.McpEnabled, on =>
        {
            _settings = _settings with { McpEnabled = on };
            SaveSettings();
        })
        { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ThemeManager.Attach(w);
        w.ShowDialog();
    }

    private void OnMcp(object sender, RoutedEventArgs e) => OpenMcp();

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

        if (msg == GlobalHotkey.WmHotkey && wParam.ToInt32() == GlobalHotkey.QuickSearchId)
        {
            handled = true;
            Dispatcher.BeginInvoke(ShowQuickSearch);
            return IntPtr.Zero;
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
            ShowStatus(Shell.Status);
        }
        else if (e.PropertyName == nameof(ShellViewModel.ActivePane))
        {
            UpdateSelectionText();
        }
    }

    private void UpdateSelectionText() => SelectionText.Text = ActiveView.SelectionSummary();

    /// <summary>상태 메시지: 가운데 칩에 크게 보이고, 새 메시지면 강조색이 잠깐 비쳤다가 사라진다 (애니메이션을 끄면 깜빡임 없음).</summary>
    private void ShowStatus(string? message)
    {
        var text = message ?? "";
        var changed = !string.Equals(StatusText.Text, text, StringComparison.Ordinal);
        StatusText.Text = text;
        StatusPill.ToolTip = text.Length > 60 ? text : null;
        StatusPill.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!changed || text.Length == 0)
        {
            return;
        }

        if (!_settings.Animations)
        {
            StatusFlash.BeginAnimation(OpacityProperty, null);
            StatusFlash.Opacity = 0;
            return;
        }

        var flash = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
        flash.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.38, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        flash.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.38, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));
        flash.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1700)), new System.Windows.Media.Animation.QuadraticEase()));
        StatusFlash.BeginAnimation(OpacityProperty, flash);
    }

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
        JobsHeader.Text = running.Count > 0 ? Foldy.Core.Localization.Loc.F("sw.jobs.running", running.Count) : Foldy.Core.Localization.Loc.T("sw.jobs");
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

    private void OnJobFreeUp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: JobViewModel j })
        {
            Shell.FreeUpSpaceFor(j);
        }
    }

    private void OnJobRetry(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: JobViewModel j })
        {
            _ = Shell.RetryAsync(j);
        }
    }

    // ───────────── 명령 ─────────────

    private Foldy.Presentation.Commands.KeyMap _keyMap = Foldy.Presentation.Commands.KeyMap.Load();

    private sealed record PaletteCommand(string Id, string Title, string Gesture, Action Run, Key Key = Key.None, ModifierKeys Mods = ModifierKeys.None, bool AllowInText = false);

    private void BuildCommands()
    {
        void Add(string id, string title, Action run, Key key = Key.None, ModifierKeys mods = ModifierKeys.None, string? gesture = null, bool inText = false) =>
            _commands.Add(new PaletteCommand(id, title, gesture ?? Gesture(key, mods), run, key, mods, inText));

        var activeTab = () => Shell.ActivePane.ActiveFolder;
        Add("file.copy", Foldy.Core.Localization.Loc.T("cmd.copyOther"), () => _ = Shell.CopyToOtherAsync(), Key.F5);
        Add("file.move", Foldy.Core.Localization.Loc.T("cmd.moveOther"), () => _ = Shell.MoveToOtherAsync(), Key.F6);
        Add("file.newFolder", Foldy.Core.Localization.Loc.T("cmd.newFolder"), NewFolder, Key.F7);
        Add("folder.fromTemplate", Foldy.Core.Localization.Loc.T("sw.c.fromTemplate"), NewFromTemplate, Key.N, ModifierKeys.Control | ModifierKeys.Shift);
        Add("folder.wizard", Foldy.Core.Localization.Loc.T("sw.c.wizard"), OpenWizard);
        Add("folder.organize", Foldy.Core.Localization.Loc.T("sw.c.organize"), () => OpenOrganize(Shell.ActivePane.ActiveFolder?.Path));
        Add("folder.description", Foldy.Core.Localization.Loc.T("sw.c.editDescription"), EditDescription);
        Add("view.images", Foldy.Core.Localization.Loc.T("cmd.images"), () => ActiveView.ToggleImages(), Key.I, ModifierKeys.Control | ModifierKeys.Shift);
        Add("git.open", Foldy.Core.Localization.Loc.T("cmd.git"), () =>
        {
            if (Shell.ActivePane.ActiveFolder?.Git is { } g)
            {
                OpenGit(Shell.ActivePane, g.RepoRoot);
            }
            else if (Shell.ActivePane.ActiveFolder?.Path is { Length: > 0 } p && Directory.Exists(p))
            {
                // 저장소가 아니면 Git 뷰가 "여기에 Git 저장소 만들기" 를 안내한다
                OpenGit(Shell.ActivePane, p);
            }
            else
            {
                Shell.Status = Foldy.Core.Localization.Loc.T("sw.c.notRepo");
            }
        });
        Add("git.blame", Foldy.Core.Localization.Loc.T("cmd.blame"), GitBlameFocusedFile);
        Add("file.rename", Foldy.Core.Localization.Loc.T("cmd.rename"), () => ActiveView.BeginRename(), gesture: "F2");
        Add("file.delete", Foldy.Core.Localization.Loc.T("cmd.recycle"), () => _ = Shell.DeleteSelectionAsync(false), gesture: "Del");
        Add("file.deletePermanent", Foldy.Core.Localization.Loc.T("cmd.deletePermanent"), () => _ = Shell.DeleteSelectionAsync(true), gesture: "Shift+Del");
        Add("sidebar.focus", Foldy.Core.Localization.Loc.T("sw.c.sidebarFocus"), FocusSidebar, Key.E, ModifierKeys.Control | ModifierKeys.Shift);
        Add("pane.switch", Foldy.Core.Localization.Loc.T("cmd.switchPane"), () => { Shell.SwitchPane(); ActiveView.FocusList(); }, Key.Tab);
        Add("tab.new", Foldy.Core.Localization.Loc.T("cmd.newTab"), () => ActiveView.NewTab(), Key.T, ModifierKeys.Control);
        Add("tab.close", Foldy.Core.Localization.Loc.T("cmd.closeTab"), () => { if (Shell.ActivePane.ActiveTab is { } t) Shell.ActivePane.CloseTab(t); }, Key.W, ModifierKeys.Control);
        Add("tab.next", Foldy.Core.Localization.Loc.T("cmd.nextTab"), () => Shell.ActivePane.ActivateRelative(1), Key.Tab, ModifierKeys.Control);
        Add("tab.prev", Foldy.Core.Localization.Loc.T("cmd.prevTab"), () => Shell.ActivePane.ActivateRelative(-1), Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);
        Add("search.focus", Foldy.Core.Localization.Loc.T("cmd.search"), () => ActiveView.FocusSearch(), Key.F, ModifierKeys.Control, inText: true);
        Add("nav.path", Foldy.Core.Localization.Loc.T("cmd.path"), () => ActiveView.BeginPathEdit(), Key.L, ModifierKeys.Control, inText: true);
        Add("nav.refresh", Foldy.Core.Localization.Loc.T("cmd.refresh"), () => _ = activeTab()?.RefreshAsync(), Key.R, ModifierKeys.Control);
        Add("nav.back", Foldy.Core.Localization.Loc.T("cmd.back"), () => _ = activeTab()?.GoBackAsync(), Key.Left, ModifierKeys.Alt);
        Add("nav.forward", Foldy.Core.Localization.Loc.T("cmd.forward"), () => _ = activeTab()?.GoForwardAsync(), Key.Right, ModifierKeys.Alt);
        Add("nav.up", Foldy.Core.Localization.Loc.T("cmd.up"), () => _ = activeTab()?.GoUpAsync(), Key.Up, ModifierKeys.Alt);
        Add("terminal.open", Foldy.Core.Localization.Loc.T("cmd.terminal"), () => Shell.OpenTerminal(TerminalKind.Default, false), Key.Oem3, ModifierKeys.Control, "Ctrl+`");
        Add("terminal.admin", Foldy.Core.Localization.Loc.T("cmd.terminalAdmin"), () => Shell.OpenTerminal(TerminalKind.Default, true), Key.Oem3, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+`");
        Add("terminal.cmd", Foldy.Core.Localization.Loc.T("cmd.cmd"), () => Shell.OpenTerminal(TerminalKind.Cmd, false));
        Add("terminal.cmdAdmin", Foldy.Core.Localization.Loc.T("cmd.cmdAdmin"), () => Shell.OpenTerminal(TerminalKind.Cmd, true));
        Add("view.hidden", Foldy.Core.Localization.Loc.T("cmd.hidden"), ToggleHidden, Key.H, ModifierKeys.Control);
        Add("analysis.open", Foldy.Core.Localization.Loc.T("sw.c.analysis"), () => _ = AskAnalysisTargetAsync());
        Add("ai.connect", Foldy.Core.Localization.Loc.T("sw.c.ai"), OpenMcp);
        Add("compare.panes", Foldy.Core.Localization.Loc.T("sw.c.compare"), () =>
        {
            if (Shell.Left.ActiveFolder?.Path is { Length: > 2 } l && Shell.Right.ActiveFolder?.Path is { Length: > 2 } r && !Foldy.Presentation.Navigation.PathUtil.IsSftp(l) && !Foldy.Presentation.Navigation.PathUtil.IsSftp(r))
            {
                _ = CompareFlow.RunAsync(this, l, r, Shell);
            }
            else
            {
                Shell.Status = Foldy.Core.Localization.Loc.T("sw.c.compareNeed");
            }
        });
        Add("cleanup.logs", Foldy.Core.Localization.Loc.T("sw.c.logCleanup"), () => OpenLogCleanup(null));
        Add("cleanup.downloads", Foldy.Core.Localization.Loc.T("sw.c.downloads"), () => OpenDownloadsCleanup(null));
        Add("cleanup.desktop", Foldy.Core.Localization.Loc.T("sw.c.desktop"), OpenDesktopTidy);
        Add("cleanup.wizard", Foldy.Core.Localization.Loc.T("sw.c.tidyWizard"), () => _ = OpenTidyWizardAsync());
        Add("cleanup.rebuildable", Foldy.Core.Localization.Loc.T("sw.c.rebuildable"), () => OpenRebuildable(Shell.ActivePane.ActiveFolder?.Path));
        Add("cleanup.devcache", Foldy.Core.Localization.Loc.T("sw.c.devcache"), () => _ = DevCleanupFlows.DevCachesAsync(this));
        Add("memo.new", Foldy.Core.Localization.Loc.T("sw.c.newMemo"), () =>
        {
            if (Shell.ActivePane.ActiveFolder?.Path is { Length: > 2 } p && p[1] == ':')
            {
                var path = Foldy.Core.Vault.SecureZip.Unique(Path.Combine(p, Foldy.Core.Localization.Loc.T("sw.memo.file")));
                File.WriteAllText(path, Foldy.Core.Localization.Loc.T("sw.memo.body"));
                MemoWindow.OpenMarkdown(path).Show();
                _ = Shell.RefreshFoldersAsync([p]);
            }
        });
        Add("secure.newMemo", Foldy.Core.Localization.Loc.T("sw.c.newLockedMemo"), () =>
        {
            // 지금 폴더가 내 PC의 폴더가 아니면(처음 화면·서버 등) 아무 일도 안 일어난 것처럼 보이지 않게 '문서'에 만든다
            var p = Shell.ActivePane.ActiveFolder?.Path is { Length: > 2 } here && here[1] == ':' && Directory.Exists(here)
                ? here
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _ = SecureFlows.NewMemoAsync(this, p, Shell);
        });
        Add("secure.lock", Foldy.Core.Localization.Loc.T("sw.c.encrypt"), () =>
        {
            var sel = ShellViewModel.SelectedPaths(Shell.ActivePane);
            if (sel.Count == 1 && File.Exists(sel[0]) && !SecureFlows.IsLocked(sel[0]))
            {
                _ = SecureFlows.LockFileAsync(this, sel[0], Shell);
            }
            else
            {
                // 고른 파일이 없으면 상태 줄에 작게 쓰는 대신 고르는 창을 바로 띄운다 (사용자 2026-11-22: 어떻게 써야 할지 모르겠다)
                SecureFlows.PickFileToLock(this, Shell, LocalHere());
            }
        });
        Add("secure.vault", Foldy.Core.Localization.Loc.T("sw.c.vault"), () =>
        {
            var sel = ShellViewModel.SelectedPaths(Shell.ActivePane);
            if (sel.Count == 1 && Directory.Exists(sel[0]))
            {
                _ = SecureFlows.PutInVaultAsync(this, sel[0], Shell);
            }
            else
            {
                SecureFlows.PickFolderForVault(this, Shell, LocalHere());
            }
        });
        Add("file.versions", Foldy.Core.Localization.Loc.T("sw.c.versions"), () =>
        {
            // 덮어쓴 파일 되살리기: 오른쪽 클릭에만 있던 것을 지키기 메뉴·무엇이든 찾기에서도 (2026-11-22 기능 찾기)
            var sel = ShellViewModel.SelectedPaths(Shell.ActivePane);
            if (sel.Count == 1 && File.Exists(sel[0]))
            {
                ShowVersions(sel[0]);
            }
            else
            {
                Shell.Status = Foldy.Core.Localization.Loc.T("sw.c.versionsNeed");
            }
        });
        Add("tag.done", Foldy.Core.Localization.Loc.T("sw.c.done"), () => ActiveView.ToggleTag(Foldy.Core.Tags.FileTags.Default), gesture: "Ctrl+D");
        foreach (var t in Foldy.Core.Tags.FileTags.Builtin.Skip(1))
        {
            Add("tag." + t.Name, Foldy.Core.Localization.Loc.F("sw.tag.toggle", Foldy.Core.Tags.FileTags.DisplayName(t.Name)), () => ActiveView.ToggleTag(t.Name));
        }

        Add("folder.manage", Foldy.Core.Localization.Loc.T("sw.c.method"), () =>
        {
            if (Shell.ActivePane.ActiveFolder?.Path is { Length: > 2 } p && p[1] == ':')
            {
                ManageFolder(p);
            }
            else
            {
                Shell.Status = Foldy.Core.Localization.Loc.T("sw.c.methodNeed");
            }
        });
        Add("view.preview", Foldy.Core.Localization.Loc.T("cmd.preview"), () => ActiveView.TogglePreview(), Key.F3);
        Add("view.zoomIn", Foldy.Core.Localization.Loc.T("sw.c.zoomIn"), () => StepUiScale(1), Key.OemPlus, ModifierKeys.Control, "Ctrl+=");
        Add("view.zoomOut", Foldy.Core.Localization.Loc.T("sw.c.zoomOut"), () => StepUiScale(-1), Key.OemMinus, ModifierKeys.Control, "Ctrl+-");
        Add("view.zoomReset", Foldy.Core.Localization.Loc.T("sw.c.zoomAuto"), () => SetUiScale(0), Key.D0, ModifierKeys.Control, "Ctrl+0");
        Add("view.tree", Foldy.Core.Localization.Loc.T("cmd.tree"), () => ActiveView.ToggleTree(), Key.B, ModifierKeys.Control);
        Add("view.sidebar", Foldy.Core.Localization.Loc.T("cmd.sidebar"), () => SidebarExpanded = !SidebarExpanded, Key.B, ModifierKeys.Control | ModifierKeys.Shift);
        Add("sidebar.pin", Foldy.Core.Localization.Loc.T("cmd.pin"), TogglePinCurrent);
        Add("view.themeSystem", Foldy.Core.Localization.Loc.T("sw.c.themeSystem"), () => SetTheme(FoldyTheme.System));
        Add("view.themeLight", Foldy.Core.Localization.Loc.T("sw.c.themeLight"), () => SetTheme(FoldyTheme.Light));
        Add("view.themeDark", Foldy.Core.Localization.Loc.T("sw.c.themeDark"), () => SetTheme(FoldyTheme.Dark));
        Add("view.themeRetro", Foldy.Core.Localization.Loc.T("sw.c.themeRetro"), () => SetTheme(FoldyTheme.Retro));
        Add("sftp.connect", Foldy.Core.Localization.Loc.T("sw.c.sftp"), ConnectSftp, Key.O, ModifierKeys.Control | ModifierKeys.Shift);
        Add("indexer.install", Foldy.Core.Localization.Loc.T("sw.c.idxInstall"), () => RunIndexerSetup(install: true));
        Add("indexer.uninstall", Foldy.Core.Localization.Loc.T("sw.c.idxRemove"), () => RunIndexerSetup(install: false));
        Add("app.palette", Foldy.Core.Localization.Loc.T("cmd.palette"), ShowPalette, Key.P, ModifierKeys.Control | ModifierKeys.Shift, inText: true);
        Add("app.quickSearch", Foldy.Core.Localization.Loc.T("sw.c.quickSearch"), ShowQuickSearch, Key.Space, ModifierKeys.Control, inText: true);
        Add("app.help", Foldy.Core.Localization.Loc.T("sw.c.help"), ShowHelp, Key.F1, inText: true);
        Add("app.tour", Foldy.Core.Localization.Loc.T("sw.c.tour"), () => _ = FirstRunTour.RunAsync(this));
        Add("app.settings", Foldy.Core.Localization.Loc.T("sw.c.settings"), ShowSettings, Key.OemComma, ModifierKeys.Control, inText: true);
        Add("app.reportBug", Foldy.Core.Localization.Loc.T("sw.c.bug"), App.OpenBugReport);
        Add("app.emptyRecycleBin", Foldy.Core.Localization.Loc.T("sw.c.emptyBin"), () => OnEmptyRecycleBin(this, new RoutedEventArgs()));
        Add("app.diagnostic", Foldy.Core.Localization.Loc.T("sw.c.diag"), ToggleDiagnostic);
        Add("app.logs", Foldy.Core.Localization.Loc.T("sw.c.logs"), OpenLogFolder);
        Add("app.about", Foldy.Core.Localization.Loc.T("sw.c.about"), ShowAbout);
        Add("app.checkUpdate", Foldy.Core.Localization.Loc.T("cmd.update"), CheckUpdateNow);
        ApplyKeyMap();
    }

    /// <summary>keymap.json의 재지정을 명령 단축키에 반영한다. KeyMap이 모르는 명령은 기본값 그대로.</summary>
    private void ApplyKeyMap()
    {
        var known = _keyMap.Commands.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < _commands.Count; i++)
        {
            var c = _commands[i];
            if (!known.Contains(c.Id) || !_keyMap.IsOverridden(c.Id))
            {
                continue;
            }

            var g = _keyMap.GetGesture(c.Id);
            if (g is null)
            {
                _commands[i] = c with { Key = Key.None, Mods = ModifierKeys.None, Gesture = "" };
            }
            else if (Foldy.Presentation.Commands.KeyGesture.TryParse(g, out var kg) && ToWpfKey(kg.Key) is { } k)
            {
                var mods = ModifierKeys.None;
                if (kg.Modifiers.HasFlag(Foldy.Presentation.Commands.GestureModifiers.Ctrl)) mods |= ModifierKeys.Control;
                if (kg.Modifiers.HasFlag(Foldy.Presentation.Commands.GestureModifiers.Alt)) mods |= ModifierKeys.Alt;
                if (kg.Modifiers.HasFlag(Foldy.Presentation.Commands.GestureModifiers.Shift)) mods |= ModifierKeys.Shift;
                if (kg.Modifiers.HasFlag(Foldy.Presentation.Commands.GestureModifiers.Win)) mods |= ModifierKeys.Windows;
                _commands[i] = c with { Key = k, Mods = mods, Gesture = g };
            }
        }
    }

    internal static Key? ToWpfKey(string name)
    {
        if (name.Length == 1 && char.IsAsciiLetterUpper(name[0])) return Enum.Parse<Key>(name);
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) return Enum.Parse<Key>("D" + name);
        return name switch
        {
            "Enter" => Key.Return, "Escape" => Key.Escape, "Tab" => Key.Tab, "Space" => Key.Space,
            "Backspace" => Key.Back, "Delete" => Key.Delete, "Insert" => Key.Insert, "Home" => Key.Home,
            "End" => Key.End, "PageUp" => Key.PageUp, "PageDown" => Key.PageDown,
            "Up" => Key.Up, "Down" => Key.Down, "Left" => Key.Left, "Right" => Key.Right,
            "`" => Key.Oem3, "-" => Key.OemMinus, "=" => Key.OemPlus, "[" => Key.OemOpenBrackets,
            "]" => Key.Oem6, ";" => Key.Oem1, "'" => Key.Oem7, "," => Key.OemComma, "." => Key.OemPeriod,
            "/" => Key.Oem2, "\\" => Key.Oem5,
            _ when name.StartsWith('F') && Enum.TryParse<Key>(name, out var f) => f,
            _ => null,
        };
    }

    private void ShowSettings()
    {
        var vm = new Foldy.Presentation.Settings.SettingsViewModel(_settings, _keyMap);
        vm.Applied += (_, e) =>
        {
            _settings = e.Settings;
            SaveSettings();
            ThemeManager.SetMode(_settings.Theme switch
            {
                "light" => FoldyTheme.Light,
                "dark" => FoldyTheme.Dark,
                "retro" => FoldyTheme.Retro,
                _ => FoldyTheme.System,
            });
            ThemeManager.SetAccent(_settings.Accent);
            ApplyDensity();
            _keyMap = e.KeyMap;
            try
            {
                _keyMap.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "단축키 저장 실패");
            }

            _commands.Clear();
            BuildCommands();
            _ = EnsureQuickSearchHotkeyAsync(null); // 단축키를 바꿨으면 다시 등록 (겹치면 알림)
            StartSlowCrawl(); // "색인 없는 드라이브도 천천히 모으기" 켜고 끄기
            UpdateIndexText();
            _ = Shell.Left.ActiveFolder?.RefreshAsync();
            _ = Shell.Right.ActiveFolder?.RefreshAsync();
            LeftPane.RefreshGit();
            RightPane.RefreshGit();
        };
        new Foldy.UI.Settings.SettingsWindow(vm) { Owner = this }.ShowDialog();
    }

    private void OnSettings(object sender, RoutedEventArgs e) => ShowSettings();

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

    private void FocusSidebar()
    {
        // 첫 번째 사이드바 버튼(접기/펼치기)으로 포커스를 옮긴다. 거기서 Tab/Shift+Tab, Enter, Esc(목록으로 복귀).
        if (Sidebar.Visibility == Visibility.Visible && Sidebar.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
        {
            return;
        }

        Sidebar.Focus();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var inText = Keyboard.FocusedElement is TextBox;

        // 키보드 접근성: 사이드바·도구 줄의 버튼에 포커스가 있을 때는 Tab·Del·F2를 파일 목록 전용으로 가로채지 않는다.
        // (Tab이 늘 "다른 패널로 이동"이면 버튼에 키보드로 들어갈 수도, 지나갈 수도 없다.)
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase)
        {
            if (mods == ModifierKeys.None && key == Key.Escape)
            {
                ActiveView.FocusList(); // Esc: 버튼에서 파일 목록으로 돌아간다
                e.Handled = true;
                return;
            }

            if ((key is Key.Tab or Key.Delete or Key.F2) && (mods & ModifierKeys.Control) == 0)
            {
                return;
            }
        }

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

    /// <summary>
    /// 정리 위저드 (사용자 2026-11-21: "정리한다고 하면 대화형으로 위저드 스타일로 따라만 하면 쉽게"):
    /// ① 어디를 정리할까요? (바탕화면 · 다운로드 · 지금 폴더 · 다른 폴더) → 바탕화면·다운로드는 원래의 대화형 정리로 이어지고,
    /// ② 폴더는 "어떻게 나눌까요?" (종류별 · 날짜별 · PARA 네 칸) → ③ 정리 후 모습을 먼저 보여 주는 창(계획을 미리 만들어 둠) → 사용자가 [정리하기]. 되돌리기 가능, 지우는 것 없음.
    /// </summary>
    private async Task OpenTidyWizardAsync()
    {
        var w = new GuideWindow(Foldy.Core.Localization.Loc.T("sw.tidy.title")) { Owner = this };
        w.Show();
        var here = Shell.ActivePane.ActiveFolder?.Path;
        var canHere = !string.IsNullOrEmpty(here) && !Foldy.Presentation.Navigation.PathUtil.IsSftp(here) && Directory.Exists(here);
        w.Say(Foldy.Core.Localization.Loc.T("sw.tidy.where"));
        var choices = new List<(string, string)> { ("desktop", Foldy.Core.Localization.Loc.T("sw.tidy.desktop")), ("downloads", Foldy.Core.Localization.Loc.T("sw.tidy.downloads")) };
        if (canHere)
        {
            var name = Path.GetFileName(here!.TrimEnd('\\'));
            choices.Add(("here", Foldy.Core.Localization.Loc.F("sw.tidy.here", (name.Length > 0 ? name : here))));
        }

        choices.Add(("pick", Foldy.Core.Localization.Loc.T("sw.tidy.other")));
        var where = await w.AskAsync(null, [.. choices]);
        string? folder = null;
        switch (where)
        {
            case "desktop":
                w.Close();
                OpenDesktopTidy(); // 원래의 대화형 바탕화면 정리 (종류별 보관, 되돌리기)
                return;
            case "downloads":
                w.Close();
                OpenDownloadsCleanup(null); // 원래의 대화형 다운로드 정리 (종류마다 하나씩 물어봄)
                return;
            case "here":
                folder = here;
                break;
            case "pick":
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Foldy.Core.Localization.Loc.T("sw.tidy.pick"), InitialDirectory = canHere ? here! : "" };
                folder = dlg.ShowDialog(w) == true ? dlg.FolderName : null;
                break;
            default:
                return;
        }

        if (string.IsNullOrEmpty(folder))
        {
            w.Close();
            return;
        }

        var how = await w.AskAsync(Foldy.Core.Localization.Loc.F("sw.tidy.how", folder),
            ("type", Foldy.Core.Localization.Loc.T("sw.tidy.byType")), ("date", Foldy.Core.Localization.Loc.T("sw.tidy.byDate")), ("para", Foldy.Core.Localization.Loc.T("sw.tidy.para")));
        if (how == GuideWindow.ClosedChoice)
        {
            return;
        }

        w.Say(Foldy.Core.Localization.Loc.T("sw.tidy.preview"));
        await Task.Delay(400);
        w.Close();
        var org = new Foldy.Presentation.Templates.OrganizeViewModel(folder, new WpfDispatcher(Dispatcher), templates: Templates.Templates);
        org.SelectedMode = org.Modes.First(m => m.Mode == how switch
        {
            "date" => Foldy.Core.Organize.OrganizeMode.ByDate,
            "para" => Foldy.Core.Organize.OrganizeMode.ByTemplate,
            _ => Foldy.Core.Organize.OrganizeMode.ByType,
        });
        if (how == "para")
        {
            org.SelectedTemplate = org.Templates.FirstOrDefault(t => t.Id == "para") ?? org.SelectedTemplate;
        }

        _ = org.PlanCommand.ExecuteAsync(null); // 창이 열리면 바로 정리 후 모습이 보이게
        Foldy.UI.Templates.TemplateDialogs.ShowOrganize(this, org);
        _ = Shell.RefreshFoldersAsync([folder]);
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

    private void CheckUpdateNow() => _ = CheckAndOfferUpdateAsync(interactive: true);

    private void OnCheckUpdate(object sender, RoutedEventArgs e) => _ = CheckAndOfferUpdateAsync(interactive: true);

    /// <summary>[업데이트] 버튼: 바로 확인하고, 새 버전이 있으면 설치를 묻고, 실패하면 이유를 보여 준다.</summary>
    private async Task CheckAndOfferUpdateAsync(bool interactive)
    {
        if (Foldy.Platform.SystemInfo.AppPackage.IsPackaged)
        {
            // Store 판: Store의 다운로드 및 업데이트 화면을 연다
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.store.check");
            try
            {
                Process.Start(new ProcessStartInfo("ms-windows-store://downloadsandupdates") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warning(ex, "Store 업데이트 화면을 열지 못함");
            }

            return;
        }

        // 받아 둔 업데이트가 있어도 서비스에는 한 번 더 확인한다: 그사이 더 새 버전이 나왔으면 그것으로 바뀐다
        // (2026-11-21 사용자: "업데이트 정보를 받은 상태에서는 새 버전이 있어도 새 버전으로 업데이트하지 않는다")
        if (_updater.ReadyVersion is null || _updater is ServiceUpdater)
        {
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.upd.checking");
            bool found;
            try
            {
                found = await _updater.CheckAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warning(ex, Foldy.Core.Localization.Loc.T("sw.upd.checkFailedShort"));
                found = false;
            }

            if (interactive)
            {
                _triedUserProxyFallback = false; // 수동 확인은 매번 사용자 프록시 경로도 다시 시도
            }

            if (!found && _updater.ReadyVersion is null && await TryUserProxyFallbackAsync())
            {
                found = true;
            }

            if (!found && _updater.ReadyVersion is null)
            {
                var err = _updater.LastError;
                if (_userProxyUpToDate)
                {
                    err = null; // 서비스는 회사망에서 못 나가도, 사용자 프록시로 확인해 보니 최신 버전
                }
                else if (err is not null && _userProxyError is { } ue)
                {
                    err = Foldy.Core.Localization.Loc.F("sw.upd.userProxy", err, ue);
                }
                Shell.Status = err is null ? Foldy.Core.Localization.Loc.F("sw.upd.latest", AppVersion) : Foldy.Core.Localization.Loc.F("sw.upd.checkFailed", err);
                if (interactive && err is not null) // 최신이면 상태 줄로만 (누를 단추를 늘리지 않는다, 원칙 1.4)
                {
                    Foldy.UI.Dialogs.FoldyDialog.Info(this, Foldy.Core.Localization.Loc.T("sw.upd.title"),
                        err.Contains(AppUpdater.MissingVerifier, StringComparison.Ordinal) ? Foldy.Core.Localization.Loc.F("sw.upd.checkFailedDlg", err)
                            : Foldy.Core.Localization.Loc.F("sw.upd.checkFailedNet", err),
                        Foldy.UI.Dialogs.DialogKind.Warning);
                }

                return;
            }
        }

        UpdateUpdateButton();
        var v = _updater.ReadyVersion;
        Shell.Status = Foldy.Core.Localization.Loc.F("sw.upd.ready", v);
        if (interactive && Foldy.UI.Dialogs.FoldyDialog.Confirm(this, Foldy.Core.Localization.Loc.T("sw.upd.title"), Foldy.Core.Localization.Loc.F("sw.upd.readyAsk", v, AppVersion), Foldy.Core.Localization.Loc.T("sw.upd.installNow"), Foldy.Core.Localization.Loc.T("sw.later")))
        {
            OnInstallUpdate(this, new RoutedEventArgs());
        }
    }

    private DispatcherTimer? _updateAfterJobs;

    private void UpdateUpdateButton()
    {
        if (_updater.ReadyVersion is { } v)
        {
            UpdateButton.Content = _updateAfterJobs is null
                ? Foldy.Core.Localization.Loc.F("sw.upd.restart", v)
                : Foldy.Core.Localization.Loc.F("sw.upd.afterJobs", v);
            UpdateButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// [재시작하고 업데이트]. 진행 중인 작업(복사·이동·삭제)이 있으면 끊지 않고 예약해 두었다가,
    /// 모두 끝나면 자동으로 설치·재시작한다. 예약 중에 다시 누르면 취소.
    /// </summary>
    private void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_updateAfterJobs is not null)
        {
            _updateAfterJobs.Stop();
            _updateAfterJobs = null;
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.upd.unscheduled");
            UpdateUpdateButton();
            return;
        }

        if (Shell.Jobs.Any(j => !j.IsFinished))
        {
            _updateAfterJobs = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
            {
                if (!Shell.Jobs.Any(j => !j.IsFinished))
                {
                    _updateAfterJobs?.Stop();
                    _updateAfterJobs = null;
                    InstallNow();
                }
            }, Dispatcher);
            _updateAfterJobs.Start();
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.upd.afterJobsSet");
            UpdateUpdateButton();
            return;
        }

        InstallNow();
    }

    private void InstallNow()
    {
        var version = _updater.ReadyVersion;
        if (_updater.LaunchInstaller())
        {
            if (_updater is ServiceUpdater && version is not null)
            {
                // Foldy가 닫혀 있는 동안 단계를 보여 주는 작은 창 (새 Foldy가 뜨면 닫힘). 애니메이션을 켰으면 연주·우진도 함께
                UpdateProgressWindow.Show(version, _settings.Animations ? UpdateProgressWindow.RenderFrames() : null);
            }

            Close(); // 세션 저장 후 종료, 설치가 끝나면 Foldy를 다시 띄운다
        }
        else
        {
            var why = _updater is ServiceUpdater
                ? Foldy.Core.Localization.Loc.F("sw.upd.installFailed", _updater.LastError)
                : Foldy.Core.Localization.Loc.T("sw.upd.uacCancelled");
            Shell.Status = why;
            UpdateUpdateButton();
            Foldy.UI.Dialogs.FoldyDialog.Info(this, Foldy.Core.Localization.Loc.T("sw.upd.title"), why, Foldy.UI.Dialogs.DialogKind.Warning);
        }
    }

    private async void RunIndexerSetup(bool install)
    {
        if (install && Foldy.Platform.SystemInfo.AppPackage.IsPackaged)
        {
            // Store 판에는 SYSTEM 서비스를 넣지 않는다 (docs/store.md)
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.idx.storeOnly");
            return;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, "foldy-index.exe");
        if (install && !File.Exists(exe))
        {
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.idx.noExe");
            return;
        }

        if (install && !Foldy.Platform.SystemInfo.IndexerService.IsProtectedLocation(exe))
        {
            // SYSTEM 서비스 exe가 사용자 폴더에 있으면 바꿔치기로 권한을 빼앗길 수 있다
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.idx.needInstall");
            return;
        }

        try
        {
            using var p = Process.Start(install ? Foldy.Platform.SystemInfo.IndexerService.BuildInstall(exe) : Foldy.Platform.SystemInfo.IndexerService.BuildUninstall());
            if (p is not null)
            {
                await p.WaitForExitAsync();
            }

            Shell.Status = install ? Foldy.Core.Localization.Loc.T("sw.idx.installed") : Foldy.Core.Localization.Loc.T("sw.idx.removed");
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
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.uacCancelled");
        }
    }

    private void ConnectSftp() => ConnectSftp(Shell.ActivePane);

    /// <summary>저장된 서버 고르기·새 연결 창을 열고, 연결되면 그 패널에 새 탭으로 연다.</summary>
    public void ConnectSftp(PaneViewModel pane)
    {
        var dlg = new SftpConnectWindow(_sftp) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is { } url)
        {
            pane.NewFolderTab(url);
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
        Shell.Status = _settings.ShowHidden ? Foldy.Core.Localization.Loc.T("sw.hidden.show") : Foldy.Core.Localization.Loc.T("sw.hidden.hide");
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

    private static readonly double[] Scales = [1.0, 1.15, 1.3, 1.5, 1.75, 2.0];

    /// <summary>Windows 접근성의 "텍스트 크기" (100~225%). WPF는 이 값을 스스로 따르지 않아 시작할 때 읽는다.</summary>
    private static double WindowsTextScale()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            return k?.GetValue("TextScaleFactor") is int v && v is >= 100 and <= 225 ? v / 100.0 : 1.0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return 1.0;
        }
    }

    private double EffectiveUiScale => _settings.UiScale > 0 ? _settings.UiScale : WindowsTextScale();

    /// <summary>화면 크기 (Ctrl+= 크게, Ctrl+- 작게, Ctrl+0 보통). 창 전체를 같은 배율로 키워 글씨·버튼이 함께 커진다.</summary>
    /// <summary>0이면 Windows 설정 따르기로 돌아간다.</summary>
    private void SetUiScale(double scale)
    {
        scale = scale <= 0 ? 0 : Math.Clamp(scale, Scales[0], Scales[^1]);
        _settings = _settings with { UiScale = scale };
        SaveSettings();
        ApplyUiScale();
        Shell.Status = scale <= 0 ? Foldy.Core.Localization.Loc.F("sw.scale.auto", EffectiveUiScale * 100) : Foldy.Core.Localization.Loc.F("sw.scale.n", scale * 100);
    }

    private void StepUiScale(int dir)
    {
        var now = EffectiveUiScale;
        var next = dir > 0 ? Scales.FirstOrDefault(s => s > now + 0.01, Scales[^1]) : Scales.LastOrDefault(s => s < now - 0.01, Scales[0]);
        SetUiScale(next);
    }

    private void ApplyUiScale()
    {
        App.UiScale = EffectiveUiScale;
        App.ApplyScale(this);
    }

    private void OnUiScale(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
        {
            SetUiScale(s);
        }
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

    // ───────────── 빠른 검색 (Ctrl+Space) ─────────────

    private QuickSearchWindow? _quickSearch;
    private IReadOnlyList<Foldy.Core.QuickSearch.StartApp>? _startApps;

    /// <summary>
    /// 빠른 검색 창 (macOS Spotlight처럼): 계산·단위 변환, 앱(시작 메뉴), Windows 설정, 자주 쓰는/최근 폴더, Foldy 기능, 파일.
    /// 다른 프로그램을 쓰는 중에도 단축키로 열린다(전역 단축키). 파일은 색인이 있으면 모든 드라이브, 없으면 지금 폴더 안.
    /// </summary>
    private void ShowQuickSearch()
    {
        if (_quickSearch is { IsLoaded: true })
        {
            _quickSearch.Activate();
            return;
        }

        _startApps ??= Foldy.Core.QuickSearch.SystemCatalog.LoadStartApps();
        var scope = Shell.ActivePane.ActiveFolder?.Path;
        var indexing = _search.LastStatus is { State: Foldy.Indexer.Client.IndexState.Indexing } ix ? ix.ProgressPermille / 10 : -1;
        var footer = (_search.IsIndexed
                ? (scope is not null && !_search.IsIndexedPath(scope) && !Foldy.Presentation.Navigation.PathUtil.IsSftp(scope)
                    ? Foldy.Core.Localization.Loc.F("sw.qs.footerHere", Path.GetFileName(scope.TrimEnd('\\')))
                    : Foldy.Core.Localization.Loc.T("sw.qs.footerAll"))
                : indexing >= 0
                    ? Foldy.Core.Localization.Loc.F("sw.qs.footerIndexing", indexing)
                : Foldy.Core.Localization.Loc.F("sw.qs.footerNoService", Path.GetFileName(scope?.TrimEnd('\\') ?? "")))
            + Foldy.Core.Localization.Loc.T("sw.qs.keys") + Foldy.Core.Localization.Loc.T("qs.filterKeys");
        _quickSearch = BuildQuickSearch(scope, footer);
        _quickSearch.Closed += (_, _) => _quickSearch = null;
        _quickSearch.Show();
    }

    private QuickSearchWindow BuildQuickSearch(string? scope, string footer) => new(
        QuickSearchInstant,
        (q, ct) => _search.SearchAsync(Foldy.Core.QuickSearch.QuickIntent.Normalize(q), scope, ct),
        f => new QuickSearchWindow.Hit(
            Foldy.Core.Localization.Loc.T("sw.qs.file"),
            f.Kind == Foldy.Core.FileSystem.FsKind.Folder ? "\uE8B7" : "\uE8A5",
            f.Name,
            Path.GetDirectoryName(f.Path.Value) ?? "",
            () => { BringToFront(); _ = Shell.OpenAsync(Shell.ActivePane, new Foldy.Presentation.Files.FileItem(f)); },
            () => RevealInFoldy(f.Path.Value),
            Preview: () => QuickSearchWindow.FilePreview(f)),
        footer,
        _settings.Animations);

    /// <summary>바로 찾는 것: 계산·단위 변환, 앱, Windows 설정, 폴더(고정·최근), Foldy 기능. 빈 칸이면 자주 쓰는 것.</summary>
    private IEnumerable<QuickSearchWindow.Hit> QuickSearchInstant(string q)
    {
        var m = new QuickMatcher(Foldy.Core.QuickSearch.QuickIntent.Normalize(q));
        if (Foldy.Core.QuickSearch.QuickCalc.Answer(q) is { } ans)
        {
            yield return new QuickSearchWindow.Hit(ans.Kind, "\uE8EF", ans.Display, Foldy.Core.Localization.Loc.T("sw.qs.copyResult"), () =>
            {
                try
                {
                    Clipboard.SetText(ans.Copy);
                    ShowStatus(Foldy.Core.Localization.Loc.F("sw.qs.copied", ans.Copy));
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                }
            }, Score: 200);
        }

        // 폴더: 고정·최근 (빈 칸이면 고정 폴더를 먼저 보여 준다)
        foreach (var f in _quick.Pinned.Concat(_quick.Recent).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var s = q.Length == 0 ? (_quick.IsPinned(f.Path) ? 1 : 0) : m.Score(f.Name);
            if (s > 0)
            {
                var path = f.Path;
                yield return new QuickSearchWindow.Hit(Foldy.Core.Localization.Loc.T("sw.qs.folder"), "\uE8B7", f.Name, path,
                    () => { BringToFront(); OpenInPane(Shell.ActivePane, path, newTab: false); }, () => RevealInFoldy(path), q.Length == 0 ? 0 : s + 5);
            }
        }

        if (q.Length == 0)
        {
            foreach (var c in QuickSearchCommands(""))
            {
                yield return CommandHit(c, 0);
            }

            yield break;
        }

        foreach (var a in _startApps ?? [])
        {
            if (m.Score(a.Name) is > 0 and var s)
            {
                var lnk = a.ShortcutPath;
                yield return new QuickSearchWindow.Hit(Foldy.Core.Localization.Loc.T("sw.qs.app"), "\uE7AC", a.Name, Foldy.Core.Localization.Loc.T("sw.qs.runApp"), () => StartShell(lnk), Score: s + 10);
            }
        }

        // Windows 설정·제어판·관리 도구: 이름, 또는 쉬운 말에 친 낱말이 모두 있으면 ("프로그램 추가/삭제", "제어판", "드라이버")
        var nq = Foldy.Core.QuickSearch.QuickIntent.Normalize(q);
        foreach (var p in Foldy.Core.QuickSearch.SystemCatalog.SettingsPages)
        {
            var s = Math.Max(m.Score(p.Title), Math.Max(m.Score(p.Keywords) / 2, Foldy.Core.QuickSearch.QuickSearchMatch.ScoreWords(p.Title + " " + p.Keywords, nq)));
            if (s > 0)
            {
                var item = p;
                var detail = p.Kind switch
                {
                    Foldy.Core.QuickSearch.SystemItemKind.ControlPanel => Foldy.Core.Localization.Loc.T("qs.ctrlPanel"),
                    Foldy.Core.QuickSearch.SystemItemKind.Tool => Foldy.Core.Localization.Loc.T("qs.winTool"),
                    _ => Foldy.Core.Localization.Loc.T("sw.qs.winSettings"),
                };
                yield return new QuickSearchWindow.Hit(Foldy.Core.Localization.Loc.T("qs.grp.settings"), "\uE713", p.Title, detail, () => StartSystemItem(item), Score: s);
            }
        }

        var sentence = Foldy.Core.QuickSearch.QuickIntent.IsSentence(q);
        foreach (var c in QuickSearchCommands(q))
        {
            var hit = CommandHit(c, Foldy.Core.QuickSearch.QuickIntent.Score(c.Id, c.Title, q) + 2);
            // 말하듯 물으면 대답하듯: "이걸 할까요?" (Enter면 그 기능의 대화 창이 이어서 한 단계씩 물어본다)
            yield return sentence ? hit with { Detail = Foldy.Core.Localization.Loc.T("sw.qs.intent") } : hit;
        }
    }

    private sealed class QuickMatcher(string q)
    {
        public int Score(string title) => Foldy.Core.QuickSearch.QuickSearchMatch.Score(title, q);
    }

    private QuickSearchWindow.Hit CommandHit(PaletteCommand c, int score) =>
        new(Foldy.Core.Localization.Loc.T("qs.grp.feature"), "\uE945", c.Title, c.Gesture.Length > 0 ? Foldy.Core.Localization.Loc.F("sw.qs.feature", c.Gesture) : Foldy.Core.Localization.Loc.T("sw.qs.featureFoldy"), () => { BringToFront(); c.Run(); }, Score: score);

    /// <summary>빠른 검색의 기능 찾기: 이름에 들어 있거나, 띄어 쓴 낱말이 모두 들어 있거나, 초성(ㅅㅈ → 설정)이 맞는 명령.</summary>
    private IEnumerable<PaletteCommand> QuickSearchCommands(string q)
    {
        var all = _commands.Where(c => c.Id != "app.quickSearch").GroupBy(c => c.Title).Select(g => g.First()).ToList();
        if (q.Length == 0)
        {
            string[] starters = ["app.settings", "analysis.open", "app.help"];
            return starters.Select(id => all.FirstOrDefault(c => c.Id == id)).OfType<PaletteCommand>();
        }

        return all.Where(c => Foldy.Core.QuickSearch.QuickIntent.Score(c.Id, c.Title, q) > 0);
    }

    private void StartShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            BringToFront();
            ShowStatus(Foldy.Core.Localization.Loc.F("sw.cantOpen", ex.Message));
        }
    }

    /// <summary>Windows 설정 화면·제어판·관리 도구를 연다 (제어판·도구는 System32의 파일만).</summary>
    private void StartSystemItem(Foldy.Core.QuickSearch.SettingsPage p)
    {
        var (file, args) = p.LaunchTarget();
        try
        {
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            BringToFront();
            ShowStatus(Foldy.Core.Localization.Loc.F("sw.cantOpen", ex.Message));
        }
    }

    /// <summary>들어 있는 폴더를 Foldy에서 연다.</summary>
    private void RevealInFoldy(string path)
    {
        BringToFront();
        if (Path.GetDirectoryName(path.TrimEnd('\\')) is { } parent)
        {
            OpenInPane(Shell.ActivePane, parent, newTab: false);
        }
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private bool _hotkeyAsking;

    /// <summary>
    /// 빠른 검색 전역 단축키를 등록한다. 다른 프로그램·입력기와 겹치면 조용히 넘어가지 않고 알리고 다른 키를 고르게 한다
    /// (처음 설정이면 그 창에서, 아니면 작은 대화 창). 시험용 실행(FOLDY_DATA_DIR)에서는 등록하지 않는다.
    /// </summary>
    private async Task EnsureQuickSearchHotkeyAsync(GuideWindow? w)
    {
        const string id = "app.quickSearch";
        var hwnd = OwnerHandle;
        if (_hotkeyAsking || hwnd == IntPtr.Zero || App.Options.IsBench || App.Options.IsCapture || App.Options.DocShots is not null
            || Environment.GetEnvironmentVariable("FOLDY_DATA_DIR") is { Length: > 0 })
        {
            return;
        }

        GlobalHotkey.Unregister(hwnd, GlobalHotkey.QuickSearchId);
        var g = _keyMap.GetGesture(id);
        if (!_settings.QuickSearchGlobal || g is null)
        {
            return;
        }

        if (!GlobalHotkey.ConflictsWithInputMethod(g) && GlobalHotkey.Register(hwnd, GlobalHotkey.QuickSearchId, g))
        {
            w?.Say(Foldy.Core.Localization.Loc.F("sw.qs.tip", g));
            return;
        }

        _hotkeyAsking = true;
        var own = w is null;
        w ??= new GuideWindow(Foldy.Core.Localization.Loc.T("sw.qs.hotkeyTitle")) { Owner = this };
        try
        {
            if (own)
            {
                w.Show();
            }

            var ch = await HotkeyFlows.ChooseAsync(w, hwnd, g);
            if (ch.Gesture is null)
            {
                ShowStatus(Foldy.Core.Localization.Loc.F("sw.qs.hotkeyTaken", g));
                return;
            }

            if (!ch.Global)
            {
                _settings = _settings with { QuickSearchGlobal = false };
                SaveSettings();
                return;
            }

            if (ch.Gesture != g)
            {
                _keyMap.SetGesture(id, ch.Gesture);
                try
                {
                    _keyMap.Save();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "단축키 저장 실패");
                }

                _commands.Clear();
                BuildCommands();
            }

            GlobalHotkey.Register(hwnd, GlobalHotkey.QuickSearchId, ch.Gesture);
            if (own && !w.IsClosed)
            {
                await w.AskAsync(null, ("ok", Foldy.Core.Localization.Loc.T("sw.okGood")));
            }
        }
        finally
        {
            _hotkeyAsking = false;
            if (own && !w.IsClosed)
            {
                w.Close();
            }
        }
    }

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
        MoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
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

    private Foldy.Analysis.AnalysisCoordinator? _analysis;

    private void OnMenuAnalysis(object sender, RoutedEventArgs e) => _ = AskAnalysisTargetAsync();

    /// <summary>
    /// 폴더 분석할 곳을 한 번에 고른다 (사용자 요청 2026-11-20): 디스크마다 단추(남은 공간과 함께), 지금 폴더, 다른 폴더 고르기.
    /// 빠른 검색·찾기 메뉴·명령 목록 어디서 열어도 같다. 사이드바 드라이브의 오른쪽 클릭 "분석"은 그 드라이브를 바로 연다.
    /// </summary>
    private async Task AskAnalysisTargetAsync()
    {
        var w = new GuideWindow(Foldy.Core.Localization.Loc.T("cmd.analysis")) { Owner = this };
        w.Show();
        try
        {
            w.Say(Foldy.Core.Localization.Loc.T("sw.analysis.where"));
            var choices = new List<(string Id, string Label)>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                    {
                        var name = d.Name.TrimEnd('\\');
                        var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? name : $"{name} {d.VolumeLabel}";
                        choices.Add((d.RootDirectory.FullName, Foldy.Core.Localization.Loc.F("sw.analysis.drive", label, Foldy.Presentation.Files.FileItem.FormatBytes(d.AvailableFreeSpace))));
                    }
                }
                catch (IOException)
                {
                }
            }

            var here = Shell.ActivePane.ActiveFolder?.Path;
            if (!string.IsNullOrEmpty(here) && here.Length > 3 && here[1] == ':')
            {
                choices.Add((here, Foldy.Core.Localization.Loc.F("sw.analysis.here", Path.GetFileName(here.TrimEnd('\\')))));
            }

            choices.Add(("pick", Foldy.Core.Localization.Loc.T("sw.tidy.other")));
            choices.Add(("cancel", Foldy.Core.Localization.Loc.T("dlg.cancel")));
            var c = await w.AskAsync(null, [.. choices]);
            string? target = c;
            if (c == "pick")
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Foldy.Core.Localization.Loc.T("sw.analysis.pick"), InitialDirectory = here ?? "" };
                target = dlg.ShowDialog(w) == true ? dlg.FolderName : null;
            }

            if (target is null || target is "cancel" || target == GuideWindow.ClosedChoice)
            {
                return;
            }

            w.Close();
            OpenAnalysis(target);
        }
        finally
        {
            if (!w.IsClosed)
            {
                w.Close();
            }
        }
    }

    private void OpenAnalysis(string? target = null)
    {
        var pane = Shell.ActivePane;
        var path = target ?? pane.ActiveFolder?.Path;
        if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':')
        {
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.analysis.localOnly");
            return;
        }

        _analysis ??= new Foldy.Analysis.AnalysisCoordinator(new IndexerAnalysisService());
        var tab = new Foldy.Presentation.Analysis.AnalysisTabViewModel(_analysis, path);
        tab.RecycleRequested += async (_, e) =>
        {
            // 휴지통은 되살릴 수 있으니 다시 묻지 않는다 (고른 것을 이미 보고 눌렀다, 원칙 1.4). 끝나면 상태 줄로 알린다
            var ok = await Shell.RecycleAsync(e.Paths);
            if (ok)
            {
                Shell.Status = Foldy.Core.Localization.Loc.F("sw.recycled", e.Paths.Count, Foldy.Presentation.Files.FileItem.FormatBytes(e.TotalBytes));
            }
            foreach (var item in e.Items)
            {
                tab.MarkRecycled(item, ok);
            }
        };
        tab.ToolRequested += (_, tool) =>
        {
            if (tool == "logs")
            {
                OpenLogCleanup(Path.GetPathRoot(path));
            }
            else if (tool == "downloads")
            {
                OpenDownloadsCleanup(null);
            }
            else if (tool == "rebuildable")
            {
                OpenRebuildable(path);
            }
        };
        tab.OpenRequested += (_, e) =>
        {
            if (e.IsFolder)
            {
                pane.NewFolderTab(e.Path);
            }
            else if (Path.GetDirectoryName(e.Path) is { } dir)
            {
                pane.NewFolderTab(dir).FocusName = Path.GetFileName(e.Path);
            }
        };
        pane.AddTab(tab, activate: true, assignColor: true);
    }

    // ───────────── 정리 도구 (로그·다운로드·바탕화면) ─────────────

    private double _recentHeight;

    /// <summary>
    /// 사이드바는 한 화면에 다 보여야 한다: 작은 화면(예: 1366×768)에서 넘치면 "최근 폴더"를 먼저 접고,
    /// 다시 자리가 생기면 펼친다 (도구 버튼이 스크롤 밖으로 밀리지 않게).
    /// </summary>
    private void OnSidebarScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            return;
        }

        var over = SidebarScroll.ExtentHeight - SidebarScroll.ViewportHeight;
        if (over > 1 && RecentSection.Visibility == Visibility.Visible)
        {
            _recentHeight = RecentSection.ActualHeight;
            RecentSection.Visibility = Visibility.Collapsed;
            RecentSection.Tag = "auto";
        }
        else if (RecentSection.Visibility == Visibility.Collapsed && Equals(RecentSection.Tag, "auto") && -over >= _recentHeight + 4)
        {
            RecentSection.Visibility = Visibility.Visible;
            RecentSection.Tag = null;
        }
    }

    /// <summary>사이드바 묶음 메뉴 항목: Tag의 명령 id를 실행한다.</summary>
    private void OnGroupMenuItem(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        if (id == "side.recycleBin")
        {
            OnSideRecycleBin(sender, e);
            return;
        }

        RunCommand(id);
    }

    /// <summary>명령 목록의 기능 하나를 id로 실행한다 (사이드바 메뉴·한 줄 제안·빈 폴더 단추).</summary>
    public void RunCommand(string id) => _commands.FirstOrDefault(c => c.Id == id)?.Run();

    private void OnSideGroup(object sender, RoutedEventArgs e) => OnSideCleanupTools(sender, e);

    private void OnSideCleanupTools(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } b)
        {
            // 켜고 끄는 항목은 지금 상태를 체크로 보여 준다 (켠 곳에서 끈다)
            foreach (var mi in menu.Items.OfType<MenuItem>())
            {
                if (mi.Tag is "view.images")
                {
                    mi.IsChecked = ActiveView.ImagesOn;
                    mi.Header = ActiveView.ImagesOn ? Foldy.Core.Localization.Loc.T("sw.img.off") : Foldy.Core.Localization.Loc.T("sw.img.on");
                }
            }

            menu.PlacementTarget = b;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
            menu.IsOpen = true;
        }
    }

    private void OnSideLogCleanup(object sender, RoutedEventArgs e) => OpenLogCleanup(null);

    private void OnSideDownloadsCleanup(object sender, RoutedEventArgs e) => OpenDownloadsCleanup(null);

    private void OnSideDesktopTidy(object sender, RoutedEventArgs e) => OpenDesktopTidy();

    /// <summary>로그 정리 창. 드라이브를 주지 않으면 지금 폴더의 드라이브 (로컬 드라이브가 아니면 C:).</summary>
    private void OpenLogCleanup(string? drive)
    {
        var p = drive ?? Shell.ActivePane.ActiveFolder?.Path;
        var root = p is { Length: >= 2 } && p[1] == ':' ? Path.GetPathRoot(p) : null;
        root ??= Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        _ = CleanupFlows.LogsAsync(this, root, Shell);
    }

    private void OpenDownloadsCleanup(string? folder)
    {
        folder ??= CleanupPaths.Downloads();
        if (folder is null)
        {
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.noDownloads");
            return;
        }

        _ = CleanupFlows.DownloadsAsync(this, folder, Shell);
    }

    private void OpenDesktopTidy() => _ = CleanupFlows.DesktopAsync(this);

    /// <summary>지워도 되는 폴더 찾기: 사용자가 고른(지금 보는) 폴더 안에서만. 크기는 색인으로 바로, 없으면 직접 더한다.</summary>
    private void OpenRebuildable(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || Foldy.Presentation.Navigation.PathUtil.IsSftp(folder) || !Directory.Exists(folder))
        {
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.openFolderFirst");
            return;
        }

        var extra = _settings.RebuildableExtra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var client = _indexer;
        long? IndexSize(string dir)
        {
            // 색인이 있으면 부모 폴더의 크기 목록에서 바로 (폴더를 다시 읽지 않는다)
            if (client is not { IsFaulted: false } || Path.GetDirectoryName(dir) is not { } parent)
            {
                return null;
            }

            try
            {
                var r = client.FolderSizesAsync(parent).GetAwaiter().GetResult();
                return r.Status == Foldy.Indexer.Client.FolderSizesStatus.Ok
                    && r.Entries.FirstOrDefault(e => e.IsDir && string.Equals(e.Name, Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase)) is { } hit
                    ? (long)hit.Size : null;
            }
            catch (Exception ex) when (ex is IOException or Foldy.Indexer.Client.ProtocolException or OperationCanceledException or TimeoutException)
            {
                return null;
            }
        }

        _ = DevCleanupFlows.RebuildableAsync(this, folder, Shell, extra, IndexSize);
    }

    /// <summary>지금 폴더가 내 PC의 폴더면 그 경로 (고르기 창의 시작 위치).</summary>
    private string? LocalHere() => Shell.ActivePane.ActiveFolder?.Path is { Length: > 2 } p && p[1] == ':' ? p : null;

    public void ShowVersions(string file) => _ = SecureFlows.VersionsAsync(this, file, Shell);

    public void LockFile(string file) => _ = SecureFlows.LockFileAsync(this, file, Shell);

    public void PutInVault(string folder) => _ = SecureFlows.PutInVaultAsync(this, folder, Shell);

    public void ManageFolder(string folder) => _ = CleanupFlows.SetMethodAsync(this, folder);

    public void ShowManagedSuggestions(string root)
    {
        if (Foldy.Core.Managed.ManagedFolders.Resolve(root) is { } m)
        {
            _ = CleanupFlows.SuggestAsync(this, m.Root, m.Method);
        }
    }

    private DispatcherTimer? _cleanupTimer;
    private CleanupFinding? _cleanupFinding;
    private DispatcherTimer? _aiSuggestHide;

    /// <summary>
    /// AI 연결 제안 (2026-11-21 사용자 결정): 실행할 때 확인 창으로 묻지 않고, 이 PC에 AI 앱이 있는데 아직 연결 안 했을 때만 창 위 한 줄.
    /// [연결하기] [나중에] [다시 묻지 않기]. 그냥 두면 저절로 사라지고, [나중에]는 다음 실행에 다시, [다시 묻지 않기]는 설정에서 되돌린다.
    /// 시험 실행·문서 캡처에서는 띄우지 않는다.
    /// </summary>
    private void MaybeSuggestAi()
    {
        if (!_settings.AiSuggest || App.Options.IsBench || App.Options.IsCapture || App.Options.DocShots is not null
            || Environment.GetEnvironmentVariable("FOLDY_DATA_DIR") is { Length: > 0 } || CleanupNotice.Visibility == Visibility.Visible)
        {
            return;
        }

        var app = Foldy.Core.Mcp.McpClientConfig.SuggestTarget(
            Foldy.Core.Mcp.McpClientConfig.Known(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            Foldy.Core.Mcp.McpClientConfig.IsConnected);
        if (app is null)
        {
            return;
        }

        _hintKey = null;
        _cleanupFinding = new CleanupFinding(Foldy.Core.Mcp.FoldyMcpTools.SuggestLine(app, Random.Shared.Next()), "ai", "");
        CleanupNoticeText.Text = _cleanupFinding.Text;
        CleanupNoticeButton.Content = Foldy.Core.Localization.Loc.T("suggest.connect");
        CleanupNoticeClose.Content = Foldy.Core.Localization.Loc.T("suggest.later");
        CleanupNoticeNever.Visibility = Visibility.Visible;
        CleanupNotice.Visibility = Visibility.Visible;

        // 그냥 두면 저절로 사라진다 (누를 것을 강요하지 않는다)
        _aiSuggestHide?.Stop();
        _aiSuggestHide = new DispatcherTimer(TimeSpan.FromSeconds(45), DispatcherPriority.Background, (_, _) =>
        {
            _aiSuggestHide?.Stop();
            if (_cleanupFinding?.Tool == "ai" && !CleanupNotice.IsMouseOver)
            {
                CleanupNotice.Visibility = Visibility.Collapsed;
            }
        }, Dispatcher);
        _aiSuggestHide.Start();
    }

    private void OnNoticeNever(object sender, RoutedEventArgs e)
    {
        CleanupNotice.Visibility = Visibility.Collapsed;
        if (_cleanupFinding?.Tool == "ai")
        {
            _settings = _settings with { AiSuggest = false };
            SaveSettings();
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.ai.never");
        }
        else if (_cleanupFinding?.Tool is "desktop-start" or "downloads-start")
        {
            _settings = _settings with { DesktopSuggest = false };
            SaveSettings();
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.tidy.never");
        }
    }

    /// <summary>
    /// 바탕화면 정리 제안 (사용자 2026-11-21): 바로 가기를 뺀 파일이 20개가 넘으면 켤 때 창 위 한 줄.
    /// [정리하기] [나중에(7일 뒤 다시)] [다시 묻지 않기]. 확인 창이 아니고, 그냥 두면 저절로 사라진다. 띄웠으면 참.
    /// </summary>
    private bool MaybeSuggestDesktop()
    {
        if (App.Options.IsBench || App.Options.IsCapture || App.Options.DocShots is not null
            || Environment.GetEnvironmentVariable("FOLDY_DATA_DIR") is { Length: > 0 } || CleanupNotice.Visibility == Visibility.Visible)
        {
            return false;
        }

        // 바탕화면 먼저, 아니면 다운로드 (사용자 2026-11-21: "바탕화면과 다운로드 폴더는 20개 초과하면 안내") — 한 번에 하나만
        var now = DateTime.UtcNow;
        var desktop = CleanupPaths.Desktop;
        var n = Directory.Exists(desktop) ? Foldy.Core.Desktop.DesktopTidy.CountLoose(desktop) : 0;
        string tool, text, target;
        if (Foldy.Core.Desktop.DesktopTidy.ShouldSuggest(n, _settings.DesktopSuggest, _settings.DesktopSuggestAfterUtc, now))
        {
            (tool, target, text) = ("desktop-start", desktop, Foldy.Core.Localization.Loc.F("suggest.desktop", n));
        }
        else if (CleanupPaths.Downloads() is { } dl && Foldy.Core.Desktop.DesktopTidy.CountLoose(dl) is var m
                 && Foldy.Core.Desktop.DesktopTidy.ShouldSuggest(m, _settings.DesktopSuggest, _settings.DownloadsSuggestAfterUtc, now))
        {
            (tool, target, text) = ("downloads-start", dl, Foldy.Core.Localization.Loc.F("suggest.downloads", m));
        }
        else
        {
            return false;
        }

        _hintKey = null;
        _cleanupFinding = new CleanupFinding(text, tool, target);
        CleanupNoticeText.Text = _cleanupFinding.Text;
        CleanupNoticeButton.Content = Foldy.Core.Localization.Loc.T("suggest.tidy");
        CleanupNoticeClose.Content = Foldy.Core.Localization.Loc.T("suggest.later");
        CleanupNoticeNever.Visibility = Visibility.Visible;
        CleanupNotice.Visibility = Visibility.Visible;
        AutoHideNotice(tool);
        return true;
    }

    private void AutoHideNotice(string tool)
    {
        // 그냥 두면 저절로 사라진다 (누를 것을 강요하지 않는다)
        _aiSuggestHide?.Stop();
        _aiSuggestHide = new DispatcherTimer(TimeSpan.FromSeconds(45), DispatcherPriority.Background, (_, _) =>
        {
            _aiSuggestHide?.Stop();
            if (_cleanupFinding?.Tool == tool && !CleanupNotice.IsMouseOver)
            {
                CleanupNotice.Visibility = Visibility.Collapsed;
            }
        }, Dispatcher);
        _aiSuggestHide.Start();
    }

    /// <summary>백그라운드 정리 확인을 시작한다: 시작 3분 뒤 한 번, 그 뒤 6시간마다. 알림은 일주일에 한 번까지.</summary>
    private void StartCleanupChecks()
    {
        if (_cleanupTimer is not null || App.Options.DocShots is not null)
        {
            return;
        }

        _cleanupTimer = new DispatcherTimer(TimeSpan.FromMinutes(3), DispatcherPriority.ApplicationIdle, async (_, _) =>
        {
            _cleanupTimer!.Interval = TimeSpan.FromHours(6);
            if (!_settings.CleanupNotices || CleanupNotifier.ShownRecently() || CleanupNotice.Visibility == Visibility.Visible)
            {
                return;
            }

            try
            {
                var f = await CleanupNotifier.CheckAsync(CancellationToken.None);
                if (f is not null && _settings.CleanupNotices)
                {
                    ShowCleanupNotice(f);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Debug(ex, "정리 확인 실패");
            }
        }, Dispatcher);
        _cleanupTimer.Start();
    }

    private void ShowCleanupNotice(CleanupFinding f)
    {
        CleanupNoticeNever.Visibility = Visibility.Collapsed;
        CleanupNoticeClose.Content = Foldy.Core.Localization.Loc.T("common.close");
        _hintKey = null;
        _cleanupFinding = f;
        CleanupNoticeText.Text = f.Text;
        CleanupNoticeButton.Content = f.Tool switch { "logs" => Foldy.Core.Localization.Loc.T("sw.logs.title"), "downloads" => Foldy.Core.Localization.Loc.T("sw.c.downloads"), "managed" => Foldy.Core.Localization.Loc.T("sw.logs.view"), _ => Foldy.Core.Localization.Loc.T("sw.c.desktop") };
        CleanupNotice.Visibility = Visibility.Visible;
        CleanupNotifier.MarkShown();
    }

    private void OnCleanupNoticeOpen(object sender, RoutedEventArgs e)
    {
        CleanupNotice.Visibility = Visibility.Collapsed;
        switch (_cleanupFinding?.Tool)
        {
            case "ai":
                OpenMcp();
                break;
            case "desktop-start":
                OpenDesktopTidy();
                break;
            case "downloads-start":
                OpenDownloadsCleanup(_cleanupFinding.Target);
                break;
            case "logs":
                OpenLogCleanup(_cleanupFinding.Target);
                break;
            case "downloads":
                OpenDownloadsCleanup(_cleanupFinding.Target);
                break;
            case "managed":
                ShowManagedSuggestions(_cleanupFinding.Target);
                break;
            case "rebuildable":
                OpenRebuildable(_cleanupFinding.Target);
                break;
            case { } t when t.StartsWith("cmd:", StringComparison.Ordinal):
                RunCommand(t[4..]);
                break;
            default:
                OpenDesktopTidy();
                break;
        }
    }

    private void OnCleanupNoticeClose(object sender, RoutedEventArgs e)
    {
        CleanupNotice.Visibility = Visibility.Collapsed;
        if (_cleanupFinding?.Tool is "desktop-start" or "downloads-start")
        {
            // [나중에]: 7일 뒤에 다시
            _settings = _cleanupFinding.Tool == "desktop-start"
                ? _settings with { DesktopSuggestAfterUtc = DateTime.UtcNow.AddDays(7) }
                : _settings with { DownloadsSuggestAfterUtc = DateTime.UtcNow.AddDays(7) };
            SaveSettings();
            return;
        }

        if (_cleanupFinding is { } f && f.Text.Length > 0 && _hintKey is { } key)
        {
            ContextHints.Dismiss(key); // 닫은 제안은 그 폴더에서 다시 보이지 않는다
        }
    }

    private DispatcherTimer? _hintTimer;
    private string? _hintPath;
    private string? _hintKey;

    /// <summary>
    /// 맥락 제안: 지금 보는 폴더에 맞는 기능을 한 줄로 알려 준다 (한 번에 하나, 닫으면 그 폴더에서는 다시 안 보임).
    /// 예: 다운로드 폴더에 파일이 많으면 [다운로드 정리]. 정리 알림 설정을 함께 따른다.
    /// </summary>
    private void StartContextHints()
    {
        if (_hintTimer is not null || App.Options.DocShots is not null || App.Options.IsCapture)
        {
            return;
        }

        _hintTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.ApplicationIdle, (_, _) => CheckContextHint(), Dispatcher);
        _hintTimer.Start();
    }

    private void CheckContextHint()
    {
        var tab = Shell.ActivePane.ActiveFolder;
        var path = tab?.Path;
        if (tab is null || path is null || string.Equals(path, _hintPath, StringComparison.OrdinalIgnoreCase) || tab.State == Foldy.Presentation.Tabs.LoadState.Loading)
        {
            return;
        }

        _hintPath = path;
        if (!_settings.CleanupNotices || CleanupNotice.Visibility == Visibility.Visible || tab.IsSlowDrive)
        {
            return;
        }

        var other = (Shell.ActivePane == Shell.Left ? Shell.Right : Shell.Left).ActiveFolder?.Path;
        if (ContextHints.For(path, tab.Items, other, ActiveView.ImagesOn) is { } hint)
        {
            _hintKey = hint.Key;
            _cleanupFinding = hint.Finding;
            CleanupNoticeText.Text = hint.Finding.Text;
            CleanupNoticeButton.Content = hint.Button;
            CleanupNoticeClose.Content = Foldy.Core.Localization.Loc.T("common.close");
            CleanupNoticeNever.Visibility = Visibility.Collapsed;
            CleanupNotice.Visibility = Visibility.Visible;
            if (hint.Once)
            {
                // 기능 찾기 제안은 평생 한 번: 띄우는 순간 기억하고, 그냥 두면 저절로 사라진다 (누를 것을 강요하지 않는다)
                ContextHints.Dismiss(hint.Key);
                AutoHideNotice(hint.Finding.Tool);
            }
        }
    }

    private void OnTour(object sender, RoutedEventArgs e) => _ = FirstRunTour.RunAsync(this);

    private void OnFnPreview(object sender, RoutedEventArgs e) => ActiveView.TogglePreview();

    private void OnHelp(object sender, RoutedEventArgs e) => ShowHelp();

    /// <summary>? 단추: 짧은 소개(쇼츠)와 사용 설명서를 고르는 메뉴 (사용자 요청 2026-11-21).</summary>
    private void OnHelpButton(object sender, RoutedEventArgs e)
    {
        HelpMenu.PlacementTarget = HelpButton;
        HelpMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        HelpMenu.IsOpen = true;
    }

    private void OnShorts(object sender, RoutedEventArgs e) => _ = FirstRunTour.RunAsync(this);

    private void OnReportBug(object sender, RoutedEventArgs e) => App.OpenBugReport();

    private void OnToggleDiagnostic(object sender, RoutedEventArgs e) => ToggleDiagnostic();

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenLogFolder();

    private static void OpenLogFolder()
    {
        var dir = Path.Combine(App.DataDir, "logs");
        Directory.CreateDirectory(dir);
        using var _ = Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    /// <summary>
    /// 진단 모드 켜기/끄기. 켜면 상세 기록을 남기고(24시간 뒤 자동으로 꺼짐) 상태바에 "진단 중"을 보인다.
    /// 끄면 그동안의 기록을 오류 보고서로 보낼지 한 번 묻는다 (경로·사용자 이름은 가림).
    /// </summary>
    private void ToggleDiagnostic()
    {
        if (!Diagnostics.DiagnosticMode.IsOn)
        {
            _settings = _settings with { DiagnosticUntil = DateTime.UtcNow.AddHours(24) };
            SaveSettings();
            Diagnostics.DiagnosticMode.Apply(_settings.DiagnosticUntil);
            UpdateDiagnosticIndicator();
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.diag.on");
            return;
        }

        _settings = _settings with { DiagnosticUntil = null };
        SaveSettings();
        Log.Information("진단 모드 꺼짐");
        Diagnostics.DiagnosticMode.Apply(null);
        UpdateDiagnosticIndicator();
        if (Foldy.UI.Dialogs.FoldyDialog.Confirm(this, Foldy.Core.Localization.Loc.T("sw.diag.title"), Foldy.Core.Localization.Loc.T("sw.diag.ask"), Foldy.Core.Localization.Loc.T("sw.diag.send"), Foldy.Core.Localization.Loc.T("sw.diag.dontSend")))
        {
            Foldy.Reporting.CrashReporting.Record(new DiagnosticReport(Foldy.Core.Localization.Loc.T("sw.diag.issueTitle")), fatal: false);
            _ = SendReportsAsync();
        }
    }

    private async Task SendReportsAsync()
    {
        var results = await Task.Run(() => Foldy.Reporting.CrashReporting.SendPendingAsync());
        var sent = results.FirstOrDefault(r => r.Outcome == Foldy.Reporting.SendOutcome.Sent);
        Shell.Status = sent?.Response?.Url is { } url ? Foldy.Core.Localization.Loc.F("sw.diag.sent", url) : results.Count == 0 ? Foldy.Core.Localization.Loc.T("sw.diag.nothing") : Foldy.Core.Localization.Loc.F("sw.diag.failed", results[^1].Error);
    }

    private void UpdateDiagnosticIndicator()
    {
        DiagnosticBadge.Visibility = Diagnostics.DiagnosticMode.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>진단 보고서용 표시 예외 (실제 오류가 아니다).</summary>
    private sealed class DiagnosticReport(string message) : Exception(message);

    /// <summary>오류 보고서 자동 보내기 동의 (설정의 SendCrashReports).</summary>
    public bool AutoSendReports
    {
        get => _settings.SendCrashReports;
        set
        {
            _settings = _settings with { SendCrashReports = value };
            SaveSettings();
        }
    }

    /// <summary>설명서(help\*.md)를 버전을 채워 %LOCALAPPDATA%\Foldy\help에 펼치고 Markdown 탭으로 연다.</summary>
    private void ShowHelp()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "help");
        // 앱 언어의 설명서(index.<언어>.md)가 있으면 그것, 없으면 한국어판 (그림은 images\<언어>\, 문서 안 경로로 고른다)
        var name = File.Exists(Path.Combine(src, $"index.{Foldy.Core.Localization.Loc.Current}.md")) ? $"index.{Foldy.Core.Localization.Loc.Current}.md" : "index.md";
        var index = Path.Combine(src, name);
        if (!File.Exists(index))
        {
            Foldy.UI.Dialogs.FoldyDialog.Info(this, Foldy.Core.Localization.Loc.T("cmd.help"), Foldy.Core.Localization.Loc.T("sw.help.missing") + index, Foldy.UI.Dialogs.DialogKind.Warning);
            return;
        }

        var version = typeof(ShellWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        var dst = Path.Combine(App.DataDir, "help");
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

            index = Path.Combine(dst, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "도움말 복사 실패, 원본을 연다");
        }

        ShellViewModel.OpenMarkdown(Shell.ActivePane, index);
    }

    // ───────────── 공통 사이드바 ─────────────

    public static readonly DependencyProperty SidebarExpandedProperty = DependencyProperty.Register(
        nameof(SidebarExpanded), typeof(bool), typeof(ShellWindow),
        new PropertyMetadata(true, (d, _) => ((ShellWindow)d).ApplySidebar()));

    /// <summary>사이드바를 펼쳤는가. 접으면 아이콘만 보이는 48px 띠가 된다.</summary>
    public bool SidebarExpanded
    {
        get => (bool)GetValue(SidebarExpandedProperty);
        set => SetValue(SidebarExpandedProperty, value);
    }

    private double _sidebarWidth = 216;
    private readonly bool _hadSettings;

    /// <summary>업데이트하고 처음 켰을 때 한 줄로 알린다 (설정은 그대로, 묻지 않음).</summary>
    private void NoticeIfUpdated()
    {
        var stamp = Path.Combine(App.DataDir, "last-version.txt");
        try
        {
            var before = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : null;
            if (before != AppVersion)
            {
                File.WriteAllText(stamp, AppVersion);
                if (_hadSettings && before is not null)
                {
                    Shell.Status = Foldy.Core.Localization.Loc.F("sw.upd.done", AppVersion);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ApplySidebar()
    {
        SidebarColumn.Width = new GridLength(SidebarExpanded ? _sidebarWidth : 48);
        // 폴더 트리 버튼과 같은 규칙: 켜져 있으면 눌린 배경, 툴팁은 누르면 일어날 일
        if (SidebarExpanded)
        {
            SidebarToggleMark.SetResourceReference(Border.BackgroundProperty, "Brush.Selection");
        }
        else
        {
            SidebarToggleMark.ClearValue(Border.BackgroundProperty);
        }

        SidebarToggle.ToolTip = (SidebarExpanded ? Foldy.Core.Localization.Loc.T("sw.sidebar.collapse") : Foldy.Core.Localization.Loc.T("sw.sidebar.expand")) + Foldy.Core.Localization.Loc.T("sw.sidebar.tipTail");
        UpdatePinnedEmpty();
    }

    /// <summary>가장자리를 끌어 놓았을 때: 아주 좁으면 아이콘만(접기), 아니면 그 너비로 펼친다.</summary>
    private void OnSidebarDragged(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var w = SidebarColumn.ActualWidth;
        if (w < 120)
        {
            SidebarExpanded = false;
        }
        else
        {
            _sidebarWidth = Math.Clamp(w, 180, 360);
            SidebarExpanded = true;
        }

        ApplySidebar();
    }

    private void UpdatePinnedEmpty()
    {
        if (PinnedEmpty is not null)
        {
            PinnedEmpty.Visibility = SidebarExpanded && _quick?.Pinned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SaveQuickAccess()
    {
        _quickSave.Stop();
        try
        {
            JsonFileStore.Save(_quickPath, _quick.Snapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "자주 쓰는 폴더 저장 실패");
        }
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e) => SidebarExpanded = !SidebarExpanded;

    private void OnPinCurrent(object sender, RoutedEventArgs e) => TogglePinCurrent();

    private void TogglePinCurrent()
    {
        var path = Shell.ActivePane.ActiveFolder?.Path;
        if (string.IsNullOrEmpty(path))
        {
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.pin.none");
            return;
        }

        var name = Foldy.Presentation.Navigation.PathUtil.DisplayName(path);
        if (_quick.IsPinned(path))
        {
            _quick.Unpin(path);
            Shell.Status = Foldy.Core.Localization.Loc.F("sw.pin.removed", name);
        }
        else if (_quick.Pin(path))
        {
            Shell.Status = Foldy.Core.Localization.Loc.F("sw.pin.added", name);
        }
        else
        {
            Shell.Status = Foldy.Core.Localization.Loc.F("sw.pin.max", QuickAccess.MaxPinned);
        }
    }

    private static T? TagOf<T>(object sender) where T : class => (sender as FrameworkElement)?.Tag as T;

    /// <summary>사이드바에서 고른 폴더를 활성 패널에서 연다 (폴더 탭이 아니면 새 탭).</summary>
    private void OpenInPane(PaneViewModel pane, string path, bool newTab)
    {
        if (!newTab && pane.ActiveFolder is { } tab)
        {
            _ = tab.NavigateAsync(path);
        }
        else
        {
            pane.NewFolderTab(path);
        }

        (pane == Shell.Left ? LeftPane : RightPane).FocusList();
    }

    private PaneViewModel OtherPane => Shell.ActivePane == Shell.Left ? Shell.Right : Shell.Left;

    private void OnSideFolder(object sender, RoutedEventArgs e)
    {
        if (TagOf<QuickFolder>(sender) is { } f)
        {
            OpenInPane(Shell.ActivePane, f.Path, newTab: false);
        }
    }

    private void OnSideFolderNewTab(object sender, RoutedEventArgs e)
    {
        if (TagOf<QuickFolder>(sender) is { } f)
        {
            OpenInPane(Shell.ActivePane, f.Path, newTab: true);
        }
    }

    private void OnSideFolderOther(object sender, RoutedEventArgs e)
    {
        if (TagOf<QuickFolder>(sender) is { } f)
        {
            OpenInPane(OtherPane, f.Path, newTab: false);
        }
    }

    private void OnPinFolder(object sender, RoutedEventArgs e)
    {
        if (TagOf<QuickFolder>(sender) is { } f && _quick.Pin(f.Path))
        {
            Shell.Status = Foldy.Core.Localization.Loc.F("sw.pin.added", f.Name);
        }
    }

    private void OnUnpin(object sender, RoutedEventArgs e)
    {
        if (TagOf<QuickFolder>(sender) is { } f && _quick.Unpin(f.Path))
        {
            Shell.Status = Foldy.Core.Localization.Loc.F("sw.pin.removed", f.Name);
        }
    }

    private void OnPinUp(object sender, RoutedEventArgs e) => _quick.Move(TagOf<QuickFolder>(sender)?.Path, -1);

    private void OnPinDown(object sender, RoutedEventArgs e) => _quick.Move(TagOf<QuickFolder>(sender)?.Path, 1);

    private void OnClearRecent(object sender, RoutedEventArgs e) => _quick.ClearRecent();

    private void OnSideDrive(object sender, RoutedEventArgs e)
    {
        if (TagOf<DriveItem>(sender) is { } d)
        {
            OpenInPane(Shell.ActivePane, d.Root, newTab: false);
        }
    }

    private void OnSideDriveNewTab(object sender, RoutedEventArgs e)
    {
        if (TagOf<DriveItem>(sender) is { } d)
        {
            OpenInPane(Shell.ActivePane, d.Root, newTab: true);
        }
    }

    private void OnSideDriveOther(object sender, RoutedEventArgs e)
    {
        if (TagOf<DriveItem>(sender) is { } d)
        {
            OpenInPane(OtherPane, d.Root, newTab: false);
        }
    }

    private void OnSideDriveAnalyze(object sender, RoutedEventArgs e)
    {
        if (TagOf<DriveItem>(sender) is { } d && Shell.ActivePane.ActiveFolder is { } tab)
        {
            _ = tab.NavigateAsync(d.Root);
        }

        OpenAnalysis();
    }

    private void OnSideSearch(object sender, RoutedEventArgs e) => ActiveView.FocusSearch();

    private void OnSideTerminal(object sender, RoutedEventArgs e) => Shell.OpenTerminal(TerminalKind.Default, false);

    /// <summary>사이드바의 휴지통 용량 표시. 시작할 때, 삭제 작업이 끝날 때, 1분마다 갱신한다.</summary>
    private async void UpdateRecycleBinInfo()
    {
        var info = await Task.Run(Foldy.Platform.Shell.RecycleBin.Query);
        if (info is { } i && i.Items > 0)
        {
            RecycleBinText.Text = Foldy.Core.Localization.Loc.F("sw.bin.size", Foldy.Presentation.Files.FileItem.FormatBytes(i.Bytes));
            EmptyRecycleBinButton.Visibility = Visibility.Visible;
            EmptyRecycleBinButton.ToolTip = Foldy.Core.Localization.Loc.F("sw.bin.emptyTip", i.Items, Foldy.Presentation.Files.FileItem.FormatBytes(i.Bytes));
        }
        else
        {
            RecycleBinText.Text = Foldy.Core.Localization.Loc.T("sw.bin");
            EmptyRecycleBinButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnEmptyRecycleBin(object sender, RoutedEventArgs e)
    {
        var info = Foldy.Platform.Shell.RecycleBin.Query();
        if (info is not { Items: > 0 } i)
        {
            UpdateRecycleBinInfo();
            return;
        }

        if (!Foldy.UI.Dialogs.FoldyDialog.Confirm(this, Foldy.Core.Localization.Loc.T("sw.bin.empty"), Foldy.Core.Localization.Loc.F("sw.bin.emptyAsk", i.Items, Foldy.Presentation.Files.FileItem.FormatBytes(i.Bytes)), Foldy.Core.Localization.Loc.T("sw.bin.emptyYes"), danger: true))
        {
            return;
        }

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Shell.Status = Foldy.Core.Localization.Loc.T("sw.bin.emptying");
        var ok = await Task.Run(() => Foldy.Platform.Shell.RecycleBin.Empty(hwnd));
        Shell.Status = ok ? Foldy.Core.Localization.Loc.F("sw.bin.emptied", Foldy.Presentation.Files.FileItem.FormatBytes(i.Bytes)) : Foldy.Core.Localization.Loc.T("sw.bin.emptyFailed");
        UpdateRecycleBinInfo();
    }

    private void OnSideRecycleBin(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning(ex, "휴지통 열기 실패");
            Shell.Status = Foldy.Core.Localization.Loc.T("sw.bin.openFailed");
        }
    }

    // ───────────── 단일 인스턴스 ─────────────

    /// <summary>다른 Foldy가 실행됐다: 이 창을 앞으로 가져오고, 경로 인자가 있으면 활성 패널에 새 탭으로 연다.</summary>
    public void ActivateFromOtherInstance(string[] args)
    {
        var path = AppOptions.Parse(args).OpenPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && SecureFlows.TryOpen(this, path, Shell))
        {
            path = null; // 탐색기에서 연 잠긴 파일·금고
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            Shell.ActivePane.NewFolderTab(path);
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        // 다른 앱이 앞에 있으면 Activate만으로는 작업 표시줄만 깜빡일 수 있다: 잠깐 맨 위로 올렸다 내린다.
        Topmost = true;
        Topmost = false;
        Focus();
        ActiveView.FocusList();
    }
}
