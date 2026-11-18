using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Foldy.UI.Imaging;

/// <summary>
/// 이미지 하나를 맞춤 / 100% / 휠 확대로 그리는 요소. 미리보기 창과 빠른 보기가 함께 쓴다.
/// 회전(<see cref="Rotation"/>)은 화면에서만 돌리고 파일은 바꾸지 않는다.
/// 100%는 원본 픽셀 1개 = 화면 픽셀 1개 (<see cref="NaturalPixelWidth"/> 기준이라 축소 디코딩한 이미지도 맞게 커진다).
/// 스레드 소유권: UI 스레드 전용.
/// </summary>
public sealed class ZoomImageView : FrameworkElement
{
    private const double MinZoom = 0.02;
    private const double MaxZoom = 32;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(BitmapSource), typeof(ZoomImageView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(ZoomImageView),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    private Vector _pan;
    private Point? _dragStart;
    private Vector _panAtDragStart;

    public ZoomImageView()
    {
        ClipToBounds = true;
        Focusable = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    /// <summary>보기 방식이 바뀌었다 (맞춤 ↔ 100%, 확대율, 회전). 호스트가 더 큰 이미지를 불러오는 신호.</summary>
    public event EventHandler? ViewChanged;

    public BitmapSource? Source { get => (BitmapSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    /// <summary>원본 해상도 (회전 전 축 기준이 아니라 방향 적용 후). 0이면 Source 크기를 원본으로 본다.</summary>
    public int NaturalPixelWidth { get; set; }

    public int NaturalPixelHeight { get; set; }

    /// <summary>화면 회전 각 (0·90·180·270).</summary>
    public int Rotation { get; private set; }

    /// <summary>null이면 맞춤, 값이 있으면 원본 대비 배율 (1 = 100%).</summary>
    public double? Zoom { get; private set; }

    public bool IsFit => Zoom is null;

    /// <summary>휠로 확대/축소할지 (빠른 보기는 켜고, 미리보기 창은 꺼서 스크롤에 양보).</summary>
    public bool WheelZoom { get; set; }

    /// <summary>이미지를 바꿀 때: 맞춤, 회전 0으로 돌아간다.</summary>
    public void Reset(BitmapSource? source, int naturalWidth, int naturalHeight)
    {
        Source = source;
        NaturalPixelWidth = naturalWidth;
        NaturalPixelHeight = naturalHeight;
        Rotation = 0;
        Zoom = null;
        _pan = default;
        InvalidateVisual();
    }

    /// <summary>같은 이미지의 더 큰 디코딩 결과로 바꾼다 (보기 상태 유지).</summary>
    public void Upgrade(BitmapSource source)
    {
        Source = source;
        InvalidateVisual();
    }

    public void RotateClockwise()
    {
        Rotation = (Rotation + 90) % 360;
        _pan = default;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>맞춤 ↔ 100%.</summary>
    public void ToggleFitActual()
    {
        Zoom = Zoom is null ? 1.0 : null;
        _pan = default;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomBy(double factor, Point? around = null)
    {
        var current = Zoom ?? FitZoom();
        var next = Math.Clamp(current * factor, MinZoom, MaxZoom);
        if (around is { } p)
        {
            // 커서 아래 지점이 그대로 있도록 이동량 보정
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var v = p - center - _pan;
            _pan += v - v * (next / current);
        }

        Zoom = next;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>원본 대비 현재 배율 (맞춤일 때도 계산값). 큰 이미지를 다시 불러올지 판단용.</summary>
    public double EffectiveZoom => Zoom ?? FitZoom();

    private (double W, double H) NaturalSize()
    {
        var src = Source;
        double w = NaturalPixelWidth > 0 ? NaturalPixelWidth : src?.PixelWidth ?? 0;
        double h = NaturalPixelHeight > 0 ? NaturalPixelHeight : src?.PixelHeight ?? 0;
        return Rotation is 90 or 270 ? (h, w) : (w, h);
    }

    /// <summary>원본 픽셀 기준 맞춤 배율 (작은 이미지는 키우지 않는다).</summary>
    private double FitZoom()
    {
        var (w, h) = NaturalSize();
        if (w <= 0 || h <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return 1;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return Math.Min(1, Math.Min(ActualWidth * dpi / w, ActualHeight * dpi / h));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var src = Source;
        if (src is null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var nat = NaturalSize();
        var zoom = EffectiveZoom;

        // 화면 크기(DIP): 원본 픽셀 × 배율 ÷ DPI. 회전 전 축으로 그리고 가운데에서 돌린다.
        var (nw, nh) = Rotation is 90 or 270 ? (nat.H, nat.W) : (nat.W, nat.H);
        var dw = nw * zoom / dpi;
        var dh = nh * zoom / dpi;
        var cx = ActualWidth / 2 + _pan.X;
        var cy = ActualHeight / 2 + _pan.Y;
        dc.PushTransform(new TranslateTransform(cx, cy));
        dc.PushTransform(new RotateTransform(Rotation));
        dc.DrawImage(src, new Rect(-dw / 2, -dh / 2, dw, dh));
        dc.Pop();
        dc.Pop();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!WheelZoom || Source is null)
        {
            return;
        }

        ZoomBy(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            ToggleFitActual();
            e.Handled = true;
            return;
        }

        if (!IsFit)
        {
            _dragStart = e.GetPosition(this);
            _panAtDragStart = _pan;
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is { } s && IsMouseCaptured)
        {
            _pan = _panAtDragStart + (e.GetPosition(this) - s);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragStart is not null)
        {
            _dragStart = null;
            ReleaseMouseCapture();
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
