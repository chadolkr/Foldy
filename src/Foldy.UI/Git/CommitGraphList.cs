using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Foldy.Git;
using Foldy.Presentation.Git;

namespace Foldy.UI.Git;

/// <summary>
/// 커밋 그래프 목록 (Fork식). FastFileList 와 같은 방식: 보이는 행만 DrawingContext 로 직접 그리고
/// 행마다 만든 FormattedText 를 캐시한다. 컨테이너·바인딩·템플릿이 없어 수만 커밋도 스크롤이 가볍다.
/// 한 행 = 레인(선·점) + ref 칩 + 제목 + 작성자 + 날짜 + 해시.
/// 레인 색은 열 번호로 고르는 고정 중간 톤 팔레트다 (라이트/다크 모두에서 보이도록). 나머지 색은 모두 테마 토큰.
/// 스레드 소유권: UI 스레드 전용.
/// </summary>
public sealed class CommitGraphList : FrameworkElement, IScrollInfo
{
    private const double LaneWidth = 14;
    private const double GraphPadding = 8;
    private const int MaxDrawnLanes = 14;
    private const int TextCacheLimit = 1500;

    // 열 번호별 레인 색 (중간 명도·채도: 흰 배경과 어두운 배경 모두에서 구분된다)
    private static readonly Color[] LaneColors =
    [
        Color.FromRgb(0x4E, 0x8D, 0xF5), Color.FromRgb(0xE0, 0x88, 0x3E), Color.FromRgb(0x3F, 0xB6, 0x8B), Color.FromRgb(0xB1, 0x67, 0xE0),
        Color.FromRgb(0xD9, 0xAD, 0x2B), Color.FromRgb(0x37, 0xB2, 0xC8), Color.FromRgb(0xE4, 0x60, 0x7F), Color.FromRgb(0x8A, 0x7B, 0xE8),
    ];

    private static readonly Pen[] LanePens = [.. LaneColors.Select(c =>
    {
        var p = new Pen(new SolidColorBrush(c), 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    })];

    private static readonly Brush[] LaneBrushes = [.. LaneColors.Select(c =>
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return (Brush)b;
    })];

    // ───────────── 의존 속성 ─────────────

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IReadOnlyList<GitCommitRow>), typeof(CommitGraphList),
        new FrameworkPropertyMetadata(null, (d, _) => ((CommitGraphList)d).OnItemsChanged()));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(GitCommitRow), typeof(CommitGraphList),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((CommitGraphList)d).OnSelectedChanged()));

    public static readonly DependencyProperty ShowGraphProperty = DependencyProperty.Register(
        nameof(ShowGraph), typeof(bool), typeof(CommitGraphList), new FrameworkPropertyMetadata(true, ResetVisuals));

    public static readonly DependencyProperty MaxLanesProperty = DependencyProperty.Register(
        nameof(MaxLanes), typeof(int), typeof(CommitGraphList), new FrameworkPropertyMetadata(1, ResetVisuals));

    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(CommitGraphList), new FrameworkPropertyMetadata(26.0, ResetVisuals));

    public static readonly DependencyProperty BackgroundProperty = Register(nameof(Background), Brushes.Transparent);
    public static readonly DependencyProperty ForegroundProperty = Register(nameof(Foreground), Brushes.Black);
    public static readonly DependencyProperty SecondaryForegroundProperty = Register(nameof(SecondaryForeground), Brushes.Gray);
    public static readonly DependencyProperty SelectionBrushProperty = Register(nameof(SelectionBrush), Brushes.LightGray);
    public static readonly DependencyProperty SelectionForegroundProperty = Register(nameof(SelectionForeground), Brushes.Black);
    public static readonly DependencyProperty HoverBrushProperty = Register(nameof(HoverBrush), Brushes.Transparent);
    public static readonly DependencyProperty AccentBrushProperty = Register(nameof(AccentBrush), Brushes.Orange);
    public static readonly DependencyProperty AccentForegroundProperty = Register(nameof(AccentForeground), Brushes.White);
    public static readonly DependencyProperty TagBrushProperty = Register(nameof(TagBrush), Brushes.Green);
    public static readonly DependencyProperty FocusBrushProperty = Register(nameof(FocusBrush), Brushes.Orange);

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(CommitGraphList), new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits, ResetVisuals));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(CommitGraphList), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.Inherits, ResetVisuals));

    private static DependencyProperty Register(string name, Brush def) => DependencyProperty.Register(
        name, typeof(Brush), typeof(CommitGraphList), new FrameworkPropertyMetadata(def, ResetVisuals));

    private static void ResetVisuals(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CommitGraphList)d).ResetCache();

    public IReadOnlyList<GitCommitRow>? ItemsSource { get => (IReadOnlyList<GitCommitRow>?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public GitCommitRow? SelectedItem { get => (GitCommitRow?)GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public bool ShowGraph { get => (bool)GetValue(ShowGraphProperty); set => SetValue(ShowGraphProperty, value); }
    public int MaxLanes { get => (int)GetValue(MaxLanesProperty); set => SetValue(MaxLanesProperty, value); }
    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush SecondaryForeground { get => (Brush)GetValue(SecondaryForegroundProperty); set => SetValue(SecondaryForegroundProperty, value); }
    public Brush SelectionBrush { get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public Brush SelectionForeground { get => (Brush)GetValue(SelectionForegroundProperty); set => SetValue(SelectionForegroundProperty, value); }
    public Brush HoverBrush { get => (Brush)GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush AccentForeground { get => (Brush)GetValue(AccentForegroundProperty); set => SetValue(AccentForegroundProperty, value); }
    public Brush TagBrush { get => (Brush)GetValue(TagBrushProperty); set => SetValue(TagBrushProperty, value); }
    public Brush FocusBrush { get => (Brush)GetValue(FocusBrushProperty); set => SetValue(FocusBrushProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    /// <summary>스크롤이 목록 끝 가까이 왔다 (다음 페이지를 읽을 때).</summary>
    public event EventHandler? ReachedEnd;

    // ───────────── 상태 ─────────────

    private readonly Dictionary<int, RowText> _textCache = [];
    private Typeface? _typeface;
    private Size _viewport;
    private double _offsetY;
    private int _hover = -1;
    private int _selectedIndex = -1;

    public CommitGraphList()
    {
        Focusable = true;
        ClipToBounds = true;
        FocusVisualStyle = null;
    }

    private int Count => ItemsSource?.Count ?? 0;

    private void OnItemsChanged()
    {
        ResetCache();
        _hover = -1;
        _selectedIndex = IndexOf(SelectedItem);
        InvalidateScroll();
    }

    private void OnSelectedChanged() => _selectedIndex = IndexOf(SelectedItem);

    private int IndexOf(GitCommitRow? row)
    {
        if (row is null || ItemsSource is not { } items) return -1;
        for (var i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], row)) return i;
        return -1;
    }

    private void ResetCache()
    {
        _textCache.Clear();
        _typeface = null;
        InvalidateVisual();
    }

    private void InvalidateScroll()
    {
        CoerceOffset();
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
        if (Count > 0 && ExtentHeight - (_offsetY + _viewport.Height) < 20 * RowHeight) ReachedEnd?.Invoke(this, EventArgs.Empty);
    }

    // ───────────── 배치 ─────────────

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? Math.Min(Count * RowHeight, 400) : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Math.Abs(finalSize.Width - _viewport.Width) > 0.5) _textCache.Clear();   // 제목 말줄임 폭이 바뀐다
        _viewport = finalSize;
        InvalidateScroll();
        return finalSize;
    }

    private double GraphWidth => ShowGraph ? Math.Min(Math.Max(MaxLanes, 1), MaxDrawnLanes) * LaneWidth + GraphPadding * 2 : GraphPadding;

    // 오른쪽 고정 열: 해시, 날짜, 작성자 (좁으면 뒤에서부터 숨긴다)
    private (double hash, double date, double author) RightColumns(double width)
    {
        var rest = width - GraphWidth;
        return rest switch
        {
            > 720 => (64, 118, 140),
            > 560 => (0, 118, 140),
            > 420 => (0, 0, 120),
            _ => (0, 0, 0),
        };
    }

    // ───────────── 그리기 ─────────────

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        if (ItemsSource is not { Count: > 0 } items || w <= 0) return;

        var rh = RowHeight;
        var first = Math.Max(0, (int)(_offsetY / rh));
        var last = Math.Min(items.Count - 1, (int)((_offsetY + h) / rh));
        var gw = GraphWidth;
        var (hashW, dateW, authorW) = RightColumns(w);
        var rightW = hashW + dateW + authorW;
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ring = new Pen(Background, 2);
        var drawGraph = ShowGraph;

        for (var i = first; i <= last; i++)
        {
            var y = i * rh - _offsetY;
            var selected = i == _selectedIndex;
            if (selected) dc.DrawRectangle(SelectionBrush, null, new Rect(0, y, w, rh));
            else if (i == _hover) dc.DrawRectangle(HoverBrush, null, new Rect(0, y, w, rh));

            var row = items[i];
            if (drawGraph) DrawLanes(dc, row.Graph, y, rh, ring);

            var t = GetText(i, row.Node, w - gw - rightW - 8, ppd);
            var x = gw;
            var fg = selected ? SelectionForeground : Foreground;
            foreach (var chip in t.Chips)
            {
                if (x + chip.Width > gw + (w - gw - rightW) * 0.5) break;   // 칩이 제목 자리를 다 먹지 않게
                DrawChip(dc, chip, x, y, rh);
                x += chip.Width + 4;
            }
            if (t.Subject is { } s)
            {
                s.MaxTextWidth = Math.Max(10, w - rightW - x - 8);
                dc.DrawText(Recolor(s, fg), new Point(x, y + (rh - s.Height) / 2));
            }

            var rx = w - rightW;
            var sec = selected ? SelectionForeground : SecondaryForeground;
            if (authorW > 0 && t.Author is { } a) DrawRight(dc, a, rx, authorW, y, rh, sec);
            rx += authorW;
            if (dateW > 0 && t.Date is { } d) DrawRight(dc, d, rx, dateW, y, rh, sec);
            rx += dateW;
            if (hashW > 0 && t.Hash is { } hh) DrawRight(dc, hh, rx, hashW, y, rh, sec);
        }

        if (IsKeyboardFocused && _selectedIndex >= first && _selectedIndex <= last)
            dc.DrawRectangle(null, new Pen(FocusBrush, 1), new Rect(0.5, _selectedIndex * rh - _offsetY + 0.5, Math.Max(0, w - 1), rh - 1));
    }

    private static FormattedText Recolor(FormattedText t, Brush b)
    {
        t.SetForegroundBrush(b);
        return t;
    }

    private static void DrawRight(DrawingContext dc, FormattedText t, double x, double width, double y, double rh, Brush brush)
    {
        t.MaxTextWidth = Math.Max(10, width - 10);
        t.SetForegroundBrush(brush);
        dc.DrawText(t, new Point(x, y + (rh - t.Height) / 2));
    }

    private static double LaneX(int col) => GraphPadding + (Math.Min(col, MaxDrawnLanes - 1) + 0.5) * LaneWidth;

    private static void DrawLanes(DrawingContext dc, GraphRow g, double y, double rh, Pen ring)
    {
        var cy = y + rh / 2;
        var bottom = y + rh;
        var nodeX = LaneX(g.Column);

        foreach (var c in g.PassThroughColumns)
            dc.DrawLine(LanePens[c % LanePens.Length], new Point(LaneX(c), y), new Point(LaneX(c), bottom));

        // 위에서 이 노드로 합류하는 선 (자식이 여럿이면 여러 열)
        foreach (var c in g.IncomingColumns)
            dc.DrawLine(LanePens[c % LanePens.Length], new Point(LaneX(c), y), new Point(nodeX, cy));

        // 이 노드에서 부모로 나가는 선
        foreach (var c in g.ParentColumns)
            dc.DrawLine(LanePens[c % LanePens.Length], new Point(nodeX, cy), new Point(LaneX(c), bottom));

        var brush = LaneBrushes[g.Column % LaneBrushes.Length];
        dc.DrawEllipse(brush, ring, new Point(nodeX, cy), 4.2, 4.2);
    }

    private void DrawChip(DrawingContext dc, ChipText chip, double x, double y, double rh)
    {
        var rect = new Rect(x, y + 4, chip.Width, rh - 8);
        var brush = chip.Kind switch
        {
            GitRefKind.Tag => TagBrush,
            GitRefKind.RemoteBranch => SecondaryForeground,
            _ => AccentBrush,
        };
        if (chip.IsCurrent)
        {
            dc.DrawRoundedRectangle(AccentBrush, null, rect, 4, 4);
            chip.Text.SetForegroundBrush(AccentForeground);
        }
        else
        {
            dc.DrawRoundedRectangle(null, new Pen(brush, 1), rect, 4, 4);
            chip.Text.SetForegroundBrush(brush);
        }
        dc.DrawText(chip.Text, new Point(x + 6, rect.Y + (rect.Height - chip.Text.Height) / 2));
    }

    // ───────────── 텍스트 캐시 ─────────────

    private sealed record ChipText(FormattedText Text, double Width, GitRefKind Kind, bool IsCurrent);

    private sealed class RowText
    {
        public FormattedText? Subject;
        public FormattedText? Author;
        public FormattedText? Date;
        public FormattedText? Hash;
        public List<ChipText> Chips = [];
    }

    private RowText GetText(int index, CommitNode c, double subjectWidth, double ppd)
    {
        if (_textCache.TryGetValue(index, out var cached)) return cached;
        if (_textCache.Count > TextCacheLimit) _textCache.Clear();

        _typeface ??= new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Make(string s, double maxWidth, bool bold = false) => new(
            s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            bold ? new Typeface(FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : _typeface,
            FontSize, Foreground, ppd)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxTextWidth = Math.Max(10, maxWidth),
        };

        var dateText = c.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var t = new RowText
        {
            Subject = Make(c.Subject, subjectWidth),
            Author = Make(c.AuthorName, 130),
            Date = Make(dateText, 110),
            Hash = Make(c.ShortHash, 60),
        };
        foreach (var r in c.Refs)
        {
            var name = r.Kind == GitRefKind.Head ? "HEAD" : r.Name;
            var ft = Make(name, 160, r.IsCurrentHead);
            ft.MaxTextWidth = 160;
            t.Chips.Add(new ChipText(ft, Math.Min(ft.WidthIncludingTrailingWhitespace, 160) + 12, r.Kind, r.IsCurrentHead));
        }
        _textCache[index] = t;
        return t;
    }

    // ───────────── 입력 ─────────────

    private int RowAt(Point p)
    {
        var i = (int)((p.Y + _offsetY) / RowHeight);
        return i >= 0 && i < Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var i = RowAt(e.GetPosition(this));
        if (i != _hover)
        {
            _hover = i;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var i = RowAt(e.GetPosition(this));
        if (i >= 0) Select(i);
    }

    private void Select(int index)
    {
        if (ItemsSource is not { } items || index < 0 || index >= items.Count) return;
        SelectedItem = items[index];
        _selectedIndex = index;
        EnsureVisible(index);
        InvalidateVisual();
    }

    private void EnsureVisible(int index)
    {
        var top = index * RowHeight;
        if (top < _offsetY) SetVerticalOffset(top);
        else if (top + RowHeight > _offsetY + _viewport.Height) SetVerticalOffset(top + RowHeight - _viewport.Height);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var page = Math.Max(1, (int)(_viewport.Height / RowHeight) - 1);
        var cur = _selectedIndex < 0 ? 0 : _selectedIndex;
        int? next = e.Key switch
        {
            Key.Down => cur + 1,
            Key.Up => cur - 1,
            Key.PageDown => cur + page,
            Key.PageUp => cur - page,
            Key.Home => 0,
            Key.End => Count - 1,
            _ => null,
        };
        if (next is { } n)
        {
            Select(Math.Clamp(n, 0, Math.Max(0, Count - 1)));
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // 16ms 안에 끝나는 가벼운 픽셀 스크롤: 휠 한 칸 = 3행
        SetVerticalOffset(_offsetY - Math.Sign(e.Delta) * 3 * RowHeight);
        e.Handled = true;
    }

    // ───────────── IScrollInfo ─────────────

    public bool CanVerticallyScroll { get; set; } = true;

    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth => _viewport.Width;

    public double ExtentHeight => Count * RowHeight;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => 0;

    public double VerticalOffset => _offsetY;

    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offsetY - RowHeight);

    public void LineDown() => SetVerticalOffset(_offsetY + RowHeight);

    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void PageUp() => SetVerticalOffset(_offsetY - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_offsetY + _viewport.Height);

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    public void MouseWheelUp() => SetVerticalOffset(_offsetY - 3 * RowHeight);

    public void MouseWheelDown() => SetVerticalOffset(_offsetY + 3 * RowHeight);

    public void MouseWheelLeft()
    {
    }

    public void MouseWheelRight()
    {
    }

    public void SetHorizontalOffset(double offset)
    {
    }

    public void SetVerticalOffset(double offset)
    {
        _offsetY = offset;
        InvalidateScroll();
    }

    private void CoerceOffset() => _offsetY = Math.Max(0, Math.Min(_offsetY, ExtentHeight - _viewport.Height));

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
