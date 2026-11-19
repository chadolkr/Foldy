using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Foldy.Presentation.Jobs;
using Foldy.Presentation.Mascot;
using Foldy.UI.Theming;

namespace Foldy.UI.Mascot;

/// <summary>
/// 작업 패널 한 줄(높이 48px 띠)에서 Foldy 마스코트를 그리는 컨트롤. 속성만 채워 주면
/// 판정(<see cref="MascotViewModel"/>)·시간 규칙(5초 이상, 완료 후 2초)·재생을 스스로 처리한다.
/// 보이지 않는 동안(5초 미만, 꺼짐, OS 애니메이션 끔)은 크기 0이라 자리도 차지하지 않는다.
/// 창이 최소화되거나 비활성이거나 컨트롤이 안 보이면 렌더링을 멈춘다.
/// </summary>
public sealed class MascotView : FrameworkElement
{
    private static readonly Lazy<MascotSceneCatalog> CatalogLazy = new(LoadCatalog);

    private readonly MascotClock _clock = new();
    private MascotPainter? _painter;
    private bool _paintRetro;
    private MascotFrame _frame = MascotFrame.Hidden;
    private double _animTime;
    private long _lastTick;
    private bool _subscribed;
    private bool _windowRunning = true;
    private Window? _window;
    private WriteableBitmap? _retroBitmap;
    private bool _retroDirty = true;
    private readonly int _autoSeed = Random.Shared.Next(1, int.MaxValue);
    private double _shownProgress = -1;

    public MascotView()
    {
        SnapsToDevicePixels = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor); // 레트로 확대는 도트 그대로
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateTicking();
    }

    /// <summary>프레임이 바뀔 때(장면·말풍선·표시 여부). 호스트가 "[취소] 제안" 같은 것을 붙일 때 쓴다.</summary>
    public event EventHandler? FrameChanged;

    public MascotFrame CurrentFrame => _frame;

    // ───────────────────────── 입력 속성 (JobViewModel에서 바인딩) ─────────────────────────

    public static readonly DependencyProperty KindProperty = Reg(nameof(Kind), JobKind.Copy);
    public static readonly DependencyProperty StateProperty = Reg(nameof(State), JobState.Queued);
    public static readonly DependencyProperty BytesPerSecondProperty = Reg(nameof(BytesPerSecond), 0.0);
    public static readonly DependencyProperty ProgressProperty = Reg(nameof(Progress), -1.0);
    public static readonly DependencyProperty PhaseProperty = Reg(nameof(Phase), MascotPhase.None);
    public static readonly DependencyProperty HasConflictProperty = Reg(nameof(HasConflict), false);
    public static readonly DependencyProperty NetworkLostProperty = Reg(nameof(NetworkLost), false);
    public static readonly DependencyProperty ItemFailedProperty = Reg(nameof(ItemFailed), false);

    /// <summary>동작 순서를 정하는 시드. 0이면 뷰마다 무작위로 정해 같은 화면의 작업끼리 다르게 보이고, 값을 주면 항상 같은 순서가 된다.</summary>
    public static readonly DependencyProperty SeedProperty = Reg(nameof(Seed), 0);

    /// <summary>설정에서 마스코트를 켰는가 (기본 켜짐).</summary>
    public static readonly DependencyProperty IsMascotEnabledProperty = Reg(nameof(IsMascotEnabled), true);

    /// <summary>true면 레트로(픽셀), false면 모던(벡터). null이면 현재 테마를 따른다.</summary>
    public static readonly DependencyProperty RetroProperty = Reg(nameof(Retro), (bool?)null);

    /// <summary>창이 비활성일 때 재생을 멈출지 (기본 true).</summary>
    public static readonly DependencyProperty PauseWhenInactiveProperty = Reg(nameof(PauseWhenInactive), true);

    public JobKind Kind { get => (JobKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public JobState State { get => (JobState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    public double BytesPerSecond { get => (double)GetValue(BytesPerSecondProperty); set => SetValue(BytesPerSecondProperty, value); }

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public MascotPhase Phase { get => (MascotPhase)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }

    public bool HasConflict { get => (bool)GetValue(HasConflictProperty); set => SetValue(HasConflictProperty, value); }

    public bool NetworkLost { get => (bool)GetValue(NetworkLostProperty); set => SetValue(NetworkLostProperty, value); }

    public bool ItemFailed { get => (bool)GetValue(ItemFailedProperty); set => SetValue(ItemFailedProperty, value); }

    public int Seed { get => (int)GetValue(SeedProperty); set => SetValue(SeedProperty, value); }

    public bool IsMascotEnabled { get => (bool)GetValue(IsMascotEnabledProperty); set => SetValue(IsMascotEnabledProperty, value); }

    public bool? Retro { get => (bool?)GetValue(RetroProperty); set => SetValue(RetroProperty, value); }

    public bool PauseWhenInactive { get => (bool)GetValue(PauseWhenInactiveProperty); set => SetValue(PauseWhenInactiveProperty, value); }

    private static DependencyProperty Reg<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(MascotView), new PropertyMetadata(def, OnInputChanged));

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MascotView)d).Refresh();
    }

    // ───────────────────────── 판정 ─────────────────────────

    private bool UseRetro => Retro ?? ThemeManager.Mode == FoldyTheme.Retro;

    private static MascotSceneCatalog Catalog => CatalogLazy.Value;

    private static MascotSceneCatalog LoadCatalog()
    {
        try
        {
            using var s = typeof(MascotView).Assembly.GetManifestResourceStream("Foldy.UI.Mascot.scenes.json");
            if (s is not null)
            {
                using var r = new StreamReader(s);
                return MascotSceneCatalog.Parse(r.ReadToEnd());
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or IOException)
        {
            Debug.WriteLine($"scenes.json 읽기 실패, 기본값 사용: {ex.Message}");
        }

        return MascotSceneCatalog.Fallback;
    }

    /// <summary>화면에 쓰는 프레임: 진행률은 부드럽게 따라가게 해서(껑충 뛰지 않게) 플레이 중심이 미끄러지듯 전진한다.</summary>
    private MascotFrame RenderFrame => _shownProgress >= 0 && _frame.Progress >= 0 ? _frame with { Progress = _shownProgress } : _frame;

    private void SmoothProgress(double dt)
    {
        var target = _frame.Progress;
        if (target < 0)
        {
            _shownProgress = -1;
        }
        else if (_shownProgress < 0)
        {
            _shownProgress = target;
        }
        else
        {
            _shownProgress += (target - _shownProgress) * (1 - Math.Exp(-dt * 2.5));
        }
    }

    private MascotInput BuildInput() => new()
    {
        Seed = Seed != 0 ? Seed : _autoSeed,
        Kind = Kind,
        State = State,
        Elapsed = _clock.Elapsed,
        SinceFinished = _clock.SinceFinished,
        BytesPerSecond = BytesPerSecond,
        Progress = Progress,
        Phase = Phase,
        HasConflict = HasConflict,
        NetworkLost = NetworkLost,
        ItemFailed = ItemFailed,
    };

    private void Refresh()
    {
        _clock.Update(State);
        var options = new MascotOptions { Enabled = IsMascotEnabled, AnimationsAllowed = true }; // VDI·원격 데스크톱은 Windows 애니메이션 효과를 꺼 두는 경우가 많다. 끄기는 Foldy 설정 "애니메이션 사용"으로만
        var next = MascotViewModel.Evaluate(BuildInput(), options);
        var wasVisible = _frame.Visible;
        var sceneChanged = next.Scene != _frame.Scene;
        var changed = next != _frame;
        _frame = next;
        if (sceneChanged)
        {
            _animTime = 0; // 새 장면은 처음 동작부터 (완료·오류 같은 한 번짜리 장면)
            ToolTip = next.Visible ? $"연주 · 우진 — {Catalog.Get(next.Scene).Description}" : null;
        }

        if (wasVisible != next.Visible)
        {
            InvalidateMeasure();
        }

        if (changed)
        {
            _retroDirty = true;
            InvalidateVisual();
            FrameChanged?.Invoke(this, EventArgs.Empty);
        }

        UpdateTicking();
    }

    // ───────────────────────── 틱 (CompositionTarget.Rendering, 프레임 수 제한) ─────────────────────────

    // 진행 중인 작업은 5초가 지나 나타나야 하므로 숨은 동안에도 느리게(4Hz) 판정만 돈다. 끝나서 숨으면 틱을 끊는다.
    private bool NeedsTicking =>
        IsLoaded && IsVisible && IsMascotEnabled && _windowRunning
        && (_frame.Visible || State is JobState.Running or JobState.Paused or JobState.NeedsAttention);

    private void UpdateTicking()
    {
        var need = NeedsTicking;
        if (need && !_subscribed)
        {
            _subscribed = true;
            _lastTick = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnRendering;
        }
        else if (!need && _subscribed)
        {
            _subscribed = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds;
        var retro = UseRetro;
        var fps = retro ? Catalog.Render.RetroFps : Catalog.Render.ModernFps;
        var interval = _frame.Visible ? 1.0 / Math.Max(1, fps) : 0.25;
        if (dt < interval - 0.002)
        {
            return;
        }

        _lastTick = now;
        _animTime += Math.Min(dt, 0.25) * _frame.Speed;
        Refresh();
        SmoothProgress(Math.Min(dt, 0.25));
        if (_frame.Visible)
        {
            _retroDirty = true;
            InvalidateVisual();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.StateChanged += OnWindowChanged;
            _window.Activated += OnWindowChanged;
            _window.Deactivated += OnWindowChanged;
        }

        ThemeManager.Changed += OnThemeChanged;
        System.Windows.SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        OnWindowChanged(null, EventArgs.Empty);
        _painter = null;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowChanged;
            _window.Activated -= OnWindowChanged;
            _window.Deactivated -= OnWindowChanged;
            _window = null;
        }

        ThemeManager.Changed -= OnThemeChanged;
        System.Windows.SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        UpdateTicking();
    }

    private void OnWindowChanged(object? sender, EventArgs e)
    {
        _windowRunning = _window is null || (_window.WindowState != WindowState.Minimized && (!PauseWhenInactive || _window.IsActive));
        _lastTick = Stopwatch.GetTimestamp(); // 멈췄다 재개할 때 시간이 한꺼번에 튀지 않게
        UpdateTicking();
    }

    private void OnThemeChanged()
    {
        _painter = null;
        _retroDirty = true;
        InvalidateVisual();
    }

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            Refresh();
        }
    }

    // ───────────────────────── 레이아웃·그리기 ─────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!_frame.Visible)
        {
            return new Size(0, 0);
        }

        var w = double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width;
        return new Size(w, MascotPainter.DesignHeight);
    }

    private MascotPainter GetPainter(bool retro)
    {
        if (_painter is null || _paintRetro != retro)
        {
            _paintRetro = retro;
            _painter = new MascotPainter(
                MascotPalette.Resolve(this, retro),
                retro ? Catalog.Render.RetroScale : 0,
                Catalog.Render.MaxWorldWidth)
            {
                PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip,
            };
        }

        return _painter;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (!_frame.Visible || ActualWidth < 20)
        {
            return;
        }

        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, MascotPainter.DesignHeight)); // 툴팁용 히트 영역
        var retro = UseRetro;
        var def = Catalog.Get(_frame.Scene);
        var cycles = _animTime / def.LoopSeconds;
        var u = def.Loop ? cycles - Math.Floor(cycles) : Math.Min(cycles, 1);
        var t = _animTime;
        if (retro)
        {
            // 레트로는 시간을 retroFps 격자로 양자화해서 뚝뚝 끊기게 한다
            var fps = Math.Max(1, Catalog.Render.RetroFps);
            t = Math.Floor(_animTime * fps) / fps;
            cycles = t / def.LoopSeconds;
            u = def.Loop ? cycles - Math.Floor(cycles) : Math.Min(cycles, 1);
        }

        var painter = GetPainter(retro);
        var frame = RenderFrame;
        var cycle = def.Loop ? (int)Math.Floor(cycles) : 0;
        if (retro)
        {
            if (_retroDirty || _retroBitmap is null || Math.Abs(_retroBitmap.PixelWidth - (ActualWidth / Catalog.Render.RetroScale)) > 1.5)
            {
                _retroBitmap = MascotBitmap.RenderRetro(painter, ActualWidth, frame, u, t, Catalog.Render.RetroScale, cycle);
                _retroDirty = false;
            }

            drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, MascotPainter.DesignHeight)));
            drawingContext.DrawImage(
                _retroBitmap,
                new Rect(0, 0, _retroBitmap.PixelWidth * (double)Catalog.Render.RetroScale, _retroBitmap.PixelHeight * (double)Catalog.Render.RetroScale));
            painter.PaintLabels(drawingContext); // 이름표와 말풍선 글리프는 확대 후에 또렷하게
            drawingContext.Pop();
        }
        else
        {
            painter.Paint(drawingContext, ActualWidth, frame, u, t, cycle);
        }
    }

    /// <summary>테스트·미리보기용: 현재 입력과 무관하게 주어진 프레임을 같은 방식으로 한 장 그려 비트맵으로 돌려준다.</summary>
    public BitmapSource Snapshot(MascotFrame frame, double u, double width, bool retro, double? seconds = null)
    {
        var def = Catalog.Get(frame.Scene);
        var painter = GetPainter(retro);
        var t = seconds ?? (u * def.LoopSeconds);
        return retro
            ? MascotBitmap.RenderRetro(painter, width, frame, u, t, Catalog.Render.RetroScale)
            : MascotBitmap.RenderVector(painter, width, frame, u, t);
    }

    /// <summary>번들된 장면 정의 (호스트·테스트가 확인용).</summary>
    public static MascotSceneCatalog SceneCatalog => Catalog;
}
