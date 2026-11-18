using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Foldy.Presentation.Tabs;
using Foldy.UI.Theming;

namespace Foldy.UI.Markdown;

/// <summary>문서 안의 폴더 링크를 눌렀을 때. 호스트(패널)가 Handled를 켜고 폴더 탭을 연다.</summary>
public sealed class MarkdownFolderLinkEventArgs(string path) : EventArgs
{
    public string Path { get; } = path;

    public bool Handled { get; set; }
}

/// <summary>
/// 내장 Markdown 뷰어 화면 (설계서 5.9). DataContext = MarkdownTabViewModel.
/// 파일 읽기·파싱은 백그라운드, FlowDocument 구성은 UI 스레드에서 (1MB가 넘으면 블록을 나눠 이어 붙임).
/// 파일이 바뀌면 스크롤 위치를 지킨 채 다시 읽는다.
/// </summary>
public partial class MarkdownView : UserControl
{
    public static readonly DependencyProperty IsOutlineVisibleProperty = DependencyProperty.Register(
        nameof(IsOutlineVisible), typeof(bool), typeof(MarkdownView), new PropertyMetadata(false));

    public static readonly DependencyProperty FolderLinkCommandProperty = DependencyProperty.Register(
        nameof(FolderLinkCommand), typeof(ICommand), typeof(MarkdownView));

    /// <summary>목차 Margin 변환 (MarkdownHeading.Indent → 왼쪽 여백).</summary>
    public static readonly IValueConverter IndentConverter = new IndentToThickness();

    private const int BatchBlocks = 200;
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _reloadTimer;
    private MarkdownTabViewModel? _vm;
    private CancellationTokenSource? _loadCts;
    private FileSystemWatcher? _watcher;
    private MarkdownFlowRenderer? _renderer;
    private string _text = "";
    private string? _loadedPath;
    private bool _allowRemoteImages;
    private string? _pendingFragment;
    private ScrollViewer? _scroll;
    private bool _attached;

    // 찾기 상태
    private FindIndex? _findIndex;
    private List<int> _matches = [];
    private int _matchIndex = -1;

    public MarkdownView()
    {
        InitializeComponent();
        _reloadTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = ReloadDelay };
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            _ = LoadAsync(keepScroll: true);
        };

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => RenderVisibleMermaid()));
    }

    /// <summary>폴더 링크. 처리하지 않으면(Handled=false) FolderLinkCommand를 부른다.</summary>
    public event EventHandler<MarkdownFolderLinkEventArgs>? FolderLinkRequested;

    /// <summary>앱 전체에서 쓸 Mermaid 렌더러 (WebView2 구현체를 앱이 등록). 없으면 코드로만 보인다.</summary>
    public static IMermaidRenderer? DefaultMermaidRenderer { get; set; }

    /// <summary>이 뷰에서 쓸 Mermaid 렌더러. 기본은 DefaultMermaidRenderer.</summary>
    public IMermaidRenderer? MermaidRenderer { get; set; } = DefaultMermaidRenderer;

    public bool IsOutlineVisible
    {
        get => (bool)GetValue(IsOutlineVisibleProperty);
        set => SetValue(IsOutlineVisibleProperty, value);
    }

    /// <summary>폴더 링크 명령 (매개변수: 폴더 전체 경로 string).</summary>
    public ICommand? FolderLinkCommand
    {
        get => (ICommand?)GetValue(FolderLinkCommandProperty);
        set => SetValue(FolderLinkCommandProperty, value);
    }

    /// <summary>현재 문서의 목차.</summary>
    public IReadOnlyList<MarkdownHeading> Headings { get; private set; } = [];

    /// <summary>마지막으로 판별한 인코딩 이름 (상태 표시).</summary>
    public string? EncodingName { get; private set; }

    /// <summary>문서 구성이 끝날 때마다 (테스트·호스트용).</summary>
    public event EventHandler? DocumentLoaded;

    // ───────────────────────────── 수명 ─────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = DataContext as MarkdownTabViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            if (_attached)
            {
                _allowRemoteImages = false;
                _ = LoadAsync(keepScroll: false);
                ApplyShowSource();
            }
        }
        else
        {
            StopWatching();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        ThemeManager.Changed += OnThemeChanged;
        if (_vm is not null)
        {
            _ = LoadAsync(keepScroll: _loadedPath == _vm.FilePath);
            ApplyShowSource();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 탭을 숨기면 감시를 멈춘다. 다시 보이면 OnLoaded에서 다시 읽는다.
        _attached = false;
        ThemeManager.Changed -= OnThemeChanged;
        _loadCts?.Cancel();
        _reloadTimer.Stop();
        StopWatching();
    }

    private void OnThemeChanged()
    {
        if (Viewer.Document is { } doc)
        {
            MarkdownPalette.Apply(doc.Resources, ThemeManager.IsDark);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MarkdownTabViewModel.FilePath):
                _allowRemoteImages = false;
                _ = LoadAsync(keepScroll: false);
                break;
            case nameof(MarkdownTabViewModel.ShowSource):
                ApplyShowSource();
                break;
        }
    }

    // ───────────────────────────── 읽기와 구성 ─────────────────────────────

    /// <summary>현재 FilePath를 읽어 그린다. keepScroll이면 스크롤 위치를 지킨다 (자동 다시 읽기).</summary>
    public async Task LoadAsync(bool keepScroll)
    {
        if (_vm is null)
        {
            return;
        }

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;
        var path = _vm.FilePath;
        var folder = _vm.Folder;
        var offset = keepScroll ? CurrentOffset() : 0;
        var fragment = _pendingFragment;
        _pendingFragment = null;

        if (!string.Equals(_watcher?.Path is { } wp ? Path.Combine(wp, _watcher.Filter) : null, path, StringComparison.OrdinalIgnoreCase))
        {
            StartWatching(path);
        }

        StatusText.Text = "불러오는 중…";
        string text;
        Markdig.Syntax.MarkdownDocument ast;
        IReadOnlyList<MarkdownHeading> headings;
        try
        {
            (text, ast, headings, var encoding) = await Task.Run(async () =>
            {
                var (t, enc) = await MarkdownText.ReadFileAsync(path, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var doc = MarkdownParsing.Parse(t);
                return (t, doc, MarkdownOutline.Extract(doc), enc);
            }, ct);
            EncodingName = encoding.CodePage == 949 ? "CP949" : encoding.WebName.ToUpperInvariant();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!ct.IsCancellationRequested)
            {
                ShowError(path, e.Message);
            }

            return;
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        _text = text;
        _loadedPath = path;
        _findIndex = null;
        Headings = headings;
        OutlineList.ItemsSource = headings;
        if (_vm.ShowSource)
        {
            SetSourceText(keepScroll);
        }

        var renderer = new MarkdownFlowRenderer(new MarkdownRenderOptions
        {
            BaseFolder = folder,
            AllowRemoteImages = _allowRemoteImages,
            LinkClicked = OnLinkClicked,
            Mermaid = MermaidRenderer,
            Dark = ThemeManager.IsDark,
        }, headings);
        _renderer = renderer;
        var document = MarkdownFlowRenderer.CreateDocument(ThemeManager.IsDark);
        var large = text.Length > MarkdownText.LargeFileThreshold;
        var count = 0;
        var shown = false;
        foreach (var block in renderer.RenderBlocks(ast))
        {
            document.Blocks.Add(block);
            if (large && ++count % BatchBlocks == 0)
            {
                // 큰 문서: 앞부분을 먼저 보여 주고 나머지는 입력을 막지 않게 조금씩 이어 붙인다.
                if (!shown)
                {
                    Viewer.Document = document;
                    shown = true;
                    if (!keepScroll && fragment is null)
                    {
                        StatusText.Text = "나머지 불러오는 중…";
                    }
                }

                await Dispatcher.Yield(DispatcherPriority.Background);
                if (ct.IsCancellationRequested || _renderer != renderer)
                {
                    return;
                }
            }
        }

        if (!shown)
        {
            Viewer.Document = document;
        }

        ImageBanner.Visibility = renderer.HasBlockedImages ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = EncodingName;

        // 레이아웃이 끝난 뒤 위치 복원 또는 앵커 이동
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (fragment is not null)
        {
            ScrollToAnchor(fragment);
        }
        else
        {
            GetScrollViewer()?.ScrollToVerticalOffset(offset);
        }

        RefreshFind();
        RenderVisibleMermaid();
        DocumentLoaded?.Invoke(this, EventArgs.Empty);
    }

    private void ShowError(string path, string message)
    {
        _renderer = null;
        _text = "";
        _findIndex = null;
        Headings = [];
        OutlineList.ItemsSource = null;
        var doc = MarkdownFlowRenderer.CreateDocument(ThemeManager.IsDark);
        var p = new Paragraph(new Run($"파일을 열 수 없습니다: {path}\n{message}"));
        p.SetResourceReference(TextElement.ForegroundProperty, "Brush.Error");
        doc.Blocks.Add(p);
        Viewer.Document = doc;
        ImageBanner.Visibility = Visibility.Collapsed;
        StatusText.Text = "열기 실패";
    }

    private void ApplyShowSource()
    {
        var source = _vm?.ShowSource == true;
        if (source)
        {
            SetSourceText(keepScroll: false);
        }

        SourceBox.Visibility = source ? Visibility.Visible : Visibility.Collapsed;
        Viewer.Visibility = source ? Visibility.Collapsed : Visibility.Visible;
        _matches = [];
        RefreshFind();
    }

    private void SetSourceText(bool keepScroll)
    {
        if (ReferenceEquals(SourceBox.Text, _text))
        {
            return;
        }

        var offset = SourceBox.VerticalOffset;
        SourceBox.Text = _text;
        if (keepScroll)
        {
            SourceBox.ScrollToVerticalOffset(offset);
        }
    }

    private double CurrentOffset() =>
        _vm?.ShowSource == true ? SourceBox.VerticalOffset : GetScrollViewer()?.VerticalOffset ?? 0;

    // ───────────────────────────── 파일 감시 ─────────────────────────────

    private void StartWatching(string path)
    {
        StopWatching();
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }

        try
        {
            var w = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            w.Changed += OnFileChanged;
            w.Created += OnFileChanged;
            w.Renamed += OnFileChanged;
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // 네트워크 드라이브 등 감시할 수 없는 위치: 자동 다시 읽기만 빠진다.
        }
    }

    private void StopWatching()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileChanged;
        _watcher.Created -= OnFileChanged;
        _watcher.Renamed -= OnFileChanged;
        _watcher.Dispose();
        _watcher = null;
    }

    // 감시 스레드에서 온다. 편집기는 저장 한 번에 이벤트를 여러 번 내므로 400ms 모아서 한 번 다시 읽는다.
    private void OnFileChanged(object sender, FileSystemEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });

    // ───────────────────────────── 툴바 ─────────────────────────────

    private void OnBack(object sender, RoutedEventArgs e) => _vm?.GoBack();

    private void OnForward(object sender, RoutedEventArgs e) => _vm?.GoForward();

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var path = _vm.FilePath;
        try
        {
            // 연결된 "편집" 동사 (VS Code, 메모장++ 등이 등록)
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "edit" });
        }
        catch (Win32Exception)
        {
            try
            {
                var psi = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
                psi.ArgumentList.Add(path);
                using var _ = Process.Start(psi);
            }
            catch (Win32Exception ex)
            {
                StatusText.Text = "편집기를 열지 못했습니다: " + ex.Message;
            }
        }
    }

    private void OnAllowRemoteImages(object sender, RoutedEventArgs e)
    {
        _allowRemoteImages = true;
        ImageBanner.Visibility = Visibility.Collapsed;
        _ = LoadAsync(keepScroll: true);
    }

    private void OnOutlineSelected(object sender, SelectionChangedEventArgs e)
    {
        if (OutlineList.SelectedItem is MarkdownHeading h)
        {
            if (_vm?.ShowSource == true)
            {
                var idx = SourceBox.GetCharacterIndexFromLineIndex(Math.Min(h.Line, Math.Max(0, SourceBox.LineCount - 1)));
                if (idx >= 0)
                {
                    SourceBox.ScrollToLine(SourceBox.GetLineIndexFromCharacterIndex(idx));
                }
            }
            else
            {
                ScrollToAnchor(h.Anchor);
            }
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F && mods == ModifierKeys.Control)
        {
            ShowFind();
            e.Handled = true;
        }
        else if (key == Key.F3)
        {
            FindStep(mods == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
        }
        else if (key == Key.Left && mods == ModifierKeys.Alt)
        {
            _vm?.GoBack();
            e.Handled = true;
        }
        else if (key == Key.Right && mods == ModifierKeys.Alt)
        {
            _vm?.GoForward();
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    // ───────────────────────────── 링크 ─────────────────────────────

    private void OnLinkClicked(string url)
    {
        if (_vm is null)
        {
            return;
        }

        var target = MarkdownLinks.Classify(url, _vm.Folder);
        switch (target.Kind)
        {
            case MarkdownLinkKind.Anchor:
                ScrollToAnchor(target.Fragment ?? "");
                break;
            case MarkdownLinkKind.Markdown:
                if (string.Equals(target.Path, _vm.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (target.Fragment is not null)
                    {
                        ScrollToAnchor(target.Fragment);
                    }
                }
                else if (File.Exists(target.Path))
                {
                    _pendingFragment = target.Fragment;
                    _vm.OpenLinked(target.Path);
                }
                else
                {
                    StatusText.Text = "파일이 없습니다: " + target.Path;
                }

                break;
            case MarkdownLinkKind.Folder:
                RequestFolder(target.Path);
                break;
            case MarkdownLinkKind.File:
                if (!File.Exists(target.Path))
                {
                    StatusText.Text = "파일이 없습니다: " + target.Path;
                }
                else if (!MarkdownLinks.IsExecutable(target.Path) || ConfirmExecutable(target.Path))
                {
                    ShellOpen(target.Path);
                }

                break;
            case MarkdownLinkKind.Web:
                ShellOpen(target.Path);
                break;
            default:
                StatusText.Text = "열 수 없는 링크: " + url;
                break;
        }
    }

    private void RequestFolder(string path)
    {
        var args = new MarkdownFolderLinkEventArgs(path);
        FolderLinkRequested?.Invoke(this, args);
        if (!args.Handled && FolderLinkCommand is { } cmd && cmd.CanExecute(path))
        {
            cmd.Execute(path);
        }
    }

    private bool ConfirmExecutable(string path) =>
        MessageBox.Show(Window.GetWindow(this)!,
            $"문서의 링크가 실행 파일을 가리킵니다.\n\n{path}\n\n실행할까요?",
            "Foldy", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void ShellOpen(string target)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Win32Exception e)
        {
            StatusText.Text = "열지 못했습니다: " + e.Message;
        }
    }

    // ───────────────────────────── 스크롤 ─────────────────────────────

    /// <summary>앵커(제목 슬러그, fn-1 등)로 이동. 찾으면 true.</summary>
    public bool ScrollToAnchor(string anchor)
    {
        if (_renderer is null)
        {
            return false;
        }

        if (!_renderer.Anchors.TryGetValue(anchor, out var element) &&
            !_renderer.Anchors.TryGetValue(HeadingSlugger.Slugify(anchor), out element))
        {
            return false;
        }

        ScrollToPosition(element.ContentStart, topMargin: 8);
        return true;
    }

    /// <summary>위치가 보이는 영역 위쪽(topMargin)에 오도록 스크롤.</summary>
    private void ScrollToPosition(TextPointer position, double topMargin)
    {
        var sv = GetScrollViewer();
        if (sv is null)
        {
            (position.Parent as FrameworkContentElement)?.BringIntoView();
            return;
        }

        sv.UpdateLayout();
        var rect = position.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty)
        {
            (position.Parent as FrameworkContentElement)?.BringIntoView();
            return;
        }

        // GetCharacterRect는 문서 뷰(보이는 영역) 좌표 → 현재 오프셋에 더한다.
        sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + rect.Top - topMargin));
    }

    private ScrollViewer? GetScrollViewer()
    {
        if (_scroll is null)
        {
            Viewer.ApplyTemplate();
            _scroll = FindChild<ScrollViewer>(Viewer);
        }

        return _scroll;
    }

    private static T? FindChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t)
            {
                return t;
            }

            if (FindChild<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // ───────────────────────────── Mermaid (지연) ─────────────────────────────

    /// <summary>화면에 들어온 mermaid 블록만 그린다 (한 번씩).</summary>
    private void RenderVisibleMermaid()
    {
        if (_renderer is null || MermaidRenderer is not { } mermaid || _renderer.MermaidBlocks.Count == 0 || !Viewer.IsVisible)
        {
            return;
        }

        foreach (var block in _renderer.MermaidBlocks)
        {
            if (block.Attempted || !block.Anchor.IsVisible)
            {
                continue;
            }

            Point pt;
            try
            {
                pt = block.Anchor.TranslatePoint(default, Viewer);
            }
            catch (InvalidOperationException)
            {
                continue; // 아직 화면 트리에 붙지 않음
            }

            if (pt.Y >= -block.Anchor.ActualHeight && pt.Y <= Viewer.ActualHeight)
            {
                _ = block.RenderAsync(mermaid, ThemeManager.IsDark);
            }
        }
    }

    // ───────────────────────────── 찾기 (Ctrl+F) ─────────────────────────────

    private void OnShowFind(object sender, RoutedEventArgs e) => ShowFind();

    private void OnCloseFind(object sender, RoutedEventArgs e) => CloseFind();

    private void OnFindNext(object sender, RoutedEventArgs e) => FindStep(1);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => FindStep(-1);

    private void ShowFind()
    {
        FindBar.Visibility = Visibility.Visible;
        if (Viewer.Selection is { IsEmpty: false } sel && sel.Text.Length < 100 && !sel.Text.Contains('\n', StringComparison.Ordinal))
        {
            FindBox.Text = sel.Text;
        }

        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        if (_vm?.ShowSource == true)
        {
            SourceBox.Focus();
        }
        else
        {
            Viewer.Focus();
        }
    }

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindStep(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshFind();
        FindStep(1, fromStart: true);
    }

    /// <summary>검색어나 문서가 바뀌면 일치 목록을 다시 만든다.</summary>
    private void RefreshFind()
    {
        _matches = [];
        _matchIndex = -1;
        var query = FindBox.Text;
        if (FindBar.Visibility != Visibility.Visible || string.IsNullOrEmpty(query))
        {
            FindCount.Text = "";
            return;
        }

        var haystack = _vm?.ShowSource == true ? _text : (_findIndex ??= FindIndex.Build(Viewer.Document)).Text;
        for (var i = haystack.IndexOf(query, StringComparison.CurrentCultureIgnoreCase); i >= 0;
             i = haystack.IndexOf(query, i + Math.Max(1, query.Length), StringComparison.CurrentCultureIgnoreCase))
        {
            _matches.Add(i);
        }

        FindCount.Text = _matches.Count == 0 ? "결과 없음" : $"0/{_matches.Count}";
    }

    private void FindStep(int direction, bool fromStart = false)
    {
        if (_matches.Count == 0)
        {
            if (FindBar.Visibility != Visibility.Visible)
            {
                ShowFind();
            }

            return;
        }

        _matchIndex = fromStart ? 0 : (_matchIndex + direction + _matches.Count) % _matches.Count;
        FindCount.Text = $"{_matchIndex + 1}/{_matches.Count}";
        var start = _matches[_matchIndex];
        var length = FindBox.Text.Length;
        if (_vm?.ShowSource == true)
        {
            SourceBox.Select(start, length);
            SourceBox.ScrollToLine(SourceBox.GetLineIndexFromCharacterIndex(start));
            return;
        }

        if (_findIndex?.Range(start, length) is var (a, b) && Viewer.Selection is { } selection)
        {
            selection.Select(a, b);
            var sv = GetScrollViewer();
            ScrollToPosition(a, topMargin: (sv?.ViewportHeight ?? 300) / 3);
        }
    }

    /// <summary>FlowDocument 글자 전체를 한 문자열로 이어 두고, 문자열 위치 ↔ TextPointer를 바꾼다.</summary>
    internal sealed class FindIndex
    {
        private readonly List<(int Start, int Length, TextPointer Pointer)> _segments = [];

        public string Text { get; private set; } = "";

        public static FindIndex Build(FlowDocument? document)
        {
            var index = new FindIndex();
            if (document is null)
            {
                return index;
            }

            var sb = new StringBuilder();
            for (var p = document.ContentStart; p is not null; p = p.GetNextContextPosition(LogicalDirection.Forward))
            {
                switch (p.GetPointerContext(LogicalDirection.Forward))
                {
                    case TextPointerContext.Text:
                        var run = p.GetTextInRun(LogicalDirection.Forward);
                        index._segments.Add((sb.Length, run.Length, p));
                        sb.Append(run);
                        break;
                    case TextPointerContext.ElementEnd when p.Parent is Block or LineBreak:
                        sb.Append('\n'); // 문단을 넘는 일치를 막는다
                        break;
                    case TextPointerContext.ElementStart when p.GetAdjacentElement(LogicalDirection.Forward) is LineBreak:
                        sb.Append('\n');
                        break;
                }
            }

            index.Text = sb.ToString();
            return index;
        }

        public (TextPointer Start, TextPointer End)? Range(int start, int length)
        {
            var a = PointerAt(start, isEnd: false);
            var b = PointerAt(start + length, isEnd: true);
            return a is null || b is null ? null : (a, b);
        }

        private TextPointer? PointerAt(int offset, bool isEnd)
        {
            // 끝 위치는 마지막 글자가 속한 조각 기준
            var probe = isEnd ? offset - 1 : offset;
            int lo = 0, hi = _segments.Count - 1, found = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (_segments[mid].Start <= probe)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (found < 0)
            {
                return null;
            }

            var seg = _segments[found];
            var delta = Math.Clamp(offset - seg.Start, 0, seg.Length);
            return seg.Pointer.GetPositionAtOffset(delta);
        }
    }

    private sealed class IndentToThickness : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            new Thickness(value is double d ? d : 0, 1, 4, 1);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
