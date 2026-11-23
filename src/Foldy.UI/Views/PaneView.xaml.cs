using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Foldy.Presentation.Files;
using Foldy.Presentation.Panes;
using Foldy.Presentation.Services;
using Foldy.Presentation.Tabs;
using Foldy.Presentation.Tree;
using Foldy.UI.Controls;

namespace Foldy.UI.Views;

/// <summary>
/// 패널 하나의 화면: 색 탭 줄 → 경로바 → 트리 | 목록 (DESIGN.md 화면 구조).
/// 목록(FastFileList)과 ViewModel 사이의 연결(선택, 포커스, 정렬, Type-ahead, 이름 바꾸기, 끌어 놓기, 셸 메뉴)을 맡는다.
/// </summary>
public partial class PaneView : UserControl
{
    private const string TabDragFormat = "Foldy.Tab";
    private static readonly SortKey[] ColumnKeys = [SortKey.Name, SortKey.Size, SortKey.Modified];

    private IPaneHost? _host;
    private PaneViewModel? _pane;
    private FolderTabViewModel? _tab;
    private SearchTabViewModel? _search;
    private readonly List<FastColumn> _folderColumns = [];
    private readonly List<FastColumn> _searchColumns = [];
    private FolderTreeViewModel? _tree;
    private string _typeAhead = "";
    private DateTime _typeAheadAt;
    private Point _tabDragStart;
    private TabBase? _tabDragCandidate;
    private TextBox? _renameBox;
    private readonly DispatcherTimer _busyTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _busyWatch = new();

    public PaneView()
    {
        InitializeComponent();
        ApplySystemTerminalIcons();
        Loaded += (_, _) => UpdateTreeButton();
        SizeChanged += (_, _) => FitToolbar();
        _folderColumns.AddRange([
            // 좁은 패널: 유형은 이름 왼쪽 아이콘으로만 보이고, 남는 폭은 모두 이름에 준다.
            new FastColumn(Foldy.Core.Localization.Loc.T("pane.col.name"), 320, o => ((FileItem)o).Name),
            new FastColumn(Foldy.Core.Localization.Loc.T("pane.col.size"), 72, o => ((FileItem)o).SizeText, alignRight: true),
            new FastColumn(Foldy.Core.Localization.Loc.T("pane.col.modified"), 68, o => ((FileItem)o).ShortModifiedText) { FontScale = 0.85 },
        ]);
        _searchColumns.AddRange([
            new FastColumn(Foldy.Core.Localization.Loc.T("pane.col.name"), 240, o => ((FileItem)o).Name),
            new FastColumn(Foldy.Core.Localization.Loc.T("pane.col.location"), 220, o => Path.GetDirectoryName(((FileItem)o).FullPath) ?? ""),
            new FastColumn(Foldy.Core.Localization.Loc.T("pane.col.size"), 72, o => ((FileItem)o).SizeText, alignRight: true),
            new FastColumn(Foldy.Core.Localization.Loc.T("pane.col.modified"), 68, o => ((FileItem)o).ShortModifiedText) { FontScale = 0.85 },
        ]);
        UseColumns(_folderColumns);
        List.SortColumn = 0;
        List.IsDimmed = o => o is FileItem f && (f.IsHidden || _tab?.Git?.BadgeOf(f.FullPath) == Foldy.Core.FileSystem.GitFileStatus.Ignored);
        List.ToolTipSelector = o => o is FileItem f && _tab?.Git is { } git ? GitExplain(git.BadgeOf(f.FullPath)) : null;
        List.BadgeSelector = GitBadge;
        List.ChipSelector = TagChips;
        Foldy.Core.Tags.FileTags.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            List.Redraw();
            UpdateTagBar();
        });
        List.SelectionChanged += OnListSelectionChanged;
        List.ItemActivated += OnItemActivated;
        List.HeaderClicked += OnHeaderClicked;
        List.TypeAhead += OnTypeAhead;
        List.ContextRequested += OnContextRequested;
        List.DragRequested += OnDragRequested;
        List.KeyDown += OnListKeyDown;
        Crumbs.NavigateRequested += p => _ = _tab?.NavigateAsync(p);
        PreviewMouseDown += OnPreviewMouseDown;
        GotKeyboardFocus += (_, _) => Activate();
        _busyTimer.Tick += (_, _) => UpdateBusy();
        Scroller.ScrollChanged += (_, _) =>
        {
            CancelRename();
            if (_tab is not null)
            {
                _tab.ScrollOffset = Scroller.VerticalOffset;
            }
        };
    }

    public PaneViewModel? Pane => _pane;

    public void Attach(IPaneHost host, PaneViewModel pane)
    {
        _host = host;
        _pane = pane;
        DataContext = pane;
        TabStrip.ItemsSource = pane.Tabs;
        DriveStrip.ItemsSource = pane.Drives;
        List.IconSelector = host.Icons.Get;
        host.Icons.IconsUpdated += List.Redraw;
        pane.PropertyChanged += OnPaneChanged;
        pane.Tabs.CollectionChanged += (_, _) => UpdateTabVisuals();
        _tree = host.CreateTree();
        Tree.ItemsSource = _tree.Roots;
        pane.Drives.CollectionChanged += (_, _) => _tree.SetDrives(pane.Drives);
        ShowTab(pane.ActiveTab);
        UpdateActive();
    }

    /// <summary>성능 측정(--bench-shell)용: 목록과 그 스크롤러.</summary>
    public (FastFileList List, ScrollViewer Scroller) BenchHandles => (List, Scroller);

    public void FocusList()
    {
        if (_pane?.ActiveTab is FolderTabViewModel)
        {
            List.Focus();
        }
        else
        {
            DocumentLayer.Focus();
        }
    }

    public void BeginPathEdit() => Crumbs.BeginEdit();

    private void Activate()
    {
        if (_host is not null && _pane is not null)
        {
            _host.Shell.Activate(_pane);
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        Activate();
        if (e.ChangedButton == MouseButton.XButton1)
        {
            _ = _tab?.GoBackAsync();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            _ = _tab?.GoForwardAsync();
            e.Handled = true;
        }
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.ActiveTab))
        {
            ShowTab(_pane!.ActiveTab);
        }
        else if (e.PropertyName == nameof(PaneViewModel.IsActive))
        {
            UpdateActive();
        }
    }

    private void UpdateActive()
    {
        ActiveLine.Visibility = _pane?.IsActive == true ? Visibility.Visible : Visibility.Hidden;
    }

    // ───────────── 탭 표시 ─────────────

    private void ShowTab(TabBase? tab)
    {
        CancelRename();
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabChanged;
            _tab.ItemsReplaced -= OnItemsReplaced;
            _tab.FolderSizesLoaded -= List.InvalidateText;
        }

        if (_search is not null)
        {
            _search.PropertyChanged -= OnSearchChanged;
        }

        var previous = _tab;
        _tab = tab as FolderTabViewModel;
        _search = tab as SearchTabViewModel;
        if (previous is not null && !ReferenceEquals(previous, tab) && PreviewVisible)
        {
            TogglePreview(); // 다른 탭(다른 폴더)으로 가면 미리보기는 닫는다
        }

        UpdateTabVisuals();
        _tree?.ShowFor(_tab?.Path); // SFTP 탭이면 그 서버의 폴더 트리
        _ = RevealInTreeAsync(_tab?.Path);

        if (tab is not null and not FolderTabViewModel and not SearchTabViewModel && _host is not null && _pane is not null)
        {
            FolderLayer.Visibility = Visibility.Collapsed;
            ImageLayer.Visibility = Visibility.Collapsed;
            DocumentLayer.Visibility = Visibility.Visible;
            DocumentLayer.Content = _host.CreateTabView(tab, _pane);
            Crumbs.Path = tab.Location ?? "";
            GitChip.Visibility = Visibility.Collapsed;
            BackBtn.IsEnabled = ForwardBtn.IsEnabled = UpBtn.IsEnabled = false;
            return;
        }

        DocumentLayer.Content = null;
        DocumentLayer.Visibility = Visibility.Collapsed;
        FolderLayer.Visibility = Visibility.Visible;
        if (_search is not null)
        {
            ImageLayer.Visibility = Visibility.Collapsed;
            Scroller.Visibility = Visibility.Visible;
            GitChip.Visibility = Visibility.Collapsed;
            UseColumns(_searchColumns);
            List.SortColumn = -1;
            _search.PropertyChanged += OnSearchChanged;
            List.ItemsSource = _search.Items;
            Crumbs.Path = _search.Scope ?? "";
            BackBtn.IsEnabled = ForwardBtn.IsEnabled = UpBtn.IsEnabled = false;
            ErrorBanner.Visibility = EmptyPanel.Visibility = Visibility.Collapsed;
            UpdateSearchState();
            return;
        }

        UseColumns(_folderColumns);
        if (_tab is null)
        {
            List.ItemsSource = null;
            return;
        }

        _tab.PropertyChanged += OnTabChanged;
        _tab.ItemsReplaced += OnItemsReplaced;
        _tab.FolderSizesLoaded += List.InvalidateText;
        List.ItemsSource = _tab.Items;
        List.SortColumn = Array.IndexOf(ColumnKeys, _tab.Sort.Key);
        List.SortDescending = _tab.Sort.Descending;
        RestoreFocus(_tab.FocusName, scrollTo: _tab.ScrollOffset);
        UpdateNav();
        UpdateGit();
        SlowChip.Visibility = _tab.IsSlowDrive ? Visibility.Visible : Visibility.Collapsed;
        UpdateImageLayer();
        UpdateState();
    }

    private void UseColumns(List<FastColumn> cols)
    {
        if (List.Columns.SequenceEqual(cols))
        {
            return;
        }

        List.Columns.Clear();
        List.Columns.AddRange(cols);
        List.InvalidateMeasure();
    }

    private void OnSearchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchTabViewModel.Items))
        {
            List.ItemsSource = _search!.Items;
        }

        UpdateSearchState();
    }

    private void UpdateSearchState()
    {
        if (_search is null)
        {
            return;
        }

        LoadingText.Visibility = Visibility.Visible;
        LoadingText.Text = _search.Status + (_search.IsBusy ? Foldy.Core.Localization.Loc.T("pane.escCancel") : "");
        EmptyPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>지금 보이는 목록과 선택 (폴더 탭 또는 검색 탭).</summary>
    private ItemList? CurrentItems => _tab?.Items ?? _search?.Items;

    private IReadOnlyList<FileItem> CurrentSelection => _tab?.Selection ?? _search?.Selection ?? [];

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _host is not null && _pane is not null)
        {
            _host.Shell.StartSearch(_pane, SearchBox.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            List.Focus();
            e.Handled = true;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) =>
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateTabVisuals()
    {
        if (_pane?.ActiveTab is { } active)
        {
            ThemeBrush.SetTabBackground(TabBand, -1);
            ThemeBrush.SetTabBackground(TabBand, active.ColorIndex);
            ThemeBrush.SetTabBackground(ContentHost, -1);
        }

        // 활성 탭만 진하게 (OneNote처럼 선택된 섹션이 앞으로 나온 느낌)
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var item in TabStrip.Items)
            {
                if (TabStrip.ItemContainerGenerator.ContainerFromItem(item) is ContentPresenter cp &&
                    VisualTreeHelper.GetChildrenCount(cp) > 0 && VisualTreeHelper.GetChild(cp, 0) is Border b)
                {
                    var isActive = item == _pane?.ActiveTab;
                    b.Opacity = isActive ? 1 : 0.62;
                    b.Height = isActive ? 32 : 28;
                    b.Margin = new Thickness(0, isActive ? 0 : 4, 3, 0);
                }
            }

            if (_pane?.ActiveTab is { } a && TabStrip.ItemContainerGenerator.ContainerFromItem(a) is FrameworkElement fe)
            {
                fe.BringIntoView();
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FolderTabViewModel.Items):
                List.ItemsSource = _tab!.Items;
                UpdateState();
                UpdateTagBar();
                if (_tab.ImagesOnly)
                {
                    UpdateImageLayer();
                }

                break;
            case nameof(FolderTabViewModel.Path):
                UpdateNav();
                _tree?.ShowFor(_tab!.Path);
                _ = RevealInTreeAsync(_tab!.Path);
                // 다른 폴더·드라이브로 가면 미리보기는 닫는다 (보던 파일과 상관없는 곳, 사용자 요청 2026-11-21). F3으로 다시 켠다
                if (PreviewVisible)
                {
                    TogglePreview();
                }

                _ = UpdateManagedBarAsync();
                break;
            case nameof(FolderTabViewModel.Git):
                UpdateGit();
                break;
            case nameof(FolderTabViewModel.IsSlowDrive):
                SlowChip.Visibility = _tab!.IsSlowDrive ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(FolderTabViewModel.State):
            case nameof(FolderTabViewModel.Error):
            case nameof(FolderTabViewModel.LoadedCount):
            case nameof(FolderTabViewModel.IsBusy):
                UpdateState();
                break;
            case nameof(FolderTabViewModel.Sort):
                List.SortColumn = Array.IndexOf(ColumnKeys, _tab!.Sort.Key);
                List.SortDescending = _tab.Sort.Descending;
                List.Redraw();
                break;
            case nameof(FolderTabViewModel.CanGoBack):
            case nameof(FolderTabViewModel.CanGoForward):
            case nameof(FolderTabViewModel.CanGoUp):
                UpdateNav();
                break;
        }
    }

    private (string Text, Brush Brush)? GitBadge(object o)
    {
        if (_tab?.Git is not { } g || o is not FileItem f)
        {
            return null;
        }

        // M·A·?·! 같은 Git 약자 대신 처음 봐도 아는 말로, Git 화면과 같은 말 (배워야 쓰면 실패, DECISIONS 2026-11-20). 마우스를 올리면 설명
        var (text, key) = g.BadgeOf(f.FullPath) switch
        {
            Foldy.Core.FileSystem.GitFileStatus.Modified => (Foldy.Core.Localization.Loc.T("pane.col.modified"), "Brush.Progress"),
            Foldy.Core.FileSystem.GitFileStatus.Added => (Foldy.Core.Localization.Loc.T("pane.git.added"), "Brush.Done"),
            Foldy.Core.FileSystem.GitFileStatus.Untracked => (Foldy.Core.Localization.Loc.T("pane.git.untracked"), "Brush.TextSecondary"),
            Foldy.Core.FileSystem.GitFileStatus.Conflicted => (Foldy.Core.Localization.Loc.T("pane.git.conflict"), "Brush.Error"),
            _ => (null, null),
        };
        return text is not null && TryFindResource(key) is Brush b ? (text, b) : null;
    }

    /// <summary>Git 상태 표시에 마우스를 올렸을 때의 설명 (Git을 몰라도 알 수 있게).</summary>
    public static string? GitExplain(Foldy.Core.FileSystem.GitFileStatus s) => s switch
    {
        Foldy.Core.FileSystem.GitFileStatus.Modified => Foldy.Core.Localization.Loc.T("pane.git.tipModified"),
        Foldy.Core.FileSystem.GitFileStatus.Added => Foldy.Core.Localization.Loc.T("pane.git.tipAdded"),
        Foldy.Core.FileSystem.GitFileStatus.Untracked => Foldy.Core.Localization.Loc.T("pane.git.tipUntracked"),
        Foldy.Core.FileSystem.GitFileStatus.Conflicted => Foldy.Core.Localization.Loc.T("pane.git.tipConflict"),
        Foldy.Core.FileSystem.GitFileStatus.Ignored => Foldy.Core.Localization.Loc.T("pane.git.tipIgnored"),
        _ => null,
    };

    private void OnSftpChip(object sender, RoutedEventArgs e)
    {
        if (_host is not null && _pane is not null)
        {
            _host.ConnectSftp(_pane);
        }
    }

    private string? _gitDetectedRoot;

    /// <summary>설정이 바뀐 뒤: Git 바를 다시 판단하고 자동 열기도 다시 평가한다.</summary>
    public void RefreshGit()
    {
        _gitDetectedRoot = null;
        UpdateGit();
    }

    private void UpdateGit()
    {
        var g = _tab?.Git;
        GitChip.Visibility = g is null ? Visibility.Collapsed : Visibility.Visible;
        GitText.Text = g is null ? "" : g.Changes > 0 ? Foldy.Core.Localization.Loc.F("pane.git.branchChanges", g.BranchText, g.Changes) : g.BranchText;
        GitChip.ToolTip = g?.RepoRoot;
        UpdateGitBar();
        _ = UpdateManagedBarAsync();
        List.Redraw();
        if (g is not null && _host is not null && _pane is not null && !string.Equals(_gitDetectedRoot, g.RepoRoot, StringComparison.OrdinalIgnoreCase))
        {
            _gitDetectedRoot = g.RepoRoot;
            _host.GitDetected(_pane, g.RepoRoot);
        }
        else if (g is null)
        {
            _gitDetectedRoot = null;
        }
    }

    /// <summary>Git 바를 보이거나 숨긴다 (설정 변경 때도 부른다). 높이를 짧게 펼쳐 목록이 갑자기 밀리지 않게 한다.</summary>
    public void UpdateGitBar()
    {
        var g = _tab?.Git;
        var show = g is not null && _host?.ShowGitBar == true;
        // Git 바가 보이면 경로 옆 Git 칩은 같은 내용이라 숨긴다 (좁은 화면에서 경로가 잘리지 않게)
        GitChip.Visibility = g is not null && !show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            GitBarBranch.Text = g!.BranchText;
            GitBarChanges.Text = g.Changes > 0 ? Foldy.Core.Localization.Loc.F("pane.git.changes", g.Changes) : Foldy.Core.Localization.Loc.T("pane.git.clean");
            GitBar.ToolTip = g.RepoRoot;
        }

        const double full = 38;
        var target = show ? full : 0;
        if (!show && GitBar.Height == 0)
        {
            GitBar.Visibility = Visibility.Collapsed;
            return;
        }

        GitBar.Visibility = Visibility.Visible;
        if (Math.Abs(GitBar.Height - target) < 0.5)
        {
            return;
        }

        if (!SystemParameters.ClientAreaAnimation || !IsLoaded)
        {
            GitBar.BeginAnimation(HeightProperty, null);
            GitBar.Height = target;
            if (!show) GitBar.Visibility = Visibility.Collapsed;
            return;
        }

        var anim = new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase(),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        };
        anim.Completed += (_, _) =>
        {
            GitBar.Height = target;
            if (!show) GitBar.Visibility = Visibility.Collapsed;
        };
        GitBar.BeginAnimation(HeightProperty, anim);
    }

    private void GitBarClick(GitBarCommand command)
    {
        if (_tab?.Git is { } g && _host is not null && _pane is not null)
        {
            _host.GitBarAction(_pane, g.RepoRoot, command);
        }
    }

    private void OnGitBarPull(object sender, RoutedEventArgs e) => GitBarClick(GitBarCommand.Pull);

    private void OnGitBarPush(object sender, RoutedEventArgs e) => GitBarClick(GitBarCommand.Push);

    private void OnGitBarCommit(object sender, RoutedEventArgs e) => GitBarClick(GitBarCommand.Commit);

    private void OnGitBarOpen(object sender, RoutedEventArgs e) => GitBarClick(GitBarCommand.Open);

    private void UpdateNav()
    {
        if (_tab is null)
        {
            return;
        }

        Crumbs.Path = _tab.Path;
        UpdateTerminalButtons(_tab.Path);
        BackBtn.IsEnabled = _tab.CanGoBack;
        ForwardBtn.IsEnabled = _tab.CanGoForward;
        UpBtn.IsEnabled = _tab.CanGoUp;
    }

    /// <summary>경과 시간별 표현 (DESIGN.md): 0~300ms 표시 없음, 이후 진행선, 2초 넘으면 개수.</summary>
    private void UpdateState()
    {
        if (_tab is null)
        {
            return;
        }

        var busy = _tab.IsBusy;
        if (busy && !_busyTimer.IsEnabled)
        {
            _busyWatch.Restart();
            _busyTimer.Start();
        }
        else if (!busy)
        {
            _busyTimer.Stop();
            Crumbs.SetProgress(null);
            LoadingText.Visibility = Visibility.Collapsed;
        }

        ErrorBanner.Visibility = _tab.State == LoadState.Error && _tab.Error is not null ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = _tab.Error ?? "";
        EmptyPanel.Visibility = !busy && _tab.State == LoadState.Idle && _tab.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 네트워크·리다이렉터 드라이브 루트가 비어 보이면 보안 프로그램(예: 회사 보안 드라이브 프로그램)이 탐색기에만 내용을 허용했을 수 있다.
        var restrictedRoot = EmptyPanel.Visibility == Visibility.Visible && IsRemoteDriveRoot(_tab.Path);
        EmptyText.Text = restrictedRoot
            ? Foldy.Core.Localization.Loc.T("pane.blocked")
            : Foldy.Core.Localization.Loc.T("pane.empty");
        EmptyExplorerButton.Visibility = restrictedRoot ? Visibility.Visible : Visibility.Collapsed;
        var emptyLocal = EmptyPanel.Visibility == Visibility.Visible && !restrictedRoot && _tab.Path is { Length: >= 3 } ep && ep[1] == ':';
        EmptyTip.Visibility = emptyLocal ? Visibility.Visible : Visibility.Collapsed;
        EmptyTemplateButton.Visibility = emptyLocal ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEmptyTemplate(object sender, RoutedEventArgs e)
    {
        Activate(); // 이 창의 폴더 안에 만든다
        _host?.RunCommand("folder.fromTemplate");
    }

    /// <summary>터미널 버튼을 Windows 원래 아이콘으로 (cmd·PowerShell 실행 파일 아이콘, 관리자는 UAC 방패를 겹침). 못 얻으면 글꼴 아이콘 유지.</summary>
    private void ApplySystemTerminalIcons()
    {
        var ps = Foldy.Platform.Shell.Terminals.Pwsh() is { } p && !p.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
            ? p
            : Foldy.Platform.Shell.Terminals.WindowsPowerShell();
        if (Foldy.UI.Shell.SystemToolIcons.Content(Foldy.Platform.Shell.Terminals.Cmd(), admin: false) is { } cmd)
        {
            CmdBtn.Content = cmd;
        }

        if (Foldy.UI.Shell.SystemToolIcons.Content(ps, admin: false) is { } psIcon)
        {
            PsBtn.Content = psIcon;
        }

        if (Foldy.UI.Shell.SystemToolIcons.Content(ps, admin: true) is { } admin)
        {
            AdminBtn.Content = admin;
        }
    }

    private static bool IsRemoteDriveRoot(string path) =>
        path.Length is 2 or 3 && path[1] == ':' && Foldy.Platform.SystemInfo.Drives.TypeOf(path[..2] + "\\") == Foldy.Platform.SystemInfo.NativeDriveType.Remote;

    private void OnOpenInExplorer(object sender, RoutedEventArgs e)
    {
        if (_tab?.Path is { Length: > 0 } p)
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true });
        }
    }

    private void UpdateBusy()
    {
        if (_tab is not { IsBusy: true })
        {
            _busyTimer.Stop();
            return;
        }

        var ms = _busyWatch.ElapsedMilliseconds;
        if (ms > 300)
        {
            // 총량을 모르므로 왕복하는 진행선
            var phase = ms / 1200.0 % 1.0;
            Crumbs.SetProgress(0.15 + 0.85 * phase);
        }

        if (ms > 2000)
        {
            LoadingText.Visibility = Visibility.Visible;
            LoadingText.Text = Foldy.Core.Localization.Loc.F("pane.loading", _tab.LoadedCount, ms / 1000.0);
        }
    }

    // ───────────── 목록 ↔ ViewModel ─────────────

    private void OnItemsReplaced(string? focus) => RestoreFocus(focus, scrollTo: null);

    private void RestoreFocus(string? name, double? scrollTo)
    {
        if (_tab is null)
        {
            return;
        }

        var i = name is null ? -1 : _tab.Items.IndexOfName(name);
        if (i >= 0)
        {
            List.FocusIndex(i, select: true);
        }
        else if (scrollTo is { } y)
        {
            List.SetVerticalOffset(y);
        }
    }

    private void OnListSelectionChanged(object? sender, EventArgs e)
    {
        if (CurrentItems is not { } items)
        {
            return;
        }

        var sel = new List<FileItem>(List.SelectedCount);
        foreach (var i in List.SelectedIndices())
        {
            if (i < items.Count)
            {
                sel.Add(items[i]);
            }
        }

        if (_search is not null)
        {
            _search.Selection = sel;
        }
        else if (_tab is not null)
        {
            _tab.Selection = sel;
            _tab.FocusName = List.FocusedIndex >= 0 && List.FocusedIndex < items.Count ? items[List.FocusedIndex].Name : null;
        }
        SelectionInfoChanged?.Invoke(this, EventArgs.Empty);
        UpdatePreview();
    }

    // ───────────── 미리보기 (F3) ─────────────

    private static Foldy.Presentation.Preview.PreviewProviderRegistry? s_previewRegistry;
    private Foldy.Presentation.Preview.PreviewViewModel? _preview;

    public bool PreviewVisible => PreviewHost.Visibility == Visibility.Visible;

    public void TogglePreview()
    {
        if (PreviewVisible)
        {
            PreviewHost.Visibility = Visibility.Collapsed;
            PreviewSplitter.Visibility = Visibility.Collapsed;
            PreviewSplitRow.Height = new GridLength(0);
            PreviewRow.Height = new GridLength(0);
            _ = _preview?.ShowAsync((Foldy.Presentation.Preview.PreviewRequest?)null);
            return;
        }

        if (_preview is null)
        {
            s_previewRegistry ??= Foldy.Presentation.Preview.PreviewProviderRegistry.CreateDefault(
                shellHandlerAvailable: Foldy.Platform.Preview.ShellPreviewHandlers.IsAvailable,
                isImage: Foldy.Platform.Imaging.ImageFormats.IsImage);
            _preview = new Foldy.Presentation.Preview.PreviewViewModel(s_previewRegistry);
            PreviewHost.Child = new Foldy.UI.Preview.PreviewPane { DataContext = _preview };
        }

        PreviewHost.Visibility = Visibility.Visible;
        PreviewSplitter.Visibility = Visibility.Visible;
        PreviewSplitRow.Height = new GridLength(6);
        PreviewRow.Height = new GridLength(Math.Max(180, ActualHeight * 0.4));
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_preview is null || !PreviewVisible)
        {
            return;
        }

        FileItem? item = null;
        if (CurrentItems is { } items && List.FocusedIndex >= 0 && List.FocusedIndex < items.Count)
        {
            item = items[List.FocusedIndex];
        }

        _ = _preview.ShowAsync(item);
    }

    /// <summary>선택 개수·용량 표시용 (상태바).</summary>
    public event EventHandler? SelectionInfoChanged;

    /// <summary>[이 창만 크게]/[두 창으로 보기]를 눌렀다 (주 창이 다른 창을 접거나 다시 연다).</summary>
    public event EventHandler? SinglePaneToggleRequested;

    private void OnToggleSingle(object sender, RoutedEventArgs e) => SinglePaneToggleRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>이 창 하나만 크게 보이는 중이면 단추를 [두 창으로 보기]로 바꾼다.</summary>
    public void SetSinglePane(bool single)
    {
        SingleBtn.Content = single ? "\uE73F" : "\uE740";
        var name = Foldy.Core.Localization.Loc.T(single ? "pane.single.off" : "pane.single.on");
        SingleBtn.ToolTip = Foldy.Core.Localization.Loc.T(single ? "pane.single.off.tip" : "pane.single.on.tip");
        System.Windows.Automation.AutomationProperties.SetName(SingleBtn, name);
        if (single)
        {
            SingleBtn.SetResourceReference(BackgroundProperty, "Brush.Selection");
        }
        else
        {
            SingleBtn.ClearValue(BackgroundProperty);
        }
    }

    private void OnItemActivated(object? sender, int index)
    {
        if (CurrentItems is not { } items || _host is null || _pane is null || index >= items.Count)
        {
            return;
        }

        _ = _host.Shell.OpenAsync(_pane, items[index]);
    }

    private void OnHeaderClicked(object? sender, int col)
    {
        if (_tab is not null && col >= 0 && col < ColumnKeys.Length)
        {
            _ = _tab.SetSortAsync(ColumnKeys[col]);
        }
    }

    private void OnTypeAhead(object? sender, string text)
    {
        if (_tab is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        _typeAhead = now - _typeAheadAt > TimeSpan.FromMilliseconds(900) ? text : _typeAhead + text;
        _typeAheadAt = now;
        // 같은 글자를 반복해 누르면 그 글자로 시작하는 다음 항목으로
        var repeat = _typeAhead.Length > 1 && _typeAhead.All(c => c == _typeAhead[0]);
        var prefix = repeat ? _typeAhead[..1] : _typeAhead;
        var start = _typeAhead.Length == 1 || repeat ? List.FocusedIndex + 1 : Math.Max(0, List.FocusedIndex);
        var i = _tab.FindPrefix(prefix, start);
        if (i >= 0)
        {
            List.FocusIndex(i, select: true);
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (_search is not null && e.Key == Key.Escape)
        {
            _search.Cancel();
            e.Handled = true;
            return;
        }

        if (_search is not null && _host is not null)
        {
            var m = Keyboard.Modifiers;
            if (e.Key == Key.Delete)
            {
                _ = _host.Shell.DeleteSelectionAsync(permanent: (m & ModifierKeys.Shift) != 0).ContinueWith(_ => Dispatcher.BeginInvoke(() => _search?.RunAsync()), TaskScheduler.Default);
                e.Handled = true;
            }
            else if (e.Key == Key.C && m == ModifierKeys.Control)
            {
                Clipboard_Set(move: false);
                e.Handled = true;
            }

            return;
        }

        if (_tab is null || _host is null)
        {
            return;
        }

        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Back when mods == ModifierKeys.None:
                _ = _tab.GoUpAsync();
                e.Handled = true;
                break;
            case Key.F3 when mods == ModifierKeys.None:
                TogglePreview();
                e.Handled = true;
                break;
            case Key.F2 when mods == ModifierKeys.None:
                BeginRename();
                e.Handled = true;
                break;
            case Key.D when mods == ModifierKeys.Control:
                ToggleTag(Foldy.Core.Tags.FileTags.Default);
                e.Handled = true;
                break;
            case Key.Delete:
                _ = _host.Shell.DeleteSelectionAsync(permanent: (mods & ModifierKeys.Shift) != 0);
                e.Handled = true;
                break;
            case Key.C when mods == ModifierKeys.Control:
                Clipboard_Set(move: false);
                e.Handled = true;
                break;
            case Key.X when mods == ModifierKeys.Control:
                Clipboard_Set(move: true);
                e.Handled = true;
                break;
            case Key.V when mods == ModifierKeys.Control:
                _ = PasteAsync();
                e.Handled = true;
                break;
            case Key.Escape when _tab.IsBusy:
                _tab.CancelLoad();
                e.Handled = true;
                break;
        }
    }

    // ───────────── 이름 바꾸기 (제자리 입력) ─────────────

    public void BeginRename()
    {
        if (_tab is null || List.FocusedIndex < 0 || List.FocusedIndex >= _tab.Items.Count)
        {
            return;
        }

        CancelRename();
        var index = List.FocusedIndex;
        var item = _tab.Items[index];
        List.ScrollIntoView(index);
        UpdateLayout();
        var r = List.NameCellRect(index);
        var box = new TextBox
        {
            Text = item.Name,
            Width = r.Width,
            Height = r.Height,
            Padding = new Thickness(3, 0, 3, 0),
            BorderThickness = new Thickness(1),
            Tag = item,
        };
        box.SetResourceReference(BorderBrushProperty, "Brush.Accent");
        box.SetResourceReference(BackgroundProperty, "Brush.SurfaceAlt");
        Canvas.SetLeft(box, r.X);
        Canvas.SetTop(box, r.Y);
        Overlay.Children.Add(box);
        _renameBox = box;
        box.Focus();
        // 확장자는 빼고 선택 (탐색기와 같음)
        var dot = item.IsFolder ? -1 : item.Name.LastIndexOf('.');
        box.Select(0, dot > 0 ? dot : item.Name.Length);
        box.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await CommitRenameAsync(box);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelRename();
                List.Focus();
            }
        };
        box.LostKeyboardFocus += async (_, _) => await CommitRenameAsync(box);
    }

    private async Task CommitRenameAsync(TextBox box)
    {
        if (_renameBox != box || _tab is null || _host is null)
        {
            return;
        }

        _renameBox = null;
        Overlay.Children.Remove(box);
        var item = (FileItem)box.Tag;
        await _host.Shell.RenameAsync(_tab, item, box.Text);
        List.Focus();
    }

    private void CancelRename()
    {
        if (_renameBox is not null)
        {
            Overlay.Children.Remove(_renameBox);
            _renameBox = null;
        }
    }

    /// <summary>좁은 패널(작은 노트북 화면)에서는 검색 칸을 줄여 경로가 보이게 한다.</summary>
    private void FitToolbar()
    {
        var narrow = ActualWidth < 760;
        SearchBorder.Width = narrow ? 120 : 200;
        SearchHint.Text = narrow ? Foldy.Core.Localization.Loc.T("pane.searchTip") : Foldy.Core.Localization.Loc.T("pane.searchNameTip");

        // 아주 좁으면 파일 이름에 자리를 주려고 폴더 트리를 접는다 (Ctrl+B로 언제든 다시)
        if (!_treeManual && IsLoaded)
        {
            if (ActualWidth < 620 && TreeVisible)
            {
                TreeVisible = false;
                _treeAutoHidden = true;
            }
            else if (ActualWidth >= 720 && _treeAutoHidden)
            {
                TreeVisible = true;
                _treeAutoHidden = false;
            }
        }
    }

    // ───────────── 작업 태그 ─────────────

    private static readonly Dictionary<string, Brush> TagBrushes = [];

    private static Brush TagBrush(string color)
    {
        if (!TagBrushes.TryGetValue(color, out var b))
        {
            b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            b.Freeze();
            TagBrushes[color] = b;
        }

        return b;
    }

    private IReadOnlyList<(string Text, Brush Brush)> TagChips(object o)
    {
        if (o is not FileItem f || f.IsFolder || _tab is null || _tab.IsSlowDrive || f.FullPath.Length < 3 || f.FullPath[1] != ':')
        {
            return [];
        }

        var tags = Foldy.Core.Tags.FileTags.Get(f.FullPath);
        return tags.Count == 0 ? [] : [.. tags.Select(t => (Foldy.Core.Tags.FileTags.DisplayName(t), TagBrush(Foldy.Core.Tags.FileTags.Def(t).Color)))];
    }

    /// <summary>고른 파일들에 태그를 켜고 끈다 (Ctrl+D = 작업 완료).</summary>
    public void ToggleTag(string tag)
    {
        var files = CurrentSelection.Where(i => !i.IsFolder).Select(i => i.FullPath).Where(p => p.Length > 2 && p[1] == ':').ToList();
        if (files.Count == 0)
        {
            return;
        }

        Foldy.Core.Tags.FileTags.Toggle(files, tag);
        _host?.Shell.Status = Foldy.Core.Tags.FileTags.Has(files[0], tag) ? Foldy.Core.Localization.Loc.F("pane.tagged", Foldy.Core.Tags.FileTags.DisplayName(tag), files.Count) : Foldy.Core.Localization.Loc.F("pane.untagged", Foldy.Core.Tags.FileTags.DisplayName(tag), files.Count);
    }

    /// <summary>이 폴더에 태그가 있으면 위쪽에 진행 상황을 한 줄로.</summary>
    private void UpdateTagBar()
    {
        if (_tab?.Items is not { } items || _tab.IsSlowDrive || _tab.Path.Length < 3 || _tab.Path[1] != ':')
        {
            TagBar.Visibility = Visibility.Collapsed;
            return;
        }

        var files = items.Where(i => !i.IsFolder).Select(i => i.FullPath).ToList();
        var counts = Foldy.Core.Tags.FileTags.Counts(files);
        if (counts.Count == 0)
        {
            TagBar.Visibility = Visibility.Collapsed;
            return;
        }

        TagBarText.Text = Foldy.Core.Localization.Loc.F("pane.ofFiles", files.Count) + string.Join(" · ", counts.Select(c => $"{Foldy.Core.Tags.FileTags.DisplayName(c.Tag.Name)} {c.Count:N0}"));
        TagNextButton.IsEnabled = files.Any(p => !Foldy.Core.Tags.FileTags.Has(p, Foldy.Core.Tags.FileTags.Default));
        var inGit = Foldy.Core.Tags.FileTags.GitDir(_tab.Path) is not null;
        TagShareToggle.Visibility = inGit ? Visibility.Visible : Visibility.Collapsed;
        TagShareToggle.IsChecked = inGit && Foldy.Core.Tags.FileTags.IsSharedInGit(_tab.Path);
        TagBar.Visibility = Visibility.Visible;
    }

    private void OnTagNext(object sender, RoutedEventArgs e)
    {
        if (_tab?.Items is not { } items)
        {
            return;
        }

        var list = items.ToList();
        var start = Math.Max(0, List.FocusedIndex + 1);
        for (var k = 0; k < list.Count; k++)
        {
            var i = (start + k) % list.Count;
            if (!list[i].IsFolder && !Foldy.Core.Tags.FileTags.Has(list[i].FullPath, Foldy.Core.Tags.FileTags.Default))
            {
                List.FocusIndex(i);
                List.Focus();
                return;
            }
        }
    }

    private void OnTagShare(object sender, RoutedEventArgs e)
    {
        if (_tab?.Path is not { } p)
        {
            return;
        }

        var on = TagShareToggle.IsChecked == true;
        if (on && !Foldy.UI.Dialogs.FoldyDialog.Confirm(Window.GetWindow(this), Foldy.Core.Localization.Loc.T("pane.tagShare"), Foldy.Core.Localization.Loc.T("pane.tagShareAsk"), Foldy.Core.Localization.Loc.T("pane.tagShareOn"), Foldy.Core.Localization.Loc.T("pane.tagShareOff"), danger: true))
        {
            TagShareToggle.IsChecked = false;
            return;
        }

        Foldy.Core.Tags.FileTags.SetSharedInGit(p, on);
        if (_host is not null)
        {
            _host.Shell.Status = on ? Foldy.Core.Localization.Loc.T("pane.tagSharedTip") : Foldy.Core.Localization.Loc.T("pane.tagLocalTip");
        }
    }

    private void OnTagClear(object sender, RoutedEventArgs e)
    {
        if (_tab?.Path is { } p)
        {
            var n = Foldy.Core.Tags.FileTags.ClearFolder(p);
            _host?.Shell.Status = Foldy.Core.Localization.Loc.F("pane.tagsCleared", n);
        }
    }

    // ───────────── 관리 방식 (.foldy.md) ─────────────

    private string? _managedRoot;
    private int _managedVersion;

    /// <summary>
    /// 지금 폴더가 관리 폴더 안이면 한 줄로 알린다: "PARA로 관리 중 · 정리할 것 3가지".
    /// 로컬 고정 드라이브의 빠른 폴더에서만 확인한다 (느린·네트워크 드라이브는 건너뜀).
    /// </summary>
    private async Task UpdateManagedBarAsync()
    {
        var version = ++_managedVersion;
        var path = _tab?.Path;
        if (path is null || _tab!.Git is not null || path.Length < 3 || path[1] != ':' || _tab.IsSlowDrive
            || Foldy.Platform.SystemInfo.Drives.TypeOf(path[..3]) != Foldy.Platform.SystemInfo.NativeDriveType.Fixed)
        {
            ManagedBar.Visibility = Visibility.Collapsed;
            _managedRoot = null;
            return;
        }

        var found = await Task.Run(() =>
        {
            var r = Foldy.Core.Managed.ManagedFolders.Resolve(path);
            if (r is not { } m)
            {
                return ((string Root, string Name, int Count)?)null;
            }

            int count;
            try
            {
                count = Foldy.Core.Managed.ManagedFolders.Suggest(m.Root, m.Method, DateTime.UtcNow).Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                count = 0;
            }

            return (m.Root, m.Method.DisplayName, count);
        });
        if (version != _managedVersion)
        {
            return;
        }

        if (found is not { } f)
        {
            ManagedBar.Visibility = Visibility.Collapsed;
            _managedRoot = null;
            return;
        }

        _managedRoot = f.Root;
        var here = string.Equals(f.Root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        ManagedBarText.Text = (here ? Foldy.Core.Localization.Loc.F("pane.managed", f.Name) : Foldy.Core.Localization.Loc.F("pane.managedIn", f.Name, System.IO.Path.GetFileName(f.Root.TrimEnd('\\'))))
            + (f.Count > 0 ? Foldy.Core.Localization.Loc.F("pane.toTidy", f.Count) : Foldy.Core.Localization.Loc.T("pane.tidyOk"));
        ManagedBar.ToolTip = Foldy.Core.Localization.Loc.F("pane.managedTip", f.Root);
        ManagedBarButton.Visibility = f.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ManagedBar.Visibility = Visibility.Visible;
    }

    private void OnManagedBarOpen(object sender, RoutedEventArgs e)
    {
        if (_managedRoot is not null)
        {
            _host?.ShowManagedSuggestions(_managedRoot);
        }
    }

    private void OnManagedBarSettings(object sender, RoutedEventArgs e)
    {
        if (_managedRoot is not null)
        {
            _host?.ManageFolder(_managedRoot);
        }
    }

    /// <summary>관리 방식을 바꾼 뒤 다시 확인 (호스트가 부른다).</summary>
    public void RefreshManaged()
    {
        _ = UpdateManagedBarAsync();
        List.Redraw();
    }

    // ───────────── 셸 메뉴 ─────────────

    private void OnContextRequested(object? sender, FileListContextEventArgs e)
    {
        if (_host is null || (_tab is null && _search is null))
        {
            return;
        }

        var paths = e.Index >= 0 ? CurrentSelection.Select(i => i.FullPath).ToArray() : [];
        if (paths.Length == 0 && _tab is null)
        {
            return; // 검색 결과에는 배경 메뉴가 없다
        }
        var extended = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        // PointToScreen은 이미 장치 픽셀
        // 폴더 하나(또는 빈 곳 = 지금 폴더)에서는 "이 폴더 관리 방식 정하기"를 맨 위에 덧붙인다 (로컬 드라이브만)
        var target = paths.Length == 1 && e.Index >= 0 && CurrentSelection[0].IsFolder ? paths[0] : paths.Length == 0 ? _tab?.Path : null;
        var local = (target ?? (paths.Length == 1 ? paths[0] : null)) is { Length: > 2 } lp && lp[1] == ':';
        var oneFile = paths.Length == 1 && e.Index >= 0 && !CurrentSelection[0].IsFolder && !paths[0].EndsWith(".foldylock", StringComparison.OrdinalIgnoreCase)
            && !paths[0].EndsWith(".foldyvault", StringComparison.OrdinalIgnoreCase);
        var oneFolder = paths.Length == 1 && e.Index >= 0 && CurrentSelection[0].IsFolder;
        string[]? extra = !local ? null
            : oneFile ? [Foldy.Core.Localization.Loc.T("pane.menu.encrypt"), Foldy.Core.Localization.Loc.T("pane.menu.versions")]
            : oneFolder ? [Foldy.Core.Localization.Loc.T("sw.c.method"), Foldy.Core.Localization.Loc.T("pane.menu.vault")]
            : target is not null ? [Foldy.Core.Localization.Loc.T("sw.c.method")] : null;
        var verb = _host.ShellMenu.Show(
            _host.OwnerHandle, paths, (int)e.ScreenPoint.X, (int)e.ScreenPoint.Y, extended,
            backgroundFolder: paths.Length == 0 ? _tab!.Path : null, extraItems: extra);
        if (verb == "foldy:0" && oneFile)
        {
            _host.LockFile(paths[0]);
            return;
        }

        if (verb == "foldy:1" && oneFile)
        {
            _host.ShowVersions(paths[0]);
            return;
        }

        if (verb == "foldy:0" && target is not null)
        {
            _host.ManageFolder(target);
            return;
        }

        if (verb == "foldy:1" && oneFolder)
        {
            _host.PutInVault(paths[0]);
            return;
        }

        if (verb == "rename")
        {
            BeginRename();
        }
        else if (verb is not null)
        {
            // 삭제·붙여넣기·새로 만들기 등 무엇이든 바뀌었을 수 있다 (감시가 없는 드라이브 대비)
            _ = Task.Delay(400).ContinueWith(_ => Dispatcher.BeginInvoke(() => _tab?.RefreshAsync()), TaskScheduler.Default);
            if (_search is not null && verb is "delete")
            {
                _ = _search.RunAsync();
            }
        }
    }

    // ───────────── 클립보드 ─────────────

    private void Clipboard_Set(bool move)
    {
        var selection = CurrentSelection;
        if (selection.Count == 0)
        {
            return;
        }

        var data = BuildDataObject(selection.Select(i => i.FullPath).ToArray(), move ? DragDropEffects.Move : DragDropEffects.Copy);
        try
        {
            Clipboard.SetDataObject(data, copy: true);
            _host!.Shell.Status = move ? Foldy.Core.Localization.Loc.F("pane.cutN", selection.Count) : Foldy.Core.Localization.Loc.F("pane.copiedN", selection.Count);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _host!.Shell.Status = Foldy.Core.Localization.Loc.T("pane.clipWrite") + ex.Message;
        }
    }

    private async Task PasteAsync()
    {
        if (_tab is null || _host is null)
        {
            return;
        }

        try
        {
            if (!Clipboard.ContainsFileDropList())
            {
                return;
            }

            var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
            var move = Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4 && (BitConverter.ToInt32(ms.ToArray(), 0) & 2) != 0;
            await _host.Shell.TransferAsync(files, _tab.Path, move);
            if (move)
            {
                Clipboard.Clear();
            }
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _host.Shell.Status = Foldy.Core.Localization.Loc.T("pane.clipRead") + ex.Message;
        }
    }

    private static DataObject BuildDataObject(string[] paths, DragDropEffects effect)
    {
        var data = new DataObject();
        var list = new System.Collections.Specialized.StringCollection();
        list.AddRange(paths);
        data.SetFileDropList(list);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)effect)));
        return data;
    }

    // ───────────── 끌어 놓기 (탐색기와 양방향) ─────────────

    private void OnDragRequested(object? sender, EventArgs e)
    {
        if (CurrentSelection.Count == 0)
        {
            return;
        }

        var data = BuildDataObject(CurrentSelection.Select(i => i.FullPath).ToArray(), DragDropEffects.Copy);
        _dragSourceFolder = _tab?.Path;
        try
        {
            DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _dragSourceFolder = null;
        }

        // 다른 곳으로 옮겨졌으면 목록 갱신
        _ = _tab?.RefreshAsync();
    }

    private static string? _dragSourceFolder;

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropEffectFor(e);
        e.Handled = true;
    }

    private DragDropEffects DropEffectFor(DragEventArgs e)
    {
        if (_tab is null || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return DragDropEffects.None;
        }

        if (string.Equals(_dragSourceFolder, _tab.Path, StringComparison.OrdinalIgnoreCase))
        {
            return DragDropEffects.None; // 같은 폴더에 놓기는 무시
        }

        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0)
        {
            return DragDropEffects.Move & e.AllowedEffects;
        }

        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0)
        {
            return DragDropEffects.Copy & e.AllowedEffects;
        }

        // 탐색기 규칙: 같은 드라이브면 이동, 다르면 복사
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        var sameDrive = files is { Length: > 0 } && !files.Any(Foldy.Presentation.Navigation.PathUtil.IsSftp) && !Foldy.Presentation.Navigation.PathUtil.IsSftp(_tab.Path) && string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(_tab.Path), StringComparison.OrdinalIgnoreCase);
        var want = sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
        return (want & e.AllowedEffects) != 0 ? want : DragDropEffects.Copy & e.AllowedEffects;
    }

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        var effect = DropEffectFor(e);
        if (effect == DragDropEffects.None || _tab is null || _host is null || e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        e.Effects = effect;
        e.Handled = true;
        Activate();
        await _host.Shell.TransferAsync(files, _tab.Path, move: effect == DragDropEffects.Move);
    }

    // ───────────── 탭 줄 ─────────────

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabBase tab } && _pane is not null)
        {
            _pane.ActiveTab = tab;
            _tabDragStart = e.GetPosition(this);
            _tabDragCandidate = tab;
            if (e.ClickCount == 2)
            {
                _pane.NewFolderTab(tab.Location ?? "C:\\");
            }
        }
    }

    private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        _tabDragCandidate = null;
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { Tag: TabBase tab })
        {
            _pane?.CloseTab(tab);
            e.Handled = true;
        }
    }

    private void OnTabMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance)
        {
            return;
        }

        var tab = _tabDragCandidate;
        _tabDragCandidate = null;
        var data = new DataObject(TabDragFormat, new TabDrag(tab, _pane!));
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    private void OnTabDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TabDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>탭을 끌어 순서를 바꾸거나 반대 패널로 옮긴다 (설계서 2장).</summary>
    private void OnTabDrop(object sender, DragEventArgs e)
    {
        if (_pane is null || e.Data.GetData(TabDragFormat) is not TabDrag drag || sender is not FrameworkElement { Tag: TabBase target })
        {
            return;
        }

        var to = _pane.Tabs.IndexOf(target);
        if (drag.From == _pane)
        {
            _pane.MoveTab(_pane.Tabs.IndexOf(drag.Tab), to);
        }
        else if (drag.From.DetachTab(drag.Tab))
        {
            _pane.AddTab(drag.Tab, activate: true, index: to);
        }

        e.Handled = true;
    }

    private sealed record TabDrag(TabBase Tab, PaneViewModel From);

    private void OnTabWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabBase tab })
        {
            _pane?.CloseTab(tab);
        }
    }

    private void OnNewTab(object sender, RoutedEventArgs e) => NewTab();

    public void NewTab()
    {
        var path = _pane?.ActiveFolder?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _pane?.NewFolderTab(path);
    }

    // ───────────── 버튼 ─────────────

    private void OnBack(object sender, RoutedEventArgs e) => _ = _tab?.GoBackAsync();

    private void OnForward(object sender, RoutedEventArgs e) => _ = _tab?.GoForwardAsync();

    private void OnUp(object sender, RoutedEventArgs e) => _ = _tab?.GoUpAsync();

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = _tab?.RefreshAsync();

    private void OnDriveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DriveItem d })
        {
            if (_tab is null)
            {
                _pane?.NewFolderTab(d.Root);
            }
            else
            {
                _ = _tab.NavigateAsync(d.Root);
            }
        }
    }

    private double _treeWidth = 200;

    private void OnToggleTree(object sender, RoutedEventArgs e) => ToggleTree();

    /// <summary>Ctrl+B·트리 버튼: 사용자가 직접 켜고 끈다 (그 뒤로는 좁은 창 자동 접기를 하지 않는다).</summary>
    public void ToggleTree()
    {
        _treeManual = true;
        TreeVisible = !TreeVisible;
    }


    /// <summary>트리 버튼은 켜고 끄기를 한 자리에서: 켜져 있으면 눌린 모양, 툴팁은 누르면 일어날 일.</summary>
    private void UpdateTreeButton()
    {
        var on = TreeHost.Visibility == Visibility.Visible;
        if (on)
        {
            TreeBtn.SetResourceReference(BackgroundProperty, "Brush.Selection");
        }
        else
        {
            TreeBtn.ClearValue(BackgroundProperty);
        }

        TreeBtn.ToolTip = on ? Foldy.Core.Localization.Loc.T("pane.treeHide") : Foldy.Core.Localization.Loc.T("pane.treeShow");
    }

    // 좁은 창에서 트리를 저절로 숨겼는지 (넓어지면 다시 보인다). 사용자가 직접 켜고 끄면 그 뒤로는 손대지 않는다.
    private bool _treeAutoHidden;
    private bool _treeManual;

    /// <summary>폴더 트리 보이기 (Ctrl+B). 숨겼다 다시 켜면 직전 너비로 돌아온다.</summary>
    public bool TreeVisible
    {
        get => TreeHost.Visibility == Visibility.Visible;
        set
        {
            if (value == TreeVisible)
            {
                return;
            }

            if (!value && TreeColumn.Width.Value >= 1)
            {
                _treeWidth = TreeColumn.Width.Value;
            }

            TreeColumn.Width = new GridLength(value ? _treeWidth : 0);
            TreeHost.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Splitter.Visibility = TreeHost.Visibility;
            UpdateTreeButton();
            if (value)
            {
                _ = Dispatcher.BeginInvoke(() => _ = RevealInTreeAsync(_tab?.Path), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
    }

    /// <summary>트리 너비 (세션 저장용). 0이면 숨김.</summary>
    public double TreeWidth
    {
        get => TreeVisible ? TreeColumn.Width.Value : 0;
        set
        {
            if (double.IsNaN(value) || value < 1)
            {
                TreeVisible = false;
                return;
            }

            _treeWidth = Math.Clamp(value, 120, 520);
            if (TreeVisible)
            {
                TreeColumn.Width = new GridLength(_treeWidth);
            }
            else
            {
                TreeVisible = true;
            }
        }
    }

    /// <summary>
    /// 트리가 지금 폴더를 따라가게: 위 폴더를 펼치고 그 폴더를 강조해 보이게 스크롤한다 (양쪽 패널 각자, 주소 입력·Ctrl+Space·탭·드라이브 바꾸기 모두).
    /// 트리가 접혀 있으면 하지 않는다(보일 때 다시). 폴더 목록은 비동기라 느린 드라이브도 화면을 막지 않는다.
    /// </summary>
    /// <summary>
    /// 트리가 지금 폴더를 따라 펼쳐지나. 문서용 화면 캡처는 끈다: 예시 폴더가 실제 C: 아래에 있어,
    /// 펼치면 그 PC의 다른 폴더 이름이 그림에 찍힌다 (2026-11-22, 공개 그림에 실제 폴더 이름이 나간 일).
    /// </summary>
    public static bool FollowCurrentFolderInTree { get; set; } = true;

    private async Task RevealInTreeAsync(string? path)
    {
        if (_tree is null || TreeColumnHidden() || !FollowCurrentFolderInTree)
        {
            return;
        }

        IReadOnlyList<FolderNode> chain;
        try
        {
            chain = await _tree.RevealAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return;
        }

        if (chain.Count == 0 || !Foldy.Presentation.Tree.FolderTreeViewModel.Same(chain[^1].Path, _tab?.Path ?? ""))
        {
            return;
        }

        // 가상화된 트리: 단계마다 그 항목을 만들어 가며 내려가서 마지막 것을 보이게
        _revealing = true;
        try
        {
            ItemsControl parent = Tree;
            TreeViewItem? item = null;
            foreach (var node in chain)
            {
                parent.UpdateLayout();
                item = parent.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                if (item is null && FindPanel(parent) is { } panel && parent.Items.IndexOf(node) is var i and >= 0)
                {
                    panel.BringIndexIntoViewPublic(i);
                    parent.UpdateLayout();
                    item = parent.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                }

                if (item is null)
                {
                    return;
                }

                if (!ReferenceEquals(node, chain[^1]))
                {
                    item.IsExpanded = true;
                    item.ApplyTemplate();
                }

                parent = item;
            }

            if (item is not null)
            {
                item.BringIntoView();
                await Dispatcher.InvokeAsync(item.BringIntoView, System.Windows.Threading.DispatcherPriority.Loaded); // 펼친 뒤 자리가 잡히면 한 번 더
            }
        }
        finally
        {
            _revealing = false;
        }
    }

    private bool _revealing;

    private bool TreeColumnHidden() => TreeHost.Visibility != Visibility.Visible;

    private static VirtualizingStackPanel? FindPanel(DependencyObject root)
    {
        if (root is VirtualizingStackPanel v)
        {
            return v;
        }

        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TreeViewItem)
            {
                continue; // 그 아래 단계의 패널은 건너뛴다
            }

            if (FindPanel(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode n && !string.IsNullOrEmpty(n.Path))
        {
            if (_revealing || Foldy.Presentation.Tree.FolderTreeViewModel.Same(n.Path, _tab?.Path ?? ""))
            {
                return; // 지금 폴더를 따라 강조한 것: 다시 이동하지 않는다
            }

            if (_tab is null)
            {
                _pane?.NewFolderTab(n.Path);
            }
            else
            {
                _ = _tab.NavigateAsync(n.Path);
            }
        }
    }

    private void OnCmd(object sender, RoutedEventArgs e) =>
        _host?.Shell.OpenTerminal(TerminalKind.Cmd, elevated: (Keyboard.Modifiers & ModifierKeys.Control) != 0);

    private void OnPowerShell(object sender, RoutedEventArgs e) =>
        _host?.Shell.OpenTerminal(TerminalKind.PowerShell, elevated: (Keyboard.Modifiers & ModifierKeys.Control) != 0);

    private void OnSsh(object sender, RoutedEventArgs e) => _host?.Shell.OpenTerminal(TerminalKind.Default, elevated: false);


    // ───────────── Git 뷰 ─────────────

    private void OnGitChipClick(object sender, MouseButtonEventArgs e)
    {
        if (_tab?.Git is { } g && _host is not null && _pane is not null)
        {
            _host.OpenGit(_pane, g.RepoRoot);
        }
    }

    // ───────────── 이미지만 보기 ─────────────

    private Foldy.UI.Imaging.ImageGridView? _grid;

    private void OnToggleImages(object sender, RoutedEventArgs e) => ToggleImages();

    /// <summary>지금 "이미지만 보기"가 켜져 있는지 (메뉴의 체크 표시).</summary>
    public bool ImagesOn => _tab?.ImagesOnly == true;

    public void ToggleImages()
    {
        if (_tab is null)
        {
            return;
        }

        _tab.ImagesOnly = !_tab.ImagesOnly;
        UpdateImageLayer();
    }

    private void UpdateImageLayer()
    {
        var on = _tab?.ImagesOnly == true;
        ImagesBtn.Opacity = on ? 1 : 0.75;
        if (on)
        {
            ImagesBtn.SetResourceReference(BackgroundProperty, "Brush.Selection");
        }
        else
        {
            ImagesBtn.ClearValue(BackgroundProperty);
        }

        if (!on || _tab is null)
        {
            ImageLayer.Visibility = Visibility.Collapsed;
            ImagesBar.Visibility = Visibility.Collapsed;
            Scroller.Visibility = Visibility.Visible;
            return;
        }

        if (_grid is null)
        {
            _grid = new Foldy.UI.Imaging.ImageGridView();
            _grid.ItemActivated += (_, i) => OpenImage(i);
            _grid.QuickLookRequested += (_, i) => QuickLook(i);
            _grid.SelectionChanged += (_, _) => SyncImageSelection();
            ImageLayer.Content = _grid;
        }

        var images = new ItemList(_tab.Items.Where(i => !i.IsFolder && Foldy.Platform.Imaging.ImageFormats.IsImage(i.Name)));
        _grid.AllowContentDecode = !(_tab.Path.StartsWith(@"\\", StringComparison.Ordinal) || Foldy.Presentation.Navigation.PathUtil.IsSftp(_tab.Path)
            || Foldy.Platform.SystemInfo.CloudVolumes.IsCloudPath(_tab.Path));
        _grid.ItemsSource = images;
        Scroller.Visibility = Visibility.Collapsed;
        ImageLayer.Visibility = Visibility.Visible;
        ImagesBar.Visibility = Visibility.Visible;
        var others = _tab.Items.Count - images.Count;
        ImagesBarText.Text = images.Count == 0
            ? Foldy.Core.Localization.Loc.F("pane.imgNone", others)
            : Foldy.Core.Localization.Loc.F("pane.imgOnly", images.Count, others);
        EmptyPanel.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Visible;
        LoadingText.Text = Foldy.Core.Localization.Loc.F("pane.imgStatus", images.Count);
    }

    private ItemList? ImageItems => _grid?.ItemsSource as ItemList;

    private void SyncImageSelection()
    {
        if (_tab is null || _grid is null || ImageItems is not { } items)
        {
            return;
        }

        _tab.Selection = [.. _grid.SelectedIndices().Where(i => i < items.Count).Select(i => items[i])];
        SelectionInfoChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenImage(int index)
    {
        if (ImageItems is { } items && index >= 0 && index < items.Count && _host is not null && _pane is not null)
        {
            _ = _host.Shell.OpenAsync(_pane, items[index]);
        }
    }

    private void QuickLook(int index)
    {
        if (ImageItems is not { } items || _grid is null)
        {
            return;
        }

        var ql = new Foldy.UI.Imaging.QuickLookWindow(items, index)
        {
            Owner = Window.GetWindow(this),
            Loader = _grid.Loader,
            AllowContentDecode = _grid.AllowContentDecode,
        };
        ql.CurrentIndexChanged += (_, i) => _grid.SelectIndex(i);
        ql.Show();
    }

    private void UpdateTerminalButtons(string? path)
    {
        var sftp = Foldy.Presentation.Navigation.PathUtil.IsSftp(path);
        CmdBtn.Visibility = PsBtn.Visibility = AdminBtn.Visibility = sftp ? Visibility.Collapsed : Visibility.Visible;
        SshBtn.Visibility = sftp ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAdminShell(object sender, RoutedEventArgs e) =>
        _host?.Shell.OpenTerminal(TerminalKind.PowerShell, elevated: true);

    /// <summary>선택 정보 문자열 (상태바).</summary>
    public string SelectionSummary()
    {
        if (_tab is null)
        {
            return "";
        }

        var n = _tab.Items.Count;
        var sel = _tab.Selection;
        if (sel.Count == 0)
        {
            return Foldy.Core.Localization.Loc.F("pane.items", n);
        }

        var bytes = sel.Where(i => !i.IsFolder).Sum(i => i.Entry.Size);
        return Foldy.Core.Localization.Loc.F("pane.itemsSel", n, sel.Count, FileItem.FormatBytes(bytes));
    }

}
