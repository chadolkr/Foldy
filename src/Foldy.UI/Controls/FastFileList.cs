using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Foldy.UI.Controls;

public sealed class FileListContextEventArgs(int index, Point screenPoint) : EventArgs
{
    public int Index { get; } = index;

    public Point ScreenPoint { get; } = screenPoint;
}

/// <summary>목록 열 정의. 텍스트는 처음 보일 때 한 번만 만들어 캐시한다.</summary>
public sealed class FastColumn(string header, double width, Func<object, string> text, bool alignRight = false)
{
    public string Header { get; } = header;
    public double Width { get; set; } = width;
    public Func<object, string> Text { get; } = text;
    public bool AlignRight { get; } = alignRight;

    /// <summary>글자 크기 배율 (덜 중요한 열을 작게, 예: 수정한 날짜 0.85).</summary>
    public double FontScale { get; init; } = 1;
}

/// <summary>
/// 직접 그리는 대용량 파일 목록 (설계서 6.2의 FastFileList).
/// <list type="bullet">
/// <item>보이는 행만 DrawingContext로 그리고, 행마다 만든 FormattedText를 캐시한다. 컨테이너·바인딩·템플릿이 없다.</item>
/// <item>IScrollInfo를 직접 구현해 ScrollViewer 안에서 픽셀 단위로 스크롤한다.</item>
/// <item>선택은 비트 배열로 저장해 10만 개 전체 선택도 즉시 끝난다.</item>
/// </list>
/// 스레드 소유권: UI 스레드 전용.
/// </summary>
public sealed class FastFileList : FrameworkElement, IScrollInfo
{
    private const double CellPadding = 10;
    private const int TextCacheLimit = 2048;

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IList), typeof(FastFileList),
        new FrameworkPropertyMetadata(null, (d, e) => ((FastFileList)d).OnItemsSourceChanged((IList?)e.OldValue, (IList?)e.NewValue)));

    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(FastFileList),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((FastFileList)d).ResetVisuals()));

    public static readonly DependencyProperty BackgroundProperty = Panel.BackgroundProperty.AddOwner(
        typeof(FastFileList), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(FastFileList), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits, (d, _) => ((FastFileList)d).ResetVisuals()));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(FastFileList), new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits, (d, _) => ((FastFileList)d).ResetVisuals()));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(FastFileList), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.Inherits, (d, _) => ((FastFileList)d).ResetVisuals()));

    public static readonly DependencyProperty SecondaryForegroundProperty = Register<Brush>(nameof(SecondaryForeground), Brushes.Gray, resetText: true);
    public static readonly DependencyProperty SelectionBrushProperty = Register<Brush>(nameof(SelectionBrush), Brushes.LightBlue);
    public static readonly DependencyProperty SelectionForegroundProperty = Register<Brush>(nameof(SelectionForeground), Brushes.Black, resetText: true);
    public static readonly DependencyProperty HoverBrushProperty = Register<Brush>(nameof(HoverBrush), Brushes.WhiteSmoke);
    public static readonly DependencyProperty HeaderBackgroundProperty = Register<Brush>(nameof(HeaderBackground), Brushes.White);
    public static readonly DependencyProperty HeaderForegroundProperty = Register<Brush>(nameof(HeaderForeground), Brushes.Gray, resetText: true);
    public static readonly DependencyProperty GridLineBrushProperty = Register<Brush>(nameof(GridLineBrush), Brushes.LightGray);
    public static readonly DependencyProperty FocusBrushProperty = Register<Brush>(nameof(FocusBrush), Brushes.DodgerBlue);

    private readonly Dictionary<int, RowText> _textCache = [];
    private readonly Stopwatch _renderWatch = new();
    private INotifyCollectionChanged? _observed;
    private BitArray _selected = new(0);
    private int _selectedCount;
    private int _anchor = -1;
    private int _focus = -1;
    private int _hover = -1;
    private Vector _offset;
    private Size _viewport;
    private double _renderTotalMs;
    private int _renderCount;
    private Typeface? _typeface;
    private int _resizeColumn = -1;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private Point _dragStart;
    private int _dragCandidate = -1;
    private int _pendingClick = -1;
    private FormattedText[]? _headerCache;

    public FastFileList()
    {
        Focusable = true;
        FocusVisualStyle = null;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(this, System.Windows.Automation.AutomationLiveSetting.Polite);
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    public event EventHandler? SelectionChanged;

    public event EventHandler<int>? ItemActivated;

    /// <summary>열 머리글을 눌렀을 때 (정렬). 값은 열 번호.</summary>
    public event EventHandler<int>? HeaderClicked;

    /// <summary>열 너비를 끌어서 바꾼 뒤.</summary>
    public event EventHandler? ColumnsChanged;

    /// <summary>우클릭·메뉴 키. 항목이 없는 곳이면 Index = -1 (폴더 배경 메뉴).</summary>
    public event EventHandler<FileListContextEventArgs>? ContextRequested;

    /// <summary>선택한 항목을 끌기 시작했을 때. 처리기가 DoDragDrop을 호출한다.</summary>
    public event EventHandler? DragRequested;

    /// <summary>글자를 입력했을 때 (Type-ahead).</summary>
    public event EventHandler<string>? TypeAhead;

    /// <summary>이름 열 앞에 그릴 아이콘 (null이면 아이콘 칸 없음).</summary>
    public Func<object, ImageSource?>? IconSelector { get; set; }

    /// <summary>이름 끝에 붙일 작은 배지 (Git 상태 M/A/?/! 등). 글자와 색.</summary>
    public Func<object, (string Text, Brush Brush)?>? BadgeSelector { get; set; }

    /// <summary>이름 뒤 작은 상태 칩 ("작업 완료" 등). 이름을 가리지 않게 남는 자리에만 그린다.</summary>
    public Func<object, IReadOnlyList<(string Text, Brush Brush)>>? ChipSelector { get; set; }

    /// <summary>흐리게 그릴 항목 (숨김 파일 등).</summary>
    public Func<object, bool>? IsDimmed { get; set; }

    public const double IconSize = 16;

    /// <summary>첫 열(이름)이 남는 너비를 채운다. 사용자가 이름 열 너비를 직접 바꾸면 그 값을 따른다.</summary>
    public bool FillFirstColumn { get; set; } = true;

    private bool _userResizedFirst;

    /// <summary>정렬 표시를 그릴 열 (-1이면 없음).</summary>
    public int SortColumn { get; set; } = -1;

    public bool SortDescending { get; set; }

    /// <summary>행 하나의 화면 위치 (컨트롤 좌표). 이름 바꾸기 입력칸 배치용.</summary>
    public Rect RowRect(int index) => new(0, HeaderHeight + index * RowHeight - _offset.Y, ActualWidth, RowHeight);

    /// <summary>이름 칸(아이콘 뒤)의 위치.</summary>
    public Rect NameCellRect(int index)
    {
        var r = RowRect(index);
        var iconSpace = IconSelector is null ? 0 : IconSize + 6;
        var w = Columns.Count > 0 ? Columns[0].Width : r.Width;
        return new Rect(-_offset.X + CellPadding + iconSpace - 4, r.Y + 1, Math.Max(40, w - CellPadding - iconSpace), r.Height - 2);
    }

    /// <summary>한 항목에 포커스·선택을 두고 보이게 스크롤한다.</summary>
    public void FocusIndex(int index, bool select = true)
    {
        if (index < 0 || index >= Count)
        {
            return;
        }

        if (select)
        {
            SelectWithModifiers(index, ModifierKeys.None);
        }
        else
        {
            _focus = _anchor = index;
            InvalidateVisual();
        }

        ScrollIntoView(index);
    }

    /// <summary>행 텍스트가 바뀌었을 때 (폴더 크기가 늦게 채워지는 등) 캐시를 비우고 다시 그린다.</summary>
    public void InvalidateText()
    {
        _textCache.Clear();
        InvalidateVisual();
    }

    /// <summary>아이콘이 늦게 도착했을 때 다시 그린다.</summary>
    public void Redraw() => InvalidateVisual();

    public List<FastColumn> Columns { get; } = [];

    public IList? ItemsSource { get => (IList?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }

    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    public Brush SecondaryForeground { get => (Brush)GetValue(SecondaryForegroundProperty); set => SetValue(SecondaryForegroundProperty, value); }

    public Brush SelectionBrush { get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }

    public Brush SelectionForeground { get => (Brush)GetValue(SelectionForegroundProperty); set => SetValue(SelectionForegroundProperty, value); }

    public Brush HoverBrush { get => (Brush)GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }

    public Brush HeaderBackground { get => (Brush)GetValue(HeaderBackgroundProperty); set => SetValue(HeaderBackgroundProperty, value); }

    public Brush HeaderForeground { get => (Brush)GetValue(HeaderForegroundProperty); set => SetValue(HeaderForegroundProperty, value); }

    public Brush GridLineBrush { get => (Brush)GetValue(GridLineBrushProperty); set => SetValue(GridLineBrushProperty, value); }

    public Brush FocusBrush { get => (Brush)GetValue(FocusBrushProperty); set => SetValue(FocusBrushProperty, value); }

    public int Count => ItemsSource?.Count ?? 0;

    public int SelectedCount => _selectedCount;

    public int FocusedIndex => _focus;

    /// <summary>OnRender 평균 시간(ms). 벤치 진단용.</summary>
    public double AverageRenderMs => _renderCount == 0 ? 0 : _renderTotalMs / _renderCount;

    public void ResetRenderStats() => (_renderTotalMs, _renderCount) = (0, 0);

    private double HeaderHeight => RowHeight + 2;

    public bool IsSelected(int index) => index >= 0 && index < _selected.Length && _selected[index];

    public IEnumerable<int> SelectedIndices()
    {
        for (var i = 0; i < _selected.Length && i < Count; i++)
        {
            if (_selected[i])
            {
                yield return i;
            }
        }
    }

    private static DependencyProperty Register<T>(string name, T def, bool resetText = false) =>
        DependencyProperty.Register(name, typeof(T), typeof(FastFileList), new FrameworkPropertyMetadata(
            def,
            FrameworkPropertyMetadataOptions.AffectsRender,
            resetText ? (d, _) => ((FastFileList)d).ResetVisuals() : null));

    // ───────────── 데이터 ─────────────

    private void OnItemsSourceChanged(IList? oldList, IList? newList)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnCollectionChanged;
        }

        _observed = newList as INotifyCollectionChanged;
        if (_observed is not null)
        {
            _observed.CollectionChanged += OnCollectionChanged;
        }

        _textCache.Clear();
        _selected = new BitArray(newList?.Count ?? 0);
        _selectedCount = 0;
        _anchor = _focus = _hover = -1;
        _offset = default;
        InvalidateScroll();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        _ = oldList;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // BatchedList는 뒤에 붙이기만 하므로 캐시와 선택은 유지하고 크기만 늘린다.
        var n = Count;
        if (n < _selected.Length)
        {
            _textCache.Clear();
            _selected = new BitArray(n);
            _selectedCount = 0;
            _anchor = _focus = _hover = -1;
        }
        else if (n > _selected.Length)
        {
            _selected.Length = n;
        }

        InvalidateScroll();
    }

    private void ResetVisuals()
    {
        _textCache.Clear();
        _typeface = null;
        _headerCache = null;
        InvalidateVisual();
    }

    private void InvalidateScroll()
    {
        CoerceOffset();
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    // ───────────── 그리기 ─────────────

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? Columns.Sum(c => c.Width) : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? Math.Min(Count * RowHeight + HeaderHeight, 600) : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport = new Size(finalSize.Width, Math.Max(0, finalSize.Height - HeaderHeight));
        if (FillFirstColumn && Columns.Count > 1 && !_userResizedFirst)
        {
            // 이름 열이 남는 너비를 채운다 (좁은 패널에서 가로 스크롤 없이)
            var others = Columns.Skip(1).Sum(c => c.Width);
            var w = Math.Max(120, finalSize.Width - others - 2);
            if (Math.Abs(Columns[0].Width - w) > 0.5)
            {
                Columns[0].Width = w;
                _textCache.Clear();
                _headerCache = null;
            }
        }

        InvalidateScroll();
        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        _renderWatch.Restart();
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));

        var rh = RowHeight;
        var list = ItemsSource;
        var count = Count;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var x0 = -_offset.X;

        if (list is not null && count > 0)
        {
            var first = (int)(_offset.Y / rh);
            var yStart = HeaderHeight - (_offset.Y - first * rh);
            var visible = (int)Math.Ceiling(_viewport.Height / rh) + 1;
            var last = Math.Min(count - 1, first + visible);

            dc.PushClip(new RectangleGeometry(new Rect(0, HeaderHeight, w, Math.Max(0, h - HeaderHeight))));
            for (var i = first; i <= last; i++)
            {
                var y = Math.Round(yStart + (i - first) * rh);
                var rowRect = new Rect(0, y, w, rh);
                var selected = _selected.Length > i && _selected[i];
                if (selected)
                {
                    dc.DrawRoundedRectangle(SelectionBrush, null, Inset(rowRect), 4, 4);
                }
                else if (i == _hover)
                {
                    dc.DrawRoundedRectangle(HoverBrush, null, Inset(rowRect), 4, 4);
                }

                if (i == _focus && IsKeyboardFocused)
                {
                    dc.DrawRoundedRectangle(null, new Pen(FocusBrush, 1), Inset(rowRect, 0.5), 4, 4);
                }

                var texts = GetRowText(list, i, selected, dpi);
                var item = list[i]!;
                var dim = IsDimmed?.Invoke(item) == true;
                if (dim)
                {
                    dc.PushOpacity(0.55);
                }

                var x = x0;
                for (var c = 0; c < Columns.Count; c++)
                {
                    var col = Columns[c];
                    var ft = texts[c];
                    var tx = col.AlignRight ? x + col.Width - CellPadding - ft.Width : x + CellPadding;
                    if (c == 0 && IconSelector is not null)
                    {
                        if (IconSelector(item) is { } icon)
                        {
                            dc.DrawImage(icon, new Rect(Math.Round(x + CellPadding), Math.Round(y + (rh - IconSize) / 2), IconSize, IconSize));
                        }

                        tx += IconSize + 6;
                    }

                    dc.DrawText(ft, new Point(Math.Round(tx), Math.Round(y + (rh - ft.Height) / 2)));
                    var after = tx + ft.Width + 8;
                    if (c == 0 && BadgeSelector?.Invoke(item) is { } badge)
                    {
                        var bt = new FormattedText(badge.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, FontSize - 1.5, badge.Brush, dpi);
                        var bx = Math.Min(after, x + col.Width - bt.Width - 6);
                        dc.DrawText(bt, new Point(Math.Round(bx), Math.Round(y + (rh - bt.Height) / 2)));
                        after = bx + bt.Width + 8;
                    }

                    if (c == 0 && ChipSelector?.Invoke(item) is { Count: > 0 } chips)
                    {
                        foreach (var (text, brush) in chips)
                        {
                            var ct = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, FontSize - 2, Brushes.White, dpi);
                            var cw = ct.Width + 10;
                            var chh = Math.Min(rh - 6, ct.Height + 2);
                            if (after + cw > x + col.Width - 4)
                            {
                                break; // 자리가 없으면 그리지 않는다 (이름이 먼저)
                            }

                            var top = y + (rh - chh) / 2;
                            dc.DrawRoundedRectangle(brush, null, new Rect(Math.Round(after), Math.Round(top), Math.Round(cw), Math.Round(chh)), chh / 2, chh / 2);
                            dc.DrawText(ct, new Point(Math.Round(after + 5), Math.Round(top + (chh - ct.Height) / 2)));
                            after += cw + 4;
                        }
                    }

                    x += col.Width;
                }

                if (dim)
                {
                    dc.Pop();
                }
            }

            dc.Pop();
        }

        DrawHeader(dc, w, dpi, x0);
        _renderWatch.Stop();
        _renderTotalMs += _renderWatch.Elapsed.TotalMilliseconds;
        _renderCount++;
    }

    private static Rect Inset(Rect r, double d = 1) => new(r.X + 4 + d - 1, r.Y + d, Math.Max(0, r.Width - 8 - 2 * (d - 1)), Math.Max(0, r.Height - 2 * d));

    private void DrawHeader(DrawingContext dc, double w, double dpi, double x0)
    {
        var hh = HeaderHeight;
        dc.DrawRectangle(HeaderBackground, null, new Rect(0, 0, w, hh));
        dc.DrawRectangle(GridLineBrush, null, new Rect(0, hh - 1, w, 1));
        var x = x0;
        var tf = Typeface;
        if (_headerCache is null || _headerCache.Length != Columns.Count)
        {
            _headerCache = [.. Columns.Select(col => new FormattedText(col.Header, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, FontSize, HeaderForeground, dpi)
            {
                MaxTextWidth = Math.Max(1, col.Width - 2 * CellPadding),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            })];
        }

        for (var c = 0; c < Columns.Count; c++)
        {
            var col = Columns[c];
            var ft = _headerCache[c];
            var tx = col.AlignRight ? x + col.Width - CellPadding - ft.Width : x + CellPadding;
            if (c == 0 && IconSelector is not null)
            {
                tx += IconSize + 6;
            }

            dc.DrawText(ft, new Point(Math.Round(tx), Math.Round((hh - ft.Height) / 2)));
            if (c == SortColumn)
            {
                // 정렬 방향 표시: 작은 꺾쇠
                var ax = col.AlignRight ? tx - 12 : tx + ft.Width + 6;
                var ay = hh / 2;
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    var d = SortDescending ? 1.5 : -1.5;
                    gc.BeginFigure(new Point(ax, ay - d), false, false);
                    gc.LineTo(new Point(ax + 3.5, ay + d), true, false);
                    gc.LineTo(new Point(ax + 7, ay - d), true, false);
                }

                g.Freeze();
                dc.DrawGeometry(null, new Pen(HeaderForeground, 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
            }

            x += col.Width;
            dc.DrawRectangle(GridLineBrush, null, new Rect(Math.Round(x) - 1, 6, 1, hh - 12));
        }
    }

    private Typeface Typeface => _typeface ??= new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private FormattedText[] GetRowText(IList list, int index, bool selected, double dpi)
    {
        if (!_textCache.TryGetValue(index, out var rt))
        {
            if (_textCache.Count >= TextCacheLimit)
            {
                _textCache.Clear(); // 단순 전체 비우기: 화면 몇 장 분량만 다시 만들면 된다
            }

            rt = new RowText();
            _textCache[index] = rt;
        }

        ref var slot = ref selected ? ref rt.Selected : ref rt.Normal;
        if (slot is null)
        {
            var item = list[index]!;
            slot = new FormattedText[Columns.Count];
            var tf = Typeface;
            var fs = FontSize;
            for (var c = 0; c < Columns.Count; c++)
            {
                var col = Columns[c];
                var brush = selected ? SelectionForeground : c == 0 ? Foreground : SecondaryForeground;
                var iconSpace = c == 0 && IconSelector is not null ? IconSize + 6 : 0;
                slot[c] = new FormattedText(col.Text(item), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, fs * col.FontScale, brush, dpi)
                {
                    MaxTextWidth = Math.Max(1, col.Width - 2 * CellPadding - iconSpace),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
            }
        }

        return slot;
    }

    private sealed class RowText
    {
        public FormattedText[]? Normal;
        public FormattedText[]? Selected;
    }

    // ───────────── 입력 ─────────────

    private int IndexAt(Point p)
    {
        if (p.Y < HeaderHeight)
        {
            return -1;
        }

        var i = (int)((p.Y - HeaderHeight + _offset.Y) / RowHeight);
        return i >= 0 && i < Count ? i : -1;
    }

    /// <summary>머리글에서 열 경계(±4px) 위면 그 열 번호, 아니면 -1.</summary>
    private int ColumnEdgeAt(double x)
    {
        var edge = -_offset.X;
        for (var c = 0; c < Columns.Count; c++)
        {
            edge += Columns[c].Width;
            if (Math.Abs(x - edge) <= 4)
            {
                return c;
            }
        }

        return -1;
    }

    private int ColumnAt(double x)
    {
        var left = -_offset.X;
        for (var c = 0; c < Columns.Count; c++)
        {
            if (x >= left && x < left + Columns[c].Width)
            {
                return c;
            }

            left += Columns[c].Width;
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);

        if (_resizeColumn >= 0)
        {
            Columns[_resizeColumn].Width = Math.Max(48, _resizeStartWidth + p.X - _resizeStartX);
            _userResizedFirst |= _resizeColumn == 0;
            _textCache.Clear();
            _headerCache = null;
            InvalidateScroll();
            return;
        }

        Cursor = p.Y < HeaderHeight && ColumnEdgeAt(p.X) >= 0 ? Cursors.SizeWE : null;

        if (_dragCandidate >= 0 && e.LeftButton == MouseButtonState.Pressed &&
            (Math.Abs(p.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(p.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            _dragCandidate = -1;
            _pendingClick = -1;
            DragRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var i = IndexAt(p);
        if (i != _hover)
        {
            _hover = i;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_resizeColumn >= 0)
        {
            _resizeColumn = -1;
            ReleaseMouseCapture();
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_pendingClick >= 0)
        {
            // 이미 선택된 항목을 눌렀다 뗐으면(끌지 않았으면) 그 항목만 선택
            SelectWithModifiers(_pendingClick, ModifierKeys.None);
        }

        _pendingClick = -1;
        _dragCandidate = -1;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        var i = IndexAt(e.GetPosition(this));
        if (i >= 0 && !IsSelected(i))
        {
            SelectWithModifiers(i, ModifierKeys.None);
        }
        else if (i < 0 && e.GetPosition(this).Y >= HeaderHeight)
        {
            ClearSelection();
        }

        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var p = e.GetPosition(this);
        if (p.Y >= HeaderHeight)
        {
            ContextRequested?.Invoke(this, new FileListContextEventArgs(IndexAt(p), PointToScreen(p)));
        }

        e.Handled = true;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text) && !char.IsControl(e.Text[0]) && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            TypeAhead?.Invoke(this, e.Text);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1)
        {
            _hover = -1;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var pos = e.GetPosition(this);
        if (pos.Y < HeaderHeight)
        {
            var edge = ColumnEdgeAt(pos.X);
            if (edge >= 0)
            {
                _resizeColumn = edge;
                _resizeStartX = pos.X;
                _resizeStartWidth = Columns[edge].Width;
                CaptureMouse();
            }
            else if (ColumnAt(pos.X) is var col and >= 0)
            {
                HeaderClicked?.Invoke(this, col);
            }

            e.Handled = true;
            return;
        }

        var i = IndexAt(pos);
        if (e.ClickCount == 2 && i >= 0)
        {
            ItemActivated?.Invoke(this, i);
            e.Handled = true;
            return;
        }

        if (i < 0)
        {
            if (e.GetPosition(this).Y >= HeaderHeight)
            {
                ClearSelection();
            }

            return;
        }

        _dragStart = pos;
        _dragCandidate = i;
        if (IsSelected(i) && Keyboard.Modifiers == ModifierKeys.None)
        {
            _pendingClick = i; // 여러 개를 끌 수 있게 선택 변경은 버튼을 뗄 때
            _focus = i;
        }
        else
        {
            SelectWithModifiers(i, Keyboard.Modifiers);
        }

        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var n = Count;
        if (n == 0)
        {
            return;
        }

        var page = Math.Max(1, (int)(_viewport.Height / RowHeight) - 1);
        var cur = _focus < 0 ? 0 : _focus;
        int? target = e.Key switch
        {
            Key.Down => Math.Min(n - 1, _focus < 0 ? 0 : cur + 1),
            Key.Up => Math.Max(0, cur - 1),
            Key.PageDown => Math.Min(n - 1, cur + page),
            Key.PageUp => Math.Max(0, cur - page),
            Key.Home => 0,
            Key.End => n - 1,
            _ => null,
        };

        if (target is { } t)
        {
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Control) != 0 && (mods & ModifierKeys.Shift) == 0)
            {
                _focus = t; // Ctrl+이동: 선택은 그대로, 포커스만
                InvalidateVisual();
            }
            else
            {
                SelectWithModifiers(t, mods & ModifierKeys.Shift);
            }

            ScrollIntoView(t);
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control && _focus >= 0)
        {
            SelectWithModifiers(_focus, ModifierKeys.Control);
            e.Handled = true;
        }
        else if ((e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)) && _focus >= 0)
        {
            var r = RowRect(_focus);
            ContextRequested?.Invoke(this, new FileListContextEventArgs(_focus, PointToScreen(new Point(r.X + 40, r.Bottom))));
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _focus >= 0)
        {
            ItemActivated?.Invoke(this, _focus);
            e.Handled = true;
        }
    }

    private void SelectWithModifiers(int i, ModifierKeys mods)
    {
        if ((mods & ModifierKeys.Shift) != 0 && _anchor >= 0)
        {
            if ((mods & ModifierKeys.Control) == 0)
            {
                _selected.SetAll(false);
            }

            var (a, b) = _anchor <= i ? (_anchor, i) : (i, _anchor);
            for (var k = a; k <= b; k++)
            {
                _selected[k] = true;
            }
        }
        else if ((mods & ModifierKeys.Control) != 0)
        {
            _selected[i] = !_selected[i];
            _anchor = i;
        }
        else
        {
            _selected.SetAll(false);
            _selected[i] = true;
            _anchor = i;
        }

        _focus = i;
        RecountSelection();
    }

    /// <summary>화면 캡처·UI 테스트용: 주어진 행들을 선택하고 첫 행에 포커스를 둔다.</summary>
    public void SelectWithModifiersForDemo(IReadOnlyList<int> indices)
    {
        _selected.SetAll(false);
        foreach (var i in indices.Where(i => i >= 0 && i < _selected.Length))
        {
            _selected[i] = true;
        }

        _focus = _anchor = indices.Count > 0 ? indices[0] : -1;
        _hover = indices.Count > 0 ? indices[^1] + 2 : -1;
        RecountSelection();
    }

    public void SelectAll()
    {
        _selected.SetAll(true);
        RecountSelection();
    }

    public void ClearSelection()
    {
        _selected.SetAll(false);
        RecountSelection();
    }

    private void RecountSelection()
    {
        var c = 0;
        var n = Math.Min(_selected.Length, Count);
        for (var k = 0; k < n; k++)
        {
            if (_selected[k])
            {
                c++;
            }
        }

        _selectedCount = c;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        AnnounceFocus();
    }

    public void ScrollIntoView(int index)
    {
        var top = index * RowHeight;
        if (top < _offset.Y)
        {
            SetVerticalOffset(top);
        }
        else if (top + RowHeight > _offset.Y + _viewport.Height)
        {
            SetVerticalOffset(top + RowHeight - _viewport.Height);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ListPeer(this);

    /// <summary>
    /// 화면 읽기 지원: 행마다 자동화 피어를 만들지 않는 대신(M0 성능 결과), 포커스 행을 목록의 이름으로 알리고
    /// 라이브 영역 변경 이벤트를 보낸다. 내레이터 등은 "이름, 유형, 크기, 3/120, 선택됨"처럼 읽는다.
    /// </summary>
    private void AnnounceFocus()
    {
        if (ItemsSource is not { } list || _focus < 0 || _focus >= list.Count || list[_focus] is not { } item)
        {
            return;
        }

        var parts = Columns.Select(c => c.Text(item)).Where(t => t.Length > 0);
        var text = $"{string.Join(", ", parts)}, {_focus + 1}/{list.Count}{(IsSelected(_focus) ? ", 선택됨" : "")}";
        System.Windows.Automation.AutomationProperties.SetName(this, text);
        if (UIElementAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private sealed class ListPeer(FastFileList owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        protected override string GetClassNameCore() => nameof(FastFileList);

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }

    // ───────────── IScrollInfo ─────────────

    public bool CanVerticallyScroll { get; set; } = true;

    public bool CanHorizontallyScroll { get; set; } = true;

    public double ExtentWidth => Math.Max(Columns.Sum(c => c.Width), _viewport.Width);

    public double ExtentHeight => Count * RowHeight;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => _offset.X;

    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset.Y - RowHeight);

    public void LineDown() => SetVerticalOffset(_offset.Y + RowHeight);

    public void LineLeft() => SetHorizontalOffset(_offset.X - 16);

    public void LineRight() => SetHorizontalOffset(_offset.X + 16);

    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);

    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);

    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);

    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - 3 * RowHeight);

    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + 3 * RowHeight);

    public void MouseWheelLeft() => LineLeft();

    public void MouseWheelRight() => LineRight();

    public void SetHorizontalOffset(double offset)
    {
        _offset.X = offset;
        InvalidateScroll();
    }

    public void SetVerticalOffset(double offset)
    {
        _offset.Y = offset;
        InvalidateScroll();
    }

    private void CoerceOffset()
    {
        _offset.Y = Math.Max(0, Math.Min(_offset.Y, ExtentHeight - _viewport.Height));
        _offset.X = Math.Max(0, Math.Min(_offset.X, ExtentWidth - _viewport.Width));
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
