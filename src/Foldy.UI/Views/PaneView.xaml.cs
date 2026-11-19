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
        _folderColumns.AddRange([
            // 좁은 패널: 유형은 이름 왼쪽 아이콘으로만 보이고, 남는 폭은 모두 이름에 준다.
            new FastColumn("이름", 320, o => ((FileItem)o).Name),
            new FastColumn("크기", 72, o => ((FileItem)o).SizeText, alignRight: true),
            new FastColumn("수정", 68, o => ((FileItem)o).ShortModifiedText) { FontScale = 0.85 },
        ]);
        _searchColumns.AddRange([
            new FastColumn("이름", 240, o => ((FileItem)o).Name),
            new FastColumn("위치", 220, o => Path.GetDirectoryName(((FileItem)o).FullPath) ?? ""),
            new FastColumn("크기", 72, o => ((FileItem)o).SizeText, alignRight: true),
            new FastColumn("수정", 68, o => ((FileItem)o).ShortModifiedText) { FontScale = 0.85 },
        ]);
        UseColumns(_folderColumns);
        List.SortColumn = 0;
        List.IsDimmed = o => o is FileItem f && (f.IsHidden || _tab?.Git?.BadgeOf(f.FullPath) == Foldy.Core.FileSystem.GitFileStatus.Ignored);
        List.BadgeSelector = GitBadge;
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

        _tab = tab as FolderTabViewModel;
        _search = tab as SearchTabViewModel;
        UpdateTabVisuals();

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
        LoadingText.Text = _search.Status + (_search.IsBusy ? " · Esc 취소" : "");
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
                if (_tab.ImagesOnly)
                {
                    UpdateImageLayer();
                }

                break;
            case nameof(FolderTabViewModel.Path):
                UpdateNav();
                break;
            case nameof(FolderTabViewModel.Git):
                UpdateGit();
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

        var (text, key) = g.BadgeOf(f.FullPath) switch
        {
            Foldy.Core.FileSystem.GitFileStatus.Modified => ("M", "Brush.Progress"),
            Foldy.Core.FileSystem.GitFileStatus.Added => ("A", "Brush.Done"),
            Foldy.Core.FileSystem.GitFileStatus.Untracked => ("?", "Brush.TextSecondary"),
            Foldy.Core.FileSystem.GitFileStatus.Conflicted => ("!", "Brush.Error"),
            _ => (null, null),
        };
        return text is not null && TryFindResource(key) is Brush b ? (text, b) : null;
    }

    private void UpdateGit()
    {
        var g = _tab?.Git;
        GitChip.Visibility = g is null ? Visibility.Collapsed : Visibility.Visible;
        GitText.Text = g is null ? "" : g.Changes > 0 ? $"{g.BranchText} · 변경 {g.Changes}" : g.BranchText;
        GitChip.ToolTip = g?.RepoRoot;
        List.Redraw();
    }

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
            LoadingText.Text = $"불러오는 중… {_tab.LoadedCount:N0}개 · {ms / 1000.0:0}초 · Esc 취소";
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
            PreviewSplitColumn.Width = new GridLength(0);
            PreviewColumn.Width = new GridLength(0);
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
        PreviewSplitColumn.Width = new GridLength(6);
        PreviewColumn.Width = new GridLength(Math.Max(260, ActualWidth * 0.38));
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
        var verb = _host.ShellMenu.Show(
            _host.OwnerHandle, paths, (int)e.ScreenPoint.X, (int)e.ScreenPoint.Y, extended,
            backgroundFolder: paths.Length == 0 ? _tab!.Path : null);
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
            _host!.Shell.Status = move ? $"{selection.Count:N0}개 잘라 둠" : $"{selection.Count:N0}개 복사해 둠";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _host!.Shell.Status = "클립보드를 쓸 수 없습니다: " + ex.Message;
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
            _host.Shell.Status = "클립보드를 읽을 수 없습니다: " + ex.Message;
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

    private void OnToggleTree(object sender, RoutedEventArgs e) => TreeVisible = !TreeVisible;

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

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode n && !string.IsNullOrEmpty(n.Path))
        {
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

    private void OnSudo(object sender, RoutedEventArgs e) => _host?.Shell.OpenTerminal(TerminalKind.Default, elevated: true);

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
        _grid.AllowContentDecode = !(_tab.Path.StartsWith(@"\\", StringComparison.Ordinal) || Foldy.Presentation.Navigation.PathUtil.IsSftp(_tab.Path));
        _grid.ItemsSource = images;
        Scroller.Visibility = Visibility.Collapsed;
        ImageLayer.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Visible;
        LoadingText.Text = $"이미지 {images.Count:N0}개 · 스페이스: 빠른 보기 · Ctrl+휠: 크기";
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
        SshBtn.Visibility = SudoBtn.Visibility = sftp ? Visibility.Visible : Visibility.Collapsed;
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
            return $"{n:N0}개 항목";
        }

        var bytes = sel.Where(i => !i.IsFolder).Sum(i => i.Entry.Size);
        return $"{n:N0}개 항목 · {sel.Count:N0}개 선택 · {FileItem.FormatBytes(bytes)}";
    }

}
