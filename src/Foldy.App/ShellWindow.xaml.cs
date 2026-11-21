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
                        BeforeOverwrite = dest =>
                        {
                            if (_settings.KeepPreviousVersions)
                            {
                                Foldy.Core.History.VersionHistory.Preserve(dest);
                            }
                        },
                    }),
                _sftp.Provider,
                new SftpTransferOptions { ResolveConflict = ConflictWindow.AskTransferAsync }),
            new ShellOpenFileService(),
            new TerminalService(() => _settings, (p, sudo) => _sftp.OpenTerminal(p, sudo))))
        {
            Settings = _settings,
            ConfirmAsync = msg => Task.FromResult(Foldy.UI.Dialogs.FoldyDialog.Confirm(this, "확인", msg, "지울게요", danger: true)), // SFTP 삭제처럼 되살릴 수 없는 일
            OpenSpecial = p => SecureFlows.TryOpen(this, p, Shell!),
            AskBigDeleteAsync = q => DeleteFlows.AskBigDeleteAsync(this, q),
        };

        _updater = new LegacyUpdateSource(new AppUpdater(AppVersion));
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
            Shell.Status = "다른 Git 작업이 진행 중입니다";
            return;
        }

        _gitBarBusy = true;
        var label = pull ? "받기" : "올리기";
        Shell.Status = label + " 중…";
        try
        {
            Foldy.Git.GitResult r;
            var done = label + " 완료";
            if (pull)
            {
                r = await _git.PullAsync(repoRoot, Foldy.Git.PullMode.Merge);
            }
            else
            {
                var st = await _git.GetStatusAsync(repoRoot);
                var ahead = st.Value?.Ahead ?? 0;
                r = await _git.PushAsync(repoRoot, setUpstream: st.Value is { HasUpstream: false }, forceWithLease: false);
                if (ahead > 0) done += $": {ahead}개 커밋";
            }

            Shell.Status = r.Success ? done : label + " 실패: " + Foldy.Presentation.Git.GitErrorText.Describe(r).Split('\n')[0];
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
            Shell.Status = "Git 저장소 안의 폴더에서 파일을 고른 뒤 실행하세요";
            return;
        }

        var file = folder.Selection.FirstOrDefault(i => !i.IsFolder)
            ?? folder.Items.OfType<Foldy.Presentation.Files.FileItem>().FirstOrDefault(i => !i.IsFolder && i.Name == folder.FocusName);
        if (file is null)
        {
            Shell.Status = "Blame 할 파일을 먼저 고르세요";
            return;
        }

        var root = g.RepoRoot.TrimEnd('\\', '/');
        if (!file.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            Shell.Status = "이 파일은 저장소 밖에 있습니다";
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

        if (!App.Options.IsBench && !App.Options.IsCapture)
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
        Shell.Status = $"Foldy {legacy.ReadyVersion} 업데이트가 준비됐습니다 (회사 프록시 경로, 설치할 때 관리자 확인이 한 번 필요합니다)";
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
        CopyEngineText.Text = available ? "복사 엔진: Rust" : "복사 엔진: 기본";
        CopyEngineText.ToolTip = available ? "foldy_copy.dll 사용 가능 (M3에서 병렬 복사로 전환)" : reason;
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
            IndexerText.Text = "검색: 폴더 훑기 (색인 서비스 없음)";
            IndexerText.ToolTip = $"색인 서비스에 연결하지 못했습니다 ({svc}). 30초마다 다시 연결합니다. 서비스가 없으면 ⋯ → 색인 서비스 설치로 설치할 수 있습니다.";
            await _search.RefreshStateAsync();
            return;
        }

        _indexerRetry?.Stop();
        Log.Information("색인 서비스 연결됨");
        StartCleanupChecks();
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
            Foldy.Indexer.Client.IndexState.Ready => $"검색: 색인 완료 · {st.IndexedEntries:N0}개",
            Foldy.Indexer.Client.IndexState.Indexing => $"검색: 색인 만드는 중 {st.ProgressPermille / 10}%",
            _ => "검색: 색인 준비 중",
        };
        var lines = st.Volumes.Select(v => v.State switch
        {
            Foldy.Indexer.Client.IndexState.Ready => $"{v.Volume} 완료 · {v.Entries:N0}개",
            Foldy.Indexer.Client.IndexState.Indexing => $"{v.Volume} 만드는 중 {v.ProgressPermille / 10}%",
            _ => $"{v.Volume} 준비 중",
        }).ToList();
        var indexed = st.Volumes.Select(v => v.Volume.TrimEnd('\\').ToUpperInvariant()).ToHashSet();
        foreach (var d in System.IO.DriveInfo.GetDrives())
        {
            var name = d.Name.TrimEnd('\\').ToUpperInvariant();
            if (!indexed.Contains(name))
            {
                lines.Add(_crawler?.StatusOf(d.Name) is { } cs
                    ? $"{name} {(cs.Running ? "천천히 모으는 중" : cs.Complete ? "모으기 완료" : "쉬는 중")} · {Math.Max(0, cs.Found):N0}개 ({(d.DriveType == System.IO.DriveType.Network ? "네트워크 드라이브" : "NTFS가 아니거나 가상 드라이브")})"
                    : $"{name} 색인 없음 ({(d.DriveType == System.IO.DriveType.Network ? "네트워크 드라이브" : "NTFS가 아니거나 가상 드라이브")}) · 그 폴더에 있을 때 직접 훑어요");
            }
        }

        IndexerText.ToolTip = "파일 이름 검색(Ctrl+F·Ctrl+Space)에 쓰는 색인\n" + string.Join("\n", lines);

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
                Foldy.Indexer.Client.IndexState.Ready => "색인 완료",
                Foldy.Indexer.Client.IndexState.Indexing => $"색인 중 {v.ProgressPermille / 10}%",
                _ => "색인 준비 중",
            };
        }

        if (_crawler?.StatusOf(d.Root) is { } c)
        {
            return c.Running
                ? $"색인 중 · {Math.Max(0, c.Found):N0}개 (천천히)"
                : c.Complete ? $"색인 완료 · {Math.Max(0, c.Found):N0}개" : "색인 쉬는 중";
        }

        if (_crawler is null || d.Kind == DriveKind.CdRom)
        {
            return "색인 없음 · 폴더 안에서만";
        }

        // 한 번에 한 드라이브씩 모은다: 무엇을 기다리는지 말한다
        return _crawler.Current() is { } now ? $"색인 차례 기다리는 중 · {now.Drive.TrimEnd('\\')} 다음" : "색인 곧 시작";
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

    private Foldy.Presentation.Commands.KeyMap _keyMap = Foldy.Presentation.Commands.KeyMap.Load();

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
        Add("git.open", "Git 뷰 열기", () =>
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
                Shell.Status = "Git 저장소가 아닙니다";
            }
        });
        Add("git.blame", "Git: 이 파일 Blame", GitBlameFocusedFile);
        Add("file.rename", "이름 바꾸기", () => ActiveView.BeginRename(), gesture: "F2");
        Add("file.delete", "휴지통으로 삭제", () => _ = Shell.DeleteSelectionAsync(false), gesture: "Del");
        Add("file.deletePermanent", "영구 삭제", () => _ = Shell.DeleteSelectionAsync(true), gesture: "Shift+Del");
        Add("sidebar.focus", "사이드바로 포커스 이동 (Tab으로 둘러보고 Esc로 목록 복귀)", FocusSidebar, Key.E, ModifierKeys.Control | ModifierKeys.Shift);
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
        Add("analysis.open", "폴더 분석 (디스크·폴더 고르기)", () => _ = AskAnalysisTargetAsync());
        Add("ai.connect", "AI 연결 (Claude 등에서 Foldy로 파일 찾기)", OpenMcp);
        Add("compare.panes", "두 창 비교·맞추기", () =>
        {
            if (Shell.Left.ActiveFolder?.Path is { Length: > 2 } l && Shell.Right.ActiveFolder?.Path is { Length: > 2 } r && !Foldy.Presentation.Navigation.PathUtil.IsSftp(l) && !Foldy.Presentation.Navigation.PathUtil.IsSftp(r))
            {
                _ = CompareFlow.RunAsync(this, l, r, Shell);
            }
            else
            {
                Shell.Status = "양쪽 창에 비교할 폴더를 열어 주세요 (내 PC·네트워크 폴더)";
            }
        });
        Add("cleanup.logs", "로그 정리 (현재 드라이브)", () => OpenLogCleanup(null));
        Add("cleanup.downloads", "다운로드 정리", () => OpenDownloadsCleanup(null));
        Add("cleanup.desktop", "바탕화면 정리", OpenDesktopTidy);
        Add("memo.new", "새 메모 (.md, 지금 폴더에)", () =>
        {
            if (Shell.ActivePane.ActiveFolder?.Path is { Length: > 2 } p && p[1] == ':')
            {
                var path = Foldy.Core.Vault.SecureZip.Unique(Path.Combine(p, "새 메모.md"));
                File.WriteAllText(path, "# 새 메모\n\n");
                MemoWindow.OpenMarkdown(path).Show();
                _ = Shell.RefreshFoldersAsync([p]);
            }
        });
        Add("secure.newMemo", "새 잠긴 메모 (지금 폴더에)", () =>
        {
            if (Shell.ActivePane.ActiveFolder?.Path is { Length: > 2 } p && p[1] == ':')
            {
                _ = SecureFlows.NewMemoAsync(this, p);
            }
        });
        Add("secure.lock", "암호 걸기 (고른 파일)", () =>
        {
            var sel = ShellViewModel.SelectedPaths(Shell.ActivePane);
            if (sel.Count == 1 && File.Exists(sel[0]) && !SecureFlows.IsLocked(sel[0]))
            {
                _ = SecureFlows.LockFileAsync(this, sel[0], Shell);
            }
            else
            {
                Shell.Status = "암호를 걸 파일 하나를 골라 주세요";
            }
        });
        Add("secure.vault", "금고에 넣기 (고른 폴더)", () =>
        {
            var sel = ShellViewModel.SelectedPaths(Shell.ActivePane);
            if (sel.Count == 1 && Directory.Exists(sel[0]))
            {
                _ = SecureFlows.PutInVaultAsync(this, sel[0], Shell);
            }
            else
            {
                Shell.Status = "금고에 넣을 폴더 하나를 골라 주세요";
            }
        });
        Add("tag.done", "작업 완료 표시 켜기/끄기 (고른 파일)", () => ActiveView.ToggleTag(Foldy.Core.Tags.FileTags.Default), gesture: "Ctrl+D");
        foreach (var t in Foldy.Core.Tags.FileTags.Builtin.Skip(1))
        {
            Add("tag." + t.Name, $"'{t.Name}' 표시 켜기/끄기 (고른 파일)", () => ActiveView.ToggleTag(t.Name));
        }

        Add("folder.manage", "이 폴더 관리 방식 정하기…", () =>
        {
            if (Shell.ActivePane.ActiveFolder?.Path is { Length: > 2 } p && p[1] == ':')
            {
                ManageFolder(p);
            }
            else
            {
                Shell.Status = "관리 방식은 내 PC의 폴더에서 정할 수 있어요";
            }
        });
        Add("view.preview", "미리보기 창 보이기/숨기기", () => ActiveView.TogglePreview(), Key.F3);
        Add("view.zoomIn", "화면 크게 (글씨·버튼)", () => StepUiScale(1), Key.OemPlus, ModifierKeys.Control, "Ctrl+=");
        Add("view.zoomOut", "화면 작게", () => StepUiScale(-1), Key.OemMinus, ModifierKeys.Control, "Ctrl+-");
        Add("view.zoomReset", "화면 크기: Windows 설정 따르기", () => SetUiScale(0), Key.D0, ModifierKeys.Control, "Ctrl+0");
        Add("view.tree", "폴더 트리 보이기/숨기기", () => ActiveView.ToggleTree(), Key.B, ModifierKeys.Control);
        Add("view.sidebar", "사이드바 접기/펼치기", () => SidebarExpanded = !SidebarExpanded, Key.B, ModifierKeys.Control | ModifierKeys.Shift);
        Add("sidebar.pin", "지금 폴더를 자주 쓰는 폴더에 고정/해제", TogglePinCurrent);
        Add("view.themeSystem", "테마: 시스템 설정 따르기", () => SetTheme(FoldyTheme.System));
        Add("view.themeLight", "테마: 라이트", () => SetTheme(FoldyTheme.Light));
        Add("view.themeDark", "테마: 다크", () => SetTheme(FoldyTheme.Dark));
        Add("view.themeRetro", "테마: 레트로", () => SetTheme(FoldyTheme.Retro));
        Add("sftp.connect", "SFTP 서버에 연결…", ConnectSftp, Key.O, ModifierKeys.Control | ModifierKeys.Shift);
        Add("indexer.install", "색인 서비스 설치 (관리자, 전체 드라이브 즉시 검색)", () => RunIndexerSetup(install: true));
        Add("indexer.uninstall", "색인 서비스 제거 (관리자)", () => RunIndexerSetup(install: false));
        Add("app.palette", "모든 명령 보기", ShowPalette, Key.P, ModifierKeys.Control | ModifierKeys.Shift, inText: true);
        Add("app.quickSearch", "빠른 검색 (기능·파일)", ShowQuickSearch, Key.Space, ModifierKeys.Control, inText: true);
        Add("app.help", "도움말 (사용 설명서)", ShowHelp, Key.F1, inText: true);
        Add("app.tour", "둘러보기 (처음 안내 다시 보기)", () => _ = FirstRunTour.RunAsync(this));
        Add("app.settings", "설정…", ShowSettings, Key.OemComma, ModifierKeys.Control, inText: true);
        Add("app.reportBug", "버그 신고…", App.OpenBugReport);
        Add("app.emptyRecycleBin", "휴지통 비우기…", () => OnEmptyRecycleBin(this, new RoutedEventArgs()));
        Add("app.diagnostic", "버그 진단 모드 켜기/끄기", ToggleDiagnostic);
        Add("app.logs", "로그 폴더 열기", OpenLogFolder);
        Add("app.about", "Foldy 정보", ShowAbout);
        Add("app.checkUpdate", "업데이트 확인", CheckUpdateNow);
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
        // 받아 둔 업데이트가 있어도 서비스에는 한 번 더 확인한다: 그사이 더 새 버전이 나왔으면 그것으로 바뀐다
        // (2026-11-21 사용자: "업데이트 정보를 받은 상태에서는 새 버전이 있어도 새 버전으로 업데이트하지 않는다")
        if (_updater.ReadyVersion is null || _updater is ServiceUpdater)
        {
            Shell.Status = "업데이트 확인 중…";
            bool found;
            try
            {
                found = await _updater.CheckAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warning(ex, "업데이트 확인 실패");
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
                    err = $"{err}\n사용자 프록시 경로: {ue}";
                }
                Shell.Status = err is null ? $"최신 버전이에요 ({AppVersion}). 새 버전이 나오면 알려 드릴게요" : $"업데이트를 확인하지 못했습니다: {err}";
                if (interactive && err is not null) // 최신이면 상태 줄로만 (누를 단추를 늘리지 않는다, 원칙 1.4)
                {
                    Foldy.UI.Dialogs.FoldyDialog.Info(this, "Foldy 업데이트",
                        err.Contains(AppUpdater.MissingVerifier, StringComparison.Ordinal) ? $"업데이트를 확인하지 못했어요.\n\n{err}"
                            : $"업데이트를 확인하지 못했어요.\n\n{err}\n\n인터넷·프록시 연결을 확인한 뒤 다시 해 주세요.",
                        Foldy.UI.Dialogs.DialogKind.Warning);
                }

                return;
            }
        }

        UpdateUpdateButton();
        var v = _updater.ReadyVersion;
        Shell.Status = $"Foldy {v} 업데이트가 준비됐습니다";
        if (interactive && Foldy.UI.Dialogs.FoldyDialog.Confirm(this, "Foldy 업데이트", $"Foldy {v}이(가) 준비됐어요 (지금 {AppVersion}).\n지금 Foldy를 닫고 설치할까요? 1분쯤 뒤에 다시 열려요.", "지금 설치", "나중에"))
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
                ? $"Foldy {v} · 재시작하고 업데이트"
                : $"작업이 끝나면 {v}(으)로 업데이트 · 취소";
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
            Shell.Status = "업데이트 예약을 취소했습니다";
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
            Shell.Status = "진행 중인 작업이 끝나면 업데이트합니다";
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
                ? $"업데이트 설치를 시작하지 못했습니다: {_updater.LastError}"
                : "업데이트 설치가 취소됐습니다. 관리자 권한 창에서 [예]를 눌러야 설치됩니다.";
            Shell.Status = why;
            UpdateUpdateButton();
            Foldy.UI.Dialogs.FoldyDialog.Info(this, "Foldy 업데이트", why, Foldy.UI.Dialogs.DialogKind.Warning);
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

        if (install && !Foldy.Platform.SystemInfo.IndexerService.IsProtectedLocation(exe))
        {
            // SYSTEM 서비스 exe가 사용자 폴더에 있으면 바꿔치기로 권한을 빼앗길 수 있다
            Shell.Status = "색인 서비스는 설치본(Program Files)에서만 설치할 수 있어요. 압축본에서는 색인 없이 검색해요.";
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
        Shell.Status = scale <= 0 ? $"화면 크기: Windows 설정 따르기 ({EffectiveUiScale * 100:0}%)" : $"화면 크기 {scale * 100:0}%";
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
                    ? $"파일은 색인된 드라이브 전체와, 색인이 없는 지금 폴더({Path.GetFileName(scope.TrimEnd('\\'))}) 안에서 찾아요."
                    : "파일은 색인된 드라이브 전체에서 찾아요.")
                : indexing >= 0
                    ? $"색인을 만드는 중({indexing}%)이라 지금은 이 폴더 안에서만 찾아요. 끝나면 모든 드라이브에서 찾아요."
                : $"⚠ 색인 서비스에 연결되지 않아 파일은 지금 폴더({Path.GetFileName(scope?.TrimEnd('\\') ?? "")}) 안에서만 찾아요. 아래 상태 줄의 \"검색\"을 확인해 주세요.")
            + " Enter 열기 · Ctrl+Enter 들어 있는 폴더 · Esc 닫기";
        _quickSearch = BuildQuickSearch(scope, footer);
        _quickSearch.Closed += (_, _) => _quickSearch = null;
        _quickSearch.Show();
    }

    private QuickSearchWindow BuildQuickSearch(string? scope, string footer) => new(
        QuickSearchInstant,
        (q, ct) => _search.SearchAsync(Foldy.Core.QuickSearch.QuickIntent.Normalize(q), scope, ct),
        f => new QuickSearchWindow.Hit(
            "파일",
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
            yield return new QuickSearchWindow.Hit(ans.Kind, "\uE8EF", ans.Display, "Enter로 결과 복사", () =>
            {
                try
                {
                    Clipboard.SetText(ans.Copy);
                    ShowStatus($"{ans.Copy} 복사했어요");
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
                yield return new QuickSearchWindow.Hit("폴더", "\uE8B7", f.Name, path,
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
                yield return new QuickSearchWindow.Hit("앱", "\uE7AC", a.Name, "앱 실행", () => StartShell(lnk), Score: s + 10);
            }
        }

        foreach (var p in Foldy.Core.QuickSearch.SystemCatalog.SettingsPages)
        {
            var s = Math.Max(m.Score(p.Title), m.Score(p.Keywords) / 2);
            if (s > 0)
            {
                var uri = p.Uri;
                yield return new QuickSearchWindow.Hit("설정", "\uE713", p.Title, "Windows 설정", () => StartShell(uri), Score: s);
            }
        }

        var sentence = Foldy.Core.QuickSearch.QuickIntent.IsSentence(q);
        foreach (var c in QuickSearchCommands(q))
        {
            var hit = CommandHit(c, Foldy.Core.QuickSearch.QuickIntent.Score(c.Id, c.Title, q) + 2);
            // 말하듯 물으면 대답하듯: "이걸 할까요?" (Enter면 그 기능의 대화 창이 이어서 한 단계씩 물어본다)
            yield return sentence ? hit with { Detail = "이걸 할까요? Enter를 누르면 시작해요" } : hit;
        }
    }

    private sealed class QuickMatcher(string q)
    {
        public int Score(string title) => Foldy.Core.QuickSearch.QuickSearchMatch.Score(title, q);
    }

    private QuickSearchWindow.Hit CommandHit(PaletteCommand c, int score) =>
        new("기능", "\uE945", c.Title, c.Gesture.Length > 0 ? $"Foldy 기능 · {c.Gesture}" : "Foldy 기능", () => { BringToFront(); c.Run(); }, Score: score);

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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            BringToFront();
            ShowStatus($"열 수 없어요: {ex.Message}");
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
            w?.Say($"하나 더! 어느 프로그램을 쓰다가도 {g}를 누르면 빠른 검색이 열려요. 파일·앱·설정·Foldy 기능을 한 번에 찾아요.");
            return;
        }

        _hotkeyAsking = true;
        var own = w is null;
        w ??= new GuideWindow("빠른 검색 단축키") { Owner = this };
        try
        {
            if (own)
            {
                w.Show();
            }

            var ch = await HotkeyFlows.ChooseAsync(w, hwnd, g);
            if (ch.Gesture is null)
            {
                ShowStatus($"빠른 검색 단축키({g})가 다른 프로그램과 겹쳐서 Foldy 창 안에서만 써요. 설정 → 단축키에서 바꿀 수 있어요.");
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
                await w.AskAsync(null, ("ok", "좋아요"));
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
        var w = new GuideWindow("폴더 분석") { Owner = this };
        w.Show();
        try
        {
            w.Say("어디를 분석할까요? 무엇이 공간을 차지하는지 큰 상자 그림으로 보여 드려요.");
            var choices = new List<(string Id, string Label)>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                    {
                        var name = d.Name.TrimEnd('\\');
                        var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? name : $"{name} {d.VolumeLabel}";
                        choices.Add((d.RootDirectory.FullName, $"{label} ({Foldy.Presentation.Files.FileItem.FormatBytes(d.AvailableFreeSpace)} 남음)"));
                    }
                }
                catch (IOException)
                {
                }
            }

            var here = Shell.ActivePane.ActiveFolder?.Path;
            if (!string.IsNullOrEmpty(here) && here.Length > 3 && here[1] == ':')
            {
                choices.Add((here, $"지금 폴더 ({Path.GetFileName(here.TrimEnd('\\'))})"));
            }

            choices.Add(("pick", "다른 폴더 고르기…"));
            choices.Add(("cancel", "그만두기"));
            var c = await w.AskAsync(null, [.. choices]);
            string? target = c;
            if (c == "pick")
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "분석할 폴더를 고르세요", InitialDirectory = here ?? "" };
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
            Shell.Status = "폴더 분석은 로컬 드라이브에서만 할 수 있습니다";
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
                Shell.Status = $"{e.Paths.Count:N0}개 ({Foldy.Presentation.Files.FileItem.FormatBytes(e.TotalBytes)})를 휴지통으로 옮겼어요. 휴지통에서 되살릴 수 있어요";
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

        _commands.FirstOrDefault(c => c.Id == id)?.Run();
    }

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
                    mi.Header = ActiveView.ImagesOn ? "이미지만 보기 끄기" : "이미지만 보기";
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
            Shell.Status = "다운로드 폴더를 찾지 못했습니다";
            return;
        }

        _ = CleanupFlows.DownloadsAsync(this, folder, Shell);
    }

    private void OpenDesktopTidy() => _ = CleanupFlows.DesktopAsync(this);

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
        _hintKey = null;
        _cleanupFinding = f;
        CleanupNoticeText.Text = f.Text;
        CleanupNoticeButton.Content = f.Tool switch { "logs" => "로그 정리", "downloads" => "다운로드 정리", "managed" => "정리할 것 보기", _ => "바탕화면 정리" };
        CleanupNotice.Visibility = Visibility.Visible;
        CleanupNotifier.MarkShown();
    }

    private void OnCleanupNoticeOpen(object sender, RoutedEventArgs e)
    {
        CleanupNotice.Visibility = Visibility.Collapsed;
        switch (_cleanupFinding?.Tool)
        {
            case "logs":
                OpenLogCleanup(_cleanupFinding.Target);
                break;
            case "downloads":
                OpenDownloadsCleanup(_cleanupFinding.Target);
                break;
            case "managed":
                ShowManagedSuggestions(_cleanupFinding.Target);
                break;
            default:
                OpenDesktopTidy();
                break;
        }
    }

    private void OnCleanupNoticeClose(object sender, RoutedEventArgs e)
    {
        CleanupNotice.Visibility = Visibility.Collapsed;
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

        if (ContextHints.For(path, tab.Items) is { } hint)
        {
            _hintKey = hint.Key;
            _cleanupFinding = hint.Finding;
            CleanupNoticeText.Text = hint.Finding.Text;
            CleanupNoticeButton.Content = hint.Button;
            CleanupNotice.Visibility = Visibility.Visible;
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
        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
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
            Shell.Status = "버그 진단 모드를 켰습니다. 문제를 다시 재현한 뒤 버그 진단 모드를 끄면 기록을 보낼 수 있습니다 (24시간 뒤 자동으로 꺼짐)";
            return;
        }

        _settings = _settings with { DiagnosticUntil = null };
        SaveSettings();
        Log.Information("진단 모드 꺼짐");
        Diagnostics.DiagnosticMode.Apply(null);
        UpdateDiagnosticIndicator();
        if (Foldy.UI.Dialogs.FoldyDialog.Confirm(this, "버그 진단 모드", "진단 기록을 개발자에게 오류 보고서로 보낼까요?\n\n최근 기록과 앱·Windows 버전이 들어가며, 파일 경로와 사용자 이름은 지웁니다. GitHub 공개 이슈로 올라갑니다.", "보내기", "보내지 않기"))
        {
            Foldy.Reporting.CrashReporting.Record(new DiagnosticReport("사용자 진단 기록 (버그 진단 모드)"), fatal: false);
            _ = SendReportsAsync();
        }
    }

    private async Task SendReportsAsync()
    {
        var results = await Task.Run(() => Foldy.Reporting.CrashReporting.SendPendingAsync());
        var sent = results.FirstOrDefault(r => r.Outcome == Foldy.Reporting.SendOutcome.Sent);
        Shell.Status = sent?.Response?.Url is { } url ? $"진단 기록을 보냈습니다: {url}" : results.Count == 0 ? "보낼 기록이 없습니다" : $"보내지 못했습니다: {results[^1].Error}";
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
        var index = Path.Combine(src, "index.md");
        if (!File.Exists(index))
        {
            Foldy.UI.Dialogs.FoldyDialog.Info(this, "도움말", "설명서 파일을 찾을 수 없어요.\n" + index, Foldy.UI.Dialogs.DialogKind.Warning);
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

            index = Path.Combine(dst, "index.md");
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
                    Shell.Status = $"Foldy {AppVersion}로 업데이트했어요. 설정은 그대로예요. 바뀐 점은 도움말(F1)에 있어요.";
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

        SidebarToggle.ToolTip = (SidebarExpanded ? "사이드바 접기" : "사이드바 펼치기") + " (Ctrl+Shift+B) · 오른쪽 가장자리를 끌어도 돼요";
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
            Shell.Status = "고정할 폴더가 없습니다. 폴더 탭에서 눌러 주세요.";
            return;
        }

        var name = Foldy.Presentation.Navigation.PathUtil.DisplayName(path);
        if (_quick.IsPinned(path))
        {
            _quick.Unpin(path);
            Shell.Status = $"자주 쓰는 폴더에서 뺐습니다: {name}";
        }
        else if (_quick.Pin(path))
        {
            Shell.Status = $"자주 쓰는 폴더에 고정했습니다: {name}";
        }
        else
        {
            Shell.Status = $"더 고정할 수 없습니다 (최대 {QuickAccess.MaxPinned}개)";
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
            Shell.Status = $"자주 쓰는 폴더에 고정했습니다: {f.Name}";
        }
    }

    private void OnUnpin(object sender, RoutedEventArgs e)
    {
        if (TagOf<QuickFolder>(sender) is { } f && _quick.Unpin(f.Path))
        {
            Shell.Status = $"자주 쓰는 폴더에서 뺐습니다: {f.Name}";
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
            RecycleBinText.Text = $"휴지통 · {Foldy.Presentation.Files.FileItem.FormatBytes(i.Bytes)}";
            EmptyRecycleBinButton.Visibility = Visibility.Visible;
            EmptyRecycleBinButton.ToolTip = $"휴지통 비우기: {i.Items:N0}개, {Foldy.Presentation.Files.FileItem.FormatBytes(i.Bytes)} (영구 삭제, 되살릴 수 없음)";
        }
        else
        {
            RecycleBinText.Text = "휴지통";
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

        if (!Foldy.UI.Dialogs.FoldyDialog.Confirm(this, "휴지통 비우기", $"휴지통의 {i.Items:N0}개 항목({Foldy.Presentation.Files.FileItem.FormatBytes(i.Bytes)})을 영구 삭제할까요?\n\n비우면 되살릴 수 없어요.", "비우기", danger: true))
        {
            return;
        }

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Shell.Status = "휴지통 비우는 중…";
        var ok = await Task.Run(() => Foldy.Platform.Shell.RecycleBin.Empty(hwnd));
        Shell.Status = ok ? $"휴지통을 비웠습니다 ({Foldy.Presentation.Files.FileItem.FormatBytes(i.Bytes)} 확보)" : "휴지통을 비우지 못했습니다";
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
            Shell.Status = "휴지통을 열지 못했습니다";
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
