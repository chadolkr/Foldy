using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Foldy.Copy.Native;
using Foldy.Core.Diagnostics;
using Foldy.Core.FileSystem;
using Foldy.Core.Threading;
using Foldy.Indexer.Client;
using Foldy.Providers.Local;
using Foldy.UI.Controls;
using Foldy.UI.Theming;
using Serilog;

namespace Foldy.App;

/// <summary>
/// M0 기술 검증 화면. 10만 개 목록 스크롤 벤치, 로컬 폴더 스트리밍 열기, Rust 엔진 연결 상태를 보여준다.
/// M1에서 Presentation 프로젝트의 ViewModel(MVVM)과 듀얼 패널 화면으로 대체된다.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true };

    private readonly DriveQueueRegistry _queues = new();
    private readonly LocalProvider _local;
    private readonly bool _useWpfList;
    private CancellationTokenSource? _openCts;
    private IndexerClient? _indexer;

    public MainWindow()
    {
        InitializeComponent();
        _local = new LocalProvider(_queues);
        Loaded += OnLoaded;
        Closed += async (_, _) =>
        {
            _openCts?.Cancel();
            if (_indexer is not null)
            {
                await _indexer.DisposeAsync();
            }

            await _queues.DisposeAsync();
        };
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnScrollBench(this, new RoutedEventArgs())), Key.F9, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => PathBox.Focus()), Key.L, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => _ = OpenFolderAsync(PathBox.Text)), Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => _openCts?.Cancel()), Key.Escape, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnUpClick(this, new RoutedEventArgs())), Key.Up, ModifierKeys.Alt));

        _useWpfList = App.Options.ListWpf;
        FastScroller.Visibility = _useWpfList ? Visibility.Collapsed : Visibility.Visible;
        FileList.Visibility = _useWpfList ? Visibility.Visible : Visibility.Collapsed;
        FastList.Columns.Add(new FastColumn("이름", 400, o => ((FileRow)o).Name));
        FastList.Columns.Add(new FastColumn("수정한 날짜", 150, o => ((FileRow)o).ModifiedText));
        FastList.Columns.Add(new FastColumn("유형", 150, o => ((FileRow)o).TypeText));
        FastList.Columns.Add(new FastColumn("크기", 110, o => ((FileRow)o).SizeText, alignRight: true));
        FastList.SelectionChanged += (_, _) => UpdateCount();
        FastList.ItemActivated += (_, i) => OnActivate(i);
        ThemeManager.Attach(this);

        // 제목 표시줄 왼쪽 위: "Foldy 0.0.1"
        Title = $"Foldy {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PathBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _ = ProbeEnginesAsync();

        var o = App.Options;
        if (o.Font is { } font)
        {
            FileList.FontFamily = new FontFamily(font);
            FastList.FontFamily = new FontFamily(font);
        }

        if (o.BenchList)
        {
            await ShowSyntheticAsync(o.Rows);
            var stats = await RunScrollBenchAsync(o.Step);
            FinishBench(new { kind = "list-scroll", list = _useWpfList ? "wpf-listview" : "fast-file-list", rows = o.Rows, step = o.Step, stats, diag = LastBenchDiag }, stats.PassesBudget);
        }
        else if (o.BenchFolder is { } folder)
        {
            var r = await OpenFolderAsync(folder);
            FinishBench(new { kind = "folder-open", folder, result = r }, r is { Error: null });
        }
        else if (o.Synthetic)
        {
            await ShowSyntheticAsync(o.Rows);
            if (o.Screenshot is { } shot)
            {
                await SaveScreenshotAsync(shot);
                Application.Current.Shutdown(0);
            }
        }
        else
        {
            await OpenFolderAsync(PathBox.Text);
        }
    }

    /// <summary>문서·테스트용 화면 캡처: 창 내용을 직접 렌더링해 PNG로 저장한다 (다른 창에 가려져도 됨).</summary>
    private async Task SaveScreenshotAsync(string path)
    {
        FastList.SelectWithModifiersForDemo([2, 3, 4]);
        await Task.Delay(1500); // 엔진 연결 상태 표시까지 기다림
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }

        bmp.Render(bg);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        await using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ───────────── 엔진 연결 상태 (Rust FFI / Named Pipe) ─────────────

    private async Task ProbeEnginesAsync()
    {
        var (available, reason) = await Task.Run(() => (NativeCopyEngine.IsAvailable, NativeCopyEngine.UnavailableReason));
        CopyEngineText.Text = available ? "복사 엔진: Rust" : "복사 엔진: 폴백(IFileOperation)";
        CopyEngineText.ToolTip = reason ?? $"foldy_copy.dll ABI v{NativeCopyEngine.ExpectedAbiVersion}";
        if (!available)
        {
            Log.Information("Rust 복사 엔진 사용 불가: {Reason}", reason);
        }

        await ConnectIndexerAsync();
    }

    private async Task ConnectIndexerAsync()
    {
        if (_indexer is not null)
        {
            await _indexer.DisposeAsync();
            _indexer = null;
        }

        IndexerText.Text = "색인: 연결 중…";
        _indexer = await IndexerClient.TryConnectAsync(timeout: TimeSpan.FromMilliseconds(300));
        if (_indexer is null)
        {
            IndexerText.Text = "색인: 없음 (일반 열거)";
            IndexerText.ToolTip = "foldy-index 서비스에 연결하지 못해 LocalProvider로 동작합니다.";
            return;
        }

        var st = await _indexer.StatusAsync();
        IndexerText.Text = $"색인: v{_indexer.Hello.ServiceVersion} 연결됨";
        IndexerText.ToolTip = $"볼륨 {string.Join(", ", _indexer.Hello.Volumes)} · 상태 {st.State} · 항목 {st.IndexedEntries:N0}";
    }

    private async void OnReconnectIndexer(object sender, RoutedEventArgs e) => await ConnectIndexerAsync();

    private async void OnCopySelfTest(object sender, RoutedEventArgs e)
    {
        if (!NativeCopyEngine.IsAvailable)
        {
            StatusText.Text = "Rust 복사 엔진이 없습니다: " + NativeCopyEngine.UnavailableReason;
            return;
        }

        StatusText.Text = "복사 엔진 시험 중…";
        var msg = await Task.Run(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "foldy-selftest-" + Environment.ProcessId);
            var src = Path.Combine(dir, "src.bin");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(src, new byte[8 * 1024 * 1024]);
            var sw = Stopwatch.StartNew();
            using var job = NativeCopyEngine.CreateJob([src], Path.Combine(dir, "out"));
            job.Start();
            NativeProgress p;
            while ((p = job.Poll()).State is NativeJobState.Running or NativeJobState.Created)
            {
                Thread.Sleep(10);
            }

            Directory.Delete(dir, true);
            return $"복사 엔진 시험: {p.State}, {p.BytesDone:N0} 바이트, {sw.ElapsedMilliseconds}ms";
        });
        StatusText.Text = msg;
    }

    // ───────────── 폴더 열기 (드라이브 큐 + 스트리밍) ─────────────

    private void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = OpenFolderAsync(PathBox.Text);
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => _ = OpenFolderAsync(PathBox.Text);

    private void OnCancelClick(object sender, RoutedEventArgs e) => _openCts?.Cancel();

    private void OnFocusPath(object sender, RoutedEventArgs e) => PathBox.Focus();

    public sealed record FolderOpenResult(int Entries, double FirstBatchMs, double TotalMs, string? Error);

    private async Task<FolderOpenResult> OpenFolderAsync(string path)
    {
        _openCts?.Cancel(); // 다른 폴더로 이동하면 이전 요청은 즉시 취소
        var cts = _openCts = new CancellationTokenSource();
        var list = new BatchedList<FileRow>();
        SetItems(list);
        CancelButton.IsEnabled = true;
        var sw = Stopwatch.StartNew();
        double firstMs = -1;
        StatusText.Text = "여는 중…";
        using var progress = StartProgressLine();

        try
        {
            await foreach (var batch in _local.ListBatchedAsync(FsPath.Local(path), cts.Token))
            {
                list.AddBatch(batch.Select(b => new FileRow(b)));
                if (firstMs < 0)
                {
                    firstMs = sw.Elapsed.TotalMilliseconds;
                }

                UpdateCount();
                if (sw.Elapsed > TimeSpan.FromSeconds(2))
                {
                    StatusText.Text = $"불러오는 중… {list.Count:N0}개";
                }
            }

            var total = sw.Elapsed.TotalMilliseconds;
            StatusText.Text = $"{path} · {list.Count:N0}개 · 첫 표시 {Math.Max(firstMs, 0):F0}ms · 전체 {total:F0}ms";
            Log.Information("폴더 열기 {Path}: {Count}개, 첫 배치 {First}ms, 전체 {Total}ms", path, list.Count, firstMs, total);
            return new FolderOpenResult(list.Count, firstMs, total, null);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"취소됨 ({list.Count:N0}개까지 표시)";
            return new FolderOpenResult(list.Count, firstMs, sw.Elapsed.TotalMilliseconds, "canceled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 잘못된 경로 입력 시 오류 표시, 앱은 멈추지 않음 (설계서 2장)
            StatusText.Text = "열 수 없음: " + ex.Message;
            return new FolderOpenResult(list.Count, firstMs, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
        finally
        {
            if (_openCts == cts)
            {
                CancelButton.IsEnabled = false;
            }
        }
    }

    /// <summary>300ms 이후에만 경로바 진행선을 움직인다 (깜빡임 방지).</summary>
    private Disposer StartProgressLine()
    {
        var sw = Stopwatch.StartNew();
        void Tick(object? s, EventArgs e)
        {
            if (sw.ElapsedMilliseconds < 300)
            {
                ProgressLine.Width = 0;
                return;
            }

            var host = ((FrameworkElement)ProgressLine.Parent).ActualWidth;
            ProgressLine.Width = host * 0.25;
            ProgressLine.Margin = new Thickness((sw.ElapsedMilliseconds / 4 % (long)Math.Max(1, host * 1.25)) - host * 0.25, 0, 0, 0);
        }

        CompositionTarget.Rendering += Tick;
        return new Disposer(() =>
        {
            CompositionTarget.Rendering -= Tick;
            ProgressLine.Width = 0;
        });
    }

    // ───────────── 10만 개 목록 스크롤 벤치 ─────────────

    private async void OnGenerate(object sender, RoutedEventArgs e) => await ShowSyntheticAsync(100_000);

    private async void OnScrollBench(object sender, RoutedEventArgs e)
    {
        if (ItemCount < 1000)
        {
            await ShowSyntheticAsync(100_000);
        }

        var stats = await RunScrollBenchAsync(App.Options.Step);
        StatusText.Text = "스크롤 벤치: " + stats;
    }

    private async Task ShowSyntheticAsync(int rows)
    {
        _openCts?.Cancel();
        StatusText.Text = $"가짜 항목 {rows:N0}개 만드는 중…";
        var sw = Stopwatch.StartNew();
        var data = await Task.Run(() => FileRow.Synthetic(rows));
        SetItems(data);
        UpdateCount();
        PathBox.Text = @"(벤치) C:\bench";
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        StatusText.Text = $"가짜 항목 {rows:N0}개 표시 ({sw.ElapsedMilliseconds}ms)";
    }

    /// <summary>
    /// 매 프레임 <paramref name="stepRows"/>행씩 끝까지 스크롤하며 CompositionTarget.Rendering 간격을 잰다.
    /// 처음 30프레임은 워밍업으로 버린다.
    /// </summary>
    private Task<FrameStats> RunScrollBenchAsync(int stepRows)
    {
        var sv = _useWpfList
            ? FindChild<ScrollViewer>(FileList) ?? throw new InvalidOperationException("ScrollViewer 없음")
            : FastScroller;
        var unit = _useWpfList ? 1.0 : FastList.RowHeight; // ListView는 항목 단위, FastFileList는 픽셀 단위
        FastList.ResetRenderStats();
        sv.ScrollToTop();
        var tcs = new TaskCompletionSource<FrameStats>();
        var deltas = new List<double>(4096);
        var layoutMs = new List<double>(4096);
        TimeSpan last = default;
        var frame = 0;
        StatusText.Text = "스크롤 벤치 실행 중…";

        void Tick(object? s, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == last)
            {
                return; // 같은 프레임에서 중복 호출
            }

            if (frame > 30 && last != default)
            {
                deltas.Add((now - last).TotalMilliseconds);
            }

            last = now;
            frame++;
            if ((sv.VerticalOffset >= sv.ScrollableHeight - 0.5 && frame > 30) || deltas.Count >= 900)
            {
                CompositionTarget.Rendering -= Tick;
                var stats = FrameStats.From(deltas);
                LastBenchDiag = new BenchDiag(
                    RenderCapability.Tier >> 16,
                    layoutMs.Count == 0 ? 0 : layoutMs.Average(),
                    _useWpfList ? 0 : FastList.AverageRenderMs,
                    _useWpfList ? CountRealized(FileList) : 0,
                    _useWpfList ? sv.ViewportHeight : sv.ViewportHeight / unit);
                Log.Information("스크롤 벤치 {Stats} {Diag}", stats, LastBenchDiag);
                tcs.TrySetResult(stats);
                return;
            }

            if (frame > 30)
            {
                var t0 = Stopwatch.GetTimestamp();
                sv.ScrollToVerticalOffset(sv.VerticalOffset + stepRows * unit);
                ((UIElement)(_useWpfList ? FileList : FastList)).UpdateLayout();
                layoutMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
        }

        CompositionTarget.Rendering += Tick;
        return tcs.Task;
    }

    /// <summary>벤치 진단값: 렌더링 등급(2 = 하드웨어 가속), 프레임당 레이아웃 시간, 실현된 행 컨테이너 수.</summary>
    public sealed record BenchDiag(int RenderTier, double LayoutAvgMs, double RenderAvgMs, int RealizedContainers, double ViewportRows);

    private BenchDiag? LastBenchDiag { get; set; }

    private static int CountRealized(DependencyObject root)
    {
        var n = root is ListViewItem ? 1 : 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            n += CountRealized(VisualTreeHelper.GetChild(root, i));
        }

        return n;
    }

    private static void FinishBench(object result, bool pass)
    {
        var json = JsonSerializer.Serialize(result, JsonOut);
        if (App.Options.Out is { } path)
        {
            File.WriteAllText(path, json);
        }

        Log.Information("벤치 결과 {Json}", json);
        Application.Current.Shutdown(pass ? 0 : 2);
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t)
            {
                return t;
            }

            if (FindChild<T>(c) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // ───────────── 기타 ─────────────

    private int ItemCount => _useWpfList ? FileList.Items.Count : FastList.Count;

    private void SetItems(System.Collections.IList items)
    {
        if (_useWpfList)
        {
            FileList.ItemsSource = items;
        }
        else
        {
            FastList.ItemsSource = items;
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        var sel = _useWpfList ? FileList.SelectedItems.Count : FastList.SelectedCount;
        CountText.Text = sel > 0 ? $"{ItemCount:N0}개 항목 · {sel:N0}개 선택" : $"{ItemCount:N0}개 항목";
    }

    private void OnActivate(int index)
    {
        if (FastList.ItemsSource?[index] is FileRow { Entry.Kind: FsKind.Folder or FsKind.Drive } row)
        {
            PathBox.Text = row.Entry.Path.Value;
            _ = OpenFolderAsync(row.Entry.Path.Value);
        }
    }

    private void OnUpClick(object sender, RoutedEventArgs e)
    {
        var parent = Path.GetDirectoryName(PathBox.Text.TrimEnd('\\'));
        if (!string.IsNullOrEmpty(parent))
        {
            PathBox.Text = parent.Length == 2 ? parent + "\\" : parent;
            _ = OpenFolderAsync(PathBox.Text);
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        ThemeSystem.IsChecked = ThemeManager.Mode == FoldyTheme.System;
        ThemeLight.IsChecked = ThemeManager.Mode == FoldyTheme.Light;
        ThemeDark.IsChecked = ThemeManager.Mode == FoldyTheme.Dark;
        ThemeRetro.IsChecked = ThemeManager.Mode == FoldyTheme.Retro;
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreMenu.IsOpen = true;
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<FoldyTheme>(tag, out var mode))
        {
            ThemeManager.SetMode(mode);
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnAbout(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private sealed class Disposer(Action a) : IDisposable
    {
        public void Dispose() => a();
    }

    private sealed class RelayCommand(Action a) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => a();
    }
}
