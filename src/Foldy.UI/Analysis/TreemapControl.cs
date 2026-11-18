using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Foldy.Analysis;
using Foldy.Presentation.Analysis;

namespace Foldy.UI.Analysis;

/// <summary>
/// 직접 그리는 정사각형 트리맵 (설계서 7.3 "트리맵"). 배치 계산은 <see cref="SquarifiedLayout"/>(Foldy.Analysis)가 하고 여기서는 그리기와
/// 입력만 한다. 칸 색은 테마 토큰(Brush.Tab0~7 = 폴더, Brush.SurfaceAlt = 파일, Brush.Hover = 나머지)을 SetResourceReference로
/// 받아 라이트/다크/레트로 전환에 바로 따른다. 폴더 칸을 누르면 <see cref="DrillDownCommand"/>, 오른쪽 클릭·Backspace는 <see cref="UpCommand"/>.
/// 스레드 소유권: UI 스레드 전용.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private const double Gap = 1;
    private const double MinLabelWidth = 46;
    private const double MinLabelHeight = 18;

    private static readonly DependencyProperty[] TileBrushProperties = [.. Enumerable.Range(0, 8).Select(i =>
        DependencyProperty.Register("Tile" + i.ToString(CultureInfo.InvariantCulture), typeof(Brush), typeof(TreemapControl),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender)))];

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IReadOnlyList<TreemapNode>), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TreemapControl)d).Invalidate()));

    public static readonly DependencyProperty DrillDownCommandProperty = DependencyProperty.Register(
        nameof(DrillDownCommand), typeof(ICommand), typeof(TreemapControl));

    public static readonly DependencyProperty UpCommandProperty = DependencyProperty.Register(
        nameof(UpCommand), typeof(ICommand), typeof(TreemapControl));

    public static readonly DependencyProperty FileBrushProperty = Brush(nameof(FileBrush));
    public static readonly DependencyProperty RemainderBrushProperty = Brush(nameof(RemainderBrush));
    public static readonly DependencyProperty GapBrushProperty = Brush(nameof(GapBrush));
    public static readonly DependencyProperty TextBrushProperty = Brush(nameof(TextBrush));
    public static readonly DependencyProperty SecondaryTextBrushProperty = Brush(nameof(SecondaryTextBrush));
    public static readonly DependencyProperty HoverBrushProperty = Brush(nameof(HoverBrush));

    private TreemapRect[] _rects = [];
    private Size _laidOutFor;
    private int _hover = -1;

    public TreemapControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        for (var i = 0; i < TileBrushProperties.Length; i++)
        {
            SetResourceReference(TileBrushProperties[i], "Brush.Tab" + i.ToString(CultureInfo.InvariantCulture));
        }

        SetResourceReference(FileBrushProperty, "Brush.SurfaceAlt");
        SetResourceReference(RemainderBrushProperty, "Brush.Hover");
        SetResourceReference(GapBrushProperty, "Brush.Border");
        SetResourceReference(TextBrushProperty, "Brush.Text");
        SetResourceReference(SecondaryTextBrushProperty, "Brush.TextSecondary");
        SetResourceReference(HoverBrushProperty, "Brush.Accent");
        ToolTipService.SetInitialShowDelay(this, 400);
    }

    /// <summary>폴더 칸 색 n번(0~7) 속성. 기본은 테마 토큰 Brush.Tab{n}에 연결돼 있다.</summary>
    public static DependencyProperty TileBrushProperty(int index) => TileBrushProperties[index % TileBrushProperties.Length];

    private static DependencyProperty Brush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(TreemapControl), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TreemapNode>? ItemsSource
    {
        get => (IReadOnlyList<TreemapNode>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ICommand? DrillDownCommand
    {
        get => (ICommand?)GetValue(DrillDownCommandProperty);
        set => SetValue(DrillDownCommandProperty, value);
    }

    public ICommand? UpCommand
    {
        get => (ICommand?)GetValue(UpCommandProperty);
        set => SetValue(UpCommandProperty, value);
    }

    public Brush FileBrush { get => (Brush)GetValue(FileBrushProperty); set => SetValue(FileBrushProperty, value); }

    public Brush RemainderBrush { get => (Brush)GetValue(RemainderBrushProperty); set => SetValue(RemainderBrushProperty, value); }

    public Brush GapBrush { get => (Brush)GetValue(GapBrushProperty); set => SetValue(GapBrushProperty, value); }

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    public Brush SecondaryTextBrush { get => (Brush)GetValue(SecondaryTextBrushProperty); set => SetValue(SecondaryTextBrushProperty, value); }

    public Brush HoverBrush { get => (Brush)GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }

    private void Invalidate()
    {
        _laidOutFor = default;
        _hover = -1;
        InvalidateVisual();
    }

    private void EnsureLayout()
    {
        var items = ItemsSource ?? [];
        if (_laidOutFor == RenderSize && _rects.Length == items.Count)
        {
            return;
        }

        _rects = SquarifiedLayout.Layout([.. items.Select(n => (double)n.Alloc)], new TreemapRect(0, 0, RenderSize.Width, RenderSize.Height));
        _laidOutFor = RenderSize;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var dc = drawingContext;
        dc.DrawRectangle(GapBrush, null, new Rect(RenderSize));
        var items = ItemsSource;
        if (items is null || items.Count == 0)
        {
            return;
        }

        EnsureLayout();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var family = TextElement.GetFontFamily(this);
        var size = TextElement.GetFontSize(this);
        var face = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var faceSmall = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        for (var i = 0; i < items.Count; i++)
        {
            var r = _rects[i];
            if (r.Width <= Gap * 2 || r.Height <= Gap * 2)
            {
                continue;
            }

            var n = items[i];
            var rect = new Rect(r.X + Gap, r.Y + Gap, r.Width - (Gap * 2), r.Height - (Gap * 2));
            var fill = n.ColorIndex switch
            {
                >= 0 => (Brush)GetValue(TileBrushProperties[n.ColorIndex % TileBrushProperties.Length]),
                -1 => FileBrush,
                _ => RemainderBrush,
            };
            dc.DrawRectangle(fill, i == _hover ? new Pen(HoverBrush, 2) : null, rect);
            if (rect.Width < MinLabelWidth || rect.Height < MinLabelHeight)
            {
                continue;
            }

            dc.PushClip(new RectangleGeometry(rect));
            var name = new FormattedText(n.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, TextBrush, dpi)
            {
                MaxTextWidth = Math.Max(1, rect.Width - 8),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(name, new Point(rect.X + 4, rect.Y + 3));
            if (rect.Height >= (MinLabelHeight * 2) + 2)
            {
                var sz = new FormattedText(n.SizeText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, faceSmall, size - 1, SecondaryTextBrush, dpi)
                {
                    MaxTextWidth = Math.Max(1, rect.Width - 8),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(sz, new Point(rect.X + 4, rect.Y + 4 + name.Height));
            }

            dc.Pop();
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Invalidate();
    }

    private int HitIndex(Point p)
    {
        EnsureLayout();
        return SquarifiedLayout.HitTest(_rects, p.X, p.Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        var i = HitIndex(e.GetPosition(this));
        if (i == _hover)
        {
            return;
        }

        _hover = i;
        var items = ItemsSource;
        ToolTip = items is not null && i >= 0 && i < items.Count ? items[i].ToolTip : null;
        Cursor = items is not null && i >= 0 && i < items.Count && items[i] is { IsDir: true, IsRemainder: false } ? Cursors.Hand : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonUp(e);
        Focus();
        var items = ItemsSource;
        var i = HitIndex(e.GetPosition(this));
        if (items is not null && i >= 0 && i < items.Count && DrillDownCommand is { } cmd && cmd.CanExecute(items[i]))
        {
            cmd.Execute(items[i]);
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseRightButtonUp(e);
        if (UpCommand is { } cmd && cmd.CanExecute(null))
        {
            cmd.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (e.Key is Key.Back && UpCommand is { } cmd && cmd.CanExecute(null))
        {
            cmd.Execute(null);
            e.Handled = true;
        }
    }
}
