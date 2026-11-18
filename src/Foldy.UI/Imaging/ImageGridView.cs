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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Foldy.Platform.Imaging;

namespace Foldy.UI.Imaging;

/// <summary>
/// 직접 그리는 가상화 썸네일 격자 (설계서 5.12의 ImageGridView). FastFileList와 같은 방식:
/// <list type="bullet">
/// <item>보이는 칸만 DrawingContext로 그린다. 컨테이너·바인딩·템플릿이 없다.</item>
/// <item>IScrollInfo를 직접 구현해 ScrollViewer 안에서 픽셀 단위로 스크롤한다.</item>
/// <item>보이는 칸과 다음 한 화면분만 썸네일을 요청하고, 지나간 요청은 취소한다.</item>
/// <item>선택은 비트 배열 (Ctrl·Shift 클릭, 방향키, Ctrl+A).</item>
/// </list>
/// 호스트는 ItemsSource(ItemList 등 IList)를 넣고, 필요하면 ItemInfoSelector로 항목 → 경로 변환을 바꾼다.
/// 스레드 소유권: UI 스레드 전용.
/// </summary>
public sealed class ImageGridView : FrameworkElement, IScrollInfo
{
    /// <summary>작게 / 보통 / 크게 (설계서 5.12).</summary>
    public static readonly IReadOnlyList<int> SizePresets = [96, 160, 256];

    private const int TextCacheLimit = 2048;

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IList), typeof(ImageGridView),
        new FrameworkPropertyMetadata(null, (d, e) => ((ImageGridView)d).OnItemsSourceChanged((IList?)e.NewValue)));

    public static readonly DependencyProperty ThumbnailSizeProperty = DependencyProperty.Register(
        nameof(ThumbnailSize), typeof(double), typeof(ImageGridView),
        new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => ((ImageGridView)d).OnThumbnailSizeChanged((double)e.OldValue)),
        v => v is double s && s >= 32 && s <= 1024);

    public static readonly DependencyProperty AllowContentDecodeProperty = DependencyProperty.Register(
        nameof(AllowContentDecode), typeof(bool), typeof(ImageGridView),
        new FrameworkPropertyMetadata(true, (d, _) => ((ImageGridView)d).ResetThumbnails()));

    public static readonly DependencyProperty BackgroundProperty = Panel.BackgroundProperty.AddOwner(
        typeof(ImageGridView), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(ImageGridView), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits, (d, _) => ((ImageGridView)d).ResetText()));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(ImageGridView), new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits, (d, _) => ((ImageGridView)d).ResetText()));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(ImageGridView), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((ImageGridView)d).ResetText()));

    public static readonly DependencyProperty SecondaryForegroundProperty = Register<Brush>(nameof(SecondaryForeground), Brushes.Gray, resetText: true);
    public static readonly DependencyProperty SelectionBrushProperty = Register<Brush>(nameof(SelectionBrush), Brushes.LightBlue);
    public static readonly DependencyProperty SelectionForegroundProperty = Register<Brush>(nameof(SelectionForeground), Brushes.Black, resetText: true);
    public static readonly DependencyProperty HoverBrushProperty = Register<Brush>(nameof(HoverBrush), Brushes.WhiteSmoke);
    public static readonly DependencyProperty FocusBrushProperty = Register<Brush>(nameof(FocusBrush), Brushes.DodgerBlue);
    public static readonly DependencyProperty PlaceholderBrushProperty = Register<Brush>(nameof(PlaceholderBrush), Brushes.Gainsboro);

    private readonly Dictionary<int, Slot> _slots = [];
    private readonly Dictionary<int, LabelText> _labels = [];
    private readonly Stopwatch _renderWatch = new();
    private INotifyCollectionChanged? _observed;
    private System.Collections.BitArray _selected = new(0);
    private int _selectedCount;
    private int _anchor = -1;
    private int _focus = -1;
    private int _hover = -1;
    private Vector _offset;
    private Size _viewport;
    private Typeface? _typeface;
    private double _renderTotalMs;
    private int _renderCount;
    private bool _requestsPending;

    public ImageGridView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

        // 테마 토큰 (호스트가 SetValue/SetResourceReference로 바꿀 수 있다)
        SetResourceReference(BackgroundProperty, "Brush.Window");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(SecondaryForegroundProperty, "Brush.TextSecondary");
        SetResourceReference(SelectionBrushProperty, "Brush.Selection");
        SetResourceReference(SelectionForegroundProperty, "Brush.SelectionText");
        SetResourceReference(HoverBrushProperty, "Brush.Hover");
        SetResourceReference(FocusBrushProperty, "Brush.Focus");
        SetResourceReference(PlaceholderBrushProperty, "Brush.SurfaceAlt");
        SetResourceReference(FontFamilyProperty, "Font.UI");
        SetResourceReference(FontSizeProperty, "Font.Size");

        Unloaded += (_, _) => CancelAll();
    }

    /// <summary>선택이 바뀌었다.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>포커스 항목이 바뀌었다 (미리보기 창 갱신용). 인자는 새 인덱스.</summary>
    public event EventHandler<int>? FocusedIndexChanged;

    /// <summary>더블클릭 또는 Enter.</summary>
    public event EventHandler<int>? ItemActivated;

    /// <summary>스페이스: 빠른 보기를 열어 달라.</summary>
    public event EventHandler<int>? QuickLookRequested;

    /// <summary>F3: 미리보기 창을 열거나 닫아 달라.</summary>
    public event EventHandler<int>? PreviewRequested;

    /// <summary>Ctrl+휠 등으로 썸네일 크기가 바뀌었다 (탭별 설정 저장용).</summary>
    public event EventHandler<double>? ThumbnailSizeChanged;

    public IList? ItemsSource { get => (IList?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    /// <summary>썸네일 정사각형 한 변 (DIP). 96 / 160 / 256 권장.</summary>
    public double ThumbnailSize { get => (double)GetValue(ThumbnailSizeProperty); set => SetValue(ThumbnailSizeProperty, value); }

    /// <summary>false면 Windows 셸 캐시에 있는 썸네일만 쓴다 (네트워크 드라이브).</summary>
    public bool AllowContentDecode { get => (bool)GetValue(AllowContentDecodeProperty); set => SetValue(AllowContentDecodeProperty, value); }

    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    public Brush SecondaryForeground { get => (Brush)GetValue(SecondaryForegroundProperty); set => SetValue(SecondaryForegroundProperty, value); }

    public Brush SelectionBrush { get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }

    public Brush SelectionForeground { get => (Brush)GetValue(SelectionForegroundProperty); set => SetValue(SelectionForegroundProperty, value); }

    public Brush HoverBrush { get => (Brush)GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }

    public Brush FocusBrush { get => (Brush)GetValue(FocusBrushProperty); set => SetValue(FocusBrushProperty, value); }

    public Brush PlaceholderBrush { get => (Brush)GetValue(PlaceholderBrushProperty); set => SetValue(PlaceholderBrushProperty, value); }

    /// <summary>항목 → 경로·수정 시각 변환. null이면 <see cref="ImageItemAdapter.Default"/>.</summary>
    public Func<object, ImageItemInfo>? ItemInfoSelector { get; set; }

    /// <summary>썸네일 공급자. null이면 <see cref="ThumbnailLoader.Shared"/>.</summary>
    public ThumbnailLoader? Loader { get; set; }

    public int Count => ItemsSource?.Count ?? 0;

    public int SelectedCount => _selectedCount;

    public int FocusedIndex => _focus;

    /// <summary>현재 배치 (테스트·호스트 계산용).</summary>
    public GridLayout Layout => new(_viewport.Width > 0 ? _viewport.Width : ActualWidth, ThumbnailSize, Count, LabelHeight);

    /// <summary>OnRender 평균 시간(ms). 벤치 진단용.</summary>
    public double AverageRenderMs => _renderCount == 0 ? 0 : _renderTotalMs / _renderCount;

    public void ResetRenderStats() => (_renderTotalMs, _renderCount) = (0, 0);

    private ThumbnailLoader ActiveLoader => Loader ?? ThumbnailLoader.Shared;

    private double LabelHeight => Math.Ceiling(FontSize * 1.4);

    private Typeface Typeface => _typeface ??= new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

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

    public ImageItemInfo InfoAt(int index) => (ItemInfoSelector ?? ImageItemAdapter.Default)(ItemsSource![index]!);

    private static DependencyProperty Register<T>(string name, T def, bool resetText = false) =>
        DependencyProperty.Register(name, typeof(T), typeof(ImageGridView), new FrameworkPropertyMetadata(
            def,
            FrameworkPropertyMetadataOptions.AffectsRender,
            resetText ? (d, _) => ((ImageGridView)d).ResetText() : null));

    // ───────────── 데이터 ─────────────

    private void OnItemsSourceChanged(IList? newList)
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

        CancelAll();
        _labels.Clear();
        _selected = new System.Collections.BitArray(newList?.Count ?? 0);
        _selectedCount = 0;
        _anchor = _focus = _hover = -1;
        _offset = default;
        InvalidateScroll();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // ItemList는 뒤에 붙이기만 한다: 줄면 처음부터, 늘면 선택·썸네일 유지 (칸마다 키를 다시 확인한다)
        var n = Count;
        if (n < _selected.Length)
        {
            CancelAll();
            _labels.Clear();
            _selected = new System.Collections.BitArray(n);
            _selectedCount = 0;
            _anchor = _focus = _hover = -1;
        }
        else if (n > _selected.Length)
        {
            _selected.Length = n;
        }

        InvalidateScroll();
    }

    private void OnThumbnailSizeChanged(double oldSize)
    {
        // 맨 위 항목을 기준으로 스크롤 위치를 다시 잡는다
        var oldLayout = new GridLayout(_viewport.Width, oldSize, Count, LabelHeight);
        var top = oldLayout.VisibleRange(_offset.Y, _viewport.Height).First;
        _labels.Clear();
        CancelAll(keepImages: true);
        var layout = Layout;
        _offset.Y = top > 0 ? layout.CellRect(top).Y - GridLayout.Margin : 0;
        if (_focus >= 0)
        {
            ScrollIntoView(_focus);
        }

        InvalidateScroll();
        ThumbnailSizeChanged?.Invoke(this, ThumbnailSize);
    }

    private void ResetText()
    {
        _labels.Clear();
        _typeface = null;
        InvalidateVisual();
    }

    /// <summary>썸네일을 모두 다시 요청한다 (드라이브 설정이 바뀌었을 때 등).</summary>
    public void ResetThumbnails()
    {
        CancelAll();
        InvalidateVisual();
    }

    /// <summary>특정 파일이 바뀌었을 때 그 썸네일만 다시 만든다.</summary>
    public void InvalidateThumbnail(string path)
    {
        ActiveLoader.Invalidate(path);
        foreach (var (i, slot) in _slots.Where(kv => string.Equals(kv.Value.Key.Path, path, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            slot.Cts?.Cancel();
            _slots.Remove(i);
        }

        InvalidateVisual();
    }

    private void InvalidateScroll()
    {
        CoerceOffset();
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    // ───────────── 썸네일 요청 ─────────────

    private void CancelAll(bool keepImages = false)
    {
        foreach (var s in _slots.Values)
        {
            s.Cts?.Cancel();
            s.Cts = null;
        }

        if (keepImages)
        {
            // 크기만 바뀐 경우: 이전 이미지를 늘려 그리면서 새 크기를 요청한다
            foreach (var s in _slots.Values)
            {
                s.IsFinal = false;
                s.Failed = false;
            }
        }
        else
        {
            _slots.Clear();
        }
    }

    private int PixelSize(double dpi) => (int)Math.Round(ThumbnailSize * dpi);

    /// <summary>칸의 슬롯을 돌려준다. 항목이 바뀌었으면(키 불일치) 새로 만들고 메모리 캐시를 바로 확인한다.</summary>
    private Slot GetSlot(int index, ImageItemInfo info, int pixelSize)
    {
        var key = info.KeyFor(pixelSize);
        if (_slots.TryGetValue(index, out var slot))
        {
            if (slot.Key.Equals(key))
            {
                return slot;
            }

            if (slot.Key.Equals(key with { PixelSize = slot.Key.PixelSize }))
            {
                // 같은 파일, 크기만 다름: 이미지는 임시로 유지
                slot.Cts?.Cancel();
                slot.Cts = null;
                slot.Key = key;
                slot.IsFinal = false;
                slot.Failed = false;
                if (ActiveLoader.TryGetCached(key) is { } same)
                {
                    (slot.Image, slot.IsFinal) = (same, true);
                }

                return slot;
            }

            slot.Cts?.Cancel();
        }

        slot = new Slot { Key = key };
        if (ActiveLoader.TryGetCached(key) is { } cached)
        {
            slot.Image = cached;
            slot.IsFinal = true;
        }
        else
        {
            slot.Image = ActiveLoader.Memory.FindAnySize(key, SizePresets.Select(s => (int)Math.Round(s * (pixelSize / ThumbnailSize))));
        }

        _slots[index] = slot;
        return slot;
    }

    /// <summary>
    /// 렌더 직후 한 번: 보이는 칸 + 다음 한 화면분을 요청하고, 범위 밖 요청은 취소한다.
    /// 서비스 대기열이 LIFO이므로 먼 것부터 넣어 화면 왼쪽 위가 가장 먼저 처리되게 한다.
    /// </summary>
    private void ScheduleRequests()
    {
        _requestsPending = false;
        var list = ItemsSource;
        if (list is null || Count == 0 || !IsLoaded && !IsVisible)
        {
            return;
        }

        var layout = Layout;
        var (first, last) = layout.VisibleRange(_offset.Y, _viewport.Height);
        if (last < first)
        {
            return;
        }

        var span = last - first + 1;
        var keepFrom = Math.Max(0, first - span);
        var keepTo = Math.Min(Count - 1, last + span);
        foreach (var i in _slots.Keys.Where(i => i < keepFrom || i > keepTo).ToList())
        {
            _slots[i].Cts?.Cancel();
            _slots.Remove(i);
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var px = PixelSize(dpi);
        var loader = ActiveLoader;
        var allowRead = AllowContentDecode;
        var adapter = ItemInfoSelector ?? ImageItemAdapter.Default;

        // 먼 것부터: 다음 화면(아래에서 위로) → 보이는 칸(아래에서 위로)
        for (var i = keepTo; i >= first; i--)
        {
            if (list[i] is not { } item)
            {
                continue;
            }

            var info = adapter(item);
            if (!ImageFormats.IsImage(info.Name))
            {
                continue;
            }

            var slot = GetSlot(i, info, px);
            if (slot.IsFinal || slot.Failed || slot.Cts is not null)
            {
                continue;
            }

            _ = LoadSlotAsync(i, slot, info, px, loader, allowRead);
        }
    }

    private async Task LoadSlotAsync(int index, Slot slot, ImageItemInfo info, int px, ThumbnailLoader loader, bool allowRead)
    {
        using var cts = new CancellationTokenSource();
        slot.Cts = cts;
        var ct = cts.Token;
        var dispatcher = Dispatcher;
        try
        {
            var bmp = await loader.GetThumbnailAsync(info, px, allowRead, prelim => dispatcher.InvokeAsync(() =>
            {
                if (!ct.IsCancellationRequested && !slot.IsFinal)
                {
                    slot.Image = prelim;
                    InvalidateVisual();
                }
            }), ct);

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (bmp is null)
            {
                slot.Failed = true;
            }
            else
            {
                slot.Image = bmp;
                slot.IsFinal = true;
            }

            if (_slots.TryGetValue(index, out var current) && ReferenceEquals(current, slot))
            {
                InvalidateVisual();
            }
        }
        catch (OperationCanceledException)
        {
            // 스크롤로 지나간 요청
        }
        finally
        {
            if (ReferenceEquals(slot.Cts, cts))
            {
                slot.Cts = null;
            }
        }
    }

    // ───────────── 그리기 ─────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? new GridLayout(800, ThumbnailSize, Count, LabelHeight).ExtentWidth : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? Math.Min(new GridLayout(w, ThumbnailSize, Count, LabelHeight).ExtentHeight, 600) : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport = finalSize;
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

        var list = ItemsSource;
        if (list is not null && Count > 0)
        {
            var layout = Layout;
            var (first, last) = layout.VisibleRange(_offset.Y, _viewport.Height);
            var dpi = VisualTreeHelper.GetDpi(this);
            var px = PixelSize(dpi.PixelsPerDip);
            var adapter = ItemInfoSelector ?? ImageItemAdapter.Default;
            var oy = -_offset.Y;
            var focusPen = new Pen(FocusBrush, 1.5);
            for (var i = first; i <= last; i++)
            {
                if (list[i] is not { } item)
                {
                    continue;
                }

                var info = adapter(item);
                var cell = layout.CellRect(i);
                cell.Offset(0, oy);
                var selected = IsSelected(i);
                if (selected)
                {
                    dc.DrawRoundedRectangle(SelectionBrush, null, cell, 6, 6);
                }
                else if (i == _hover)
                {
                    dc.DrawRoundedRectangle(HoverBrush, null, cell, 6, 6);
                }

                if (i == _focus && IsKeyboardFocused)
                {
                    dc.DrawRoundedRectangle(null, focusPen, Inflate(cell, -0.75), 6, 6);
                }

                var thumb = layout.ThumbnailRect(i);
                thumb.Offset(0, oy);
                var isImage = ImageFormats.IsImage(info.Name);
                var slot = isImage ? GetSlot(i, info, px) : null;
                if (slot?.Image is { } img)
                {
                    DrawFitted(dc, img, thumb, slot.IsFinal, dpi.PixelsPerDip);
                }
                else
                {
                    DrawPlaceholder(dc, thumb, info, slot?.Failed == true || !isImage, dpi.PixelsPerDip);
                }

                var label = layout.LabelRect(i);
                label.Offset(0, oy);
                var ft = GetLabel(i, info, selected, layout.ThumbnailSize, dpi.PixelsPerDip);
                dc.DrawText(ft, new Point(Math.Round(label.X), Math.Round(label.Y + (label.Height - ft.Height) / 2)));
            }
        }

        if (!_requestsPending)
        {
            _requestsPending = true;
            Dispatcher.InvokeAsync(ScheduleRequests, DispatcherPriority.Background);
        }

        _renderWatch.Stop();
        _renderTotalMs += _renderWatch.Elapsed.TotalMilliseconds;
        _renderCount++;
    }

    private static Rect Inflate(Rect r, double d) => new(r.X - d, r.Y - d, Math.Max(0, r.Width + 2 * d), Math.Max(0, r.Height + 2 * d));

    /// <summary>비율을 지켜 정사각형 안 가운데에 그린다. 최종 이미지는 원래 크기보다 키우지 않는다.</summary>
    private static void DrawFitted(DrawingContext dc, BitmapSource img, Rect box, bool final, double pixelsPerDip)
    {
        var natW = img.PixelWidth / pixelsPerDip;
        var natH = img.PixelHeight / pixelsPerDip;
        if (natW <= 0 || natH <= 0)
        {
            return;
        }

        var scale = Math.Min(box.Width / natW, box.Height / natH);
        if (final)
        {
            scale = Math.Min(scale, 1);
        }

        var dw = natW * scale;
        var dh = natH * scale;
        var r = new Rect(Math.Round(box.X + (box.Width - dw) / 2), Math.Round(box.Y + (box.Height - dh) / 2), Math.Round(dw), Math.Round(dh));
        dc.DrawImage(img, r);
    }

    private void DrawPlaceholder(DrawingContext dc, Rect box, ImageItemInfo info, bool failed, double pixelsPerDip)
    {
        var inner = Inflate(box, -box.Width * 0.12);
        dc.DrawRoundedRectangle(PlaceholderBrush, null, inner, 6, 6);
        var ext = info.Extension.TrimStart('.').ToUpperInvariant();
        var text = failed && ImageFormats.NeedsCodec(info.Name) ? ext + "\n코덱 필요" : ext;
        if (text.Length == 0)
        {
            return;
        }

        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface, FontSize, SecondaryForeground, pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, inner.Width - 8),
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, new Point(inner.X + 4, Math.Round(inner.Y + (inner.Height - ft.Height) / 2)));
    }

    private FormattedText GetLabel(int index, ImageItemInfo info, bool selected, double width, double dpi)
    {
        if (!_labels.TryGetValue(index, out var lt) || !string.Equals(lt.Name, info.Name, StringComparison.Ordinal))
        {
            if (_labels.Count >= TextCacheLimit)
            {
                _labels.Clear();
            }

            lt = new LabelText(info.Name);
            _labels[index] = lt;
        }

        ref var slot = ref selected ? ref lt.Selected : ref lt.Normal;
        slot ??= new FormattedText(info.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface, FontSize, selected ? SelectionForeground : Foreground, dpi)
        {
            MaxTextWidth = Math.Max(1, width),
            MaxLineCount = 1,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return slot;
    }

    private sealed class LabelText(string name)
    {
        public readonly string Name = name;
        public FormattedText? Normal;
        public FormattedText? Selected;
    }

    private sealed class Slot
    {
        public ThumbKey Key;
        public BitmapSource? Image;
        public bool IsFinal;
        public bool Failed;
        public CancellationTokenSource? Cts;
    }

    // ───────────── 입력 ─────────────

    private int IndexAt(Point p) => Layout.IndexAt(new Point(p.X + _offset.X, p.Y + _offset.Y));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var i = IndexAt(e.GetPosition(this));
        if (i != _hover)
        {
            _hover = i;
            InvalidateVisual();
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
        var i = IndexAt(e.GetPosition(this));
        if (e.ClickCount == 2 && i >= 0)
        {
            ItemActivated?.Invoke(this, i);
            e.Handled = true;
            return;
        }

        if (i < 0)
        {
            ClearSelection();
            return;
        }

        SelectWithModifiers(i, Keyboard.Modifiers);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        var i = IndexAt(e.GetPosition(this));
        if (i >= 0 && !IsSelected(i))
        {
            SelectWithModifiers(i, ModifierKeys.None); // 상황 메뉴는 호스트가 ContextMenu로 붙인다
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        var next = StepSize(ThumbnailSize, e.Delta > 0 ? 1 : -1);
        if (next != ThumbnailSize)
        {
            ThumbnailSize = next;
        }

        e.Handled = true; // ScrollViewer가 스크롤하지 않게
    }

    /// <summary>프리셋(96/160/256) 사이를 한 칸 오르내린다.</summary>
    public static double StepSize(double current, int direction)
    {
        if (direction > 0)
        {
            foreach (var s in SizePresets)
            {
                if (s > current + 0.5)
                {
                    return s;
                }
            }

            return current;
        }

        for (var k = SizePresets.Count - 1; k >= 0; k--)
        {
            if (SizePresets[k] < current - 0.5)
            {
                return SizePresets[k];
            }
        }

        return current;
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

        var layout = Layout;
        var page = layout.RowsPerPage(_viewport.Height);
        var cur = _focus;
        int? target = e.Key switch
        {
            Key.Right => layout.Move(cur, 1, 0),
            Key.Left => cur < 0 ? 0 : layout.Move(cur, -1, 0),
            Key.Down => layout.Move(cur, 0, 1),
            Key.Up => cur < 0 ? 0 : layout.Move(cur, 0, -1),
            Key.PageDown => cur < 0 ? 0 : Math.Min(n - 1, cur + page * layout.Columns),
            Key.PageUp => cur < 0 ? 0 : Math.Max(cur % layout.Columns, cur - page * layout.Columns),
            Key.Home => 0,
            Key.End => n - 1,
            _ => null,
        };

        var mods = Keyboard.Modifiers;
        if (target is { } t && t >= 0)
        {
            if ((mods & ModifierKeys.Control) != 0 && (mods & ModifierKeys.Shift) == 0)
            {
                SetFocus(t); // Ctrl+이동: 선택은 그대로, 포커스만
                InvalidateVisual();
            }
            else
            {
                SelectWithModifiers(t, mods & ModifierKeys.Shift);
            }

            ScrollIntoView(t);
            e.Handled = true;
        }
        else if (e.Key == Key.A && mods == ModifierKeys.Control)
        {
            SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && mods == ModifierKeys.Control && _focus >= 0)
        {
            SelectWithModifiers(_focus, ModifierKeys.Control);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && mods == ModifierKeys.None && _focus >= 0)
        {
            QuickLookRequested?.Invoke(this, _focus);
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && _focus >= 0)
        {
            PreviewRequested?.Invoke(this, _focus);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _focus >= 0)
        {
            ItemActivated?.Invoke(this, _focus);
            e.Handled = true;
        }

        // Delete·F2·Ctrl+C 등은 처리하지 않고 호스트로 올려 보낸다 (파일 작업은 호스트 몫)
    }

    private void SetFocus(int i)
    {
        if (_focus != i)
        {
            _focus = i;
            FocusedIndexChanged?.Invoke(this, i);
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

        SetFocus(i);
        RecountSelection();
    }

    /// <summary>항목 하나만 선택하고 포커스·스크롤을 맞춘다 (빠른 보기에서 돌아올 때 등).</summary>
    public void SelectIndex(int index)
    {
        if (index < 0 || index >= Count)
        {
            return;
        }

        SelectWithModifiers(index, ModifierKeys.None);
        ScrollIntoView(index);
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
    }

    public void ScrollIntoView(int index)
    {
        if (index < 0 || index >= Count)
        {
            return;
        }

        var r = Layout.CellRect(index);
        if (r.Top - GridLayout.Gap < _offset.Y)
        {
            SetVerticalOffset(r.Top - GridLayout.Margin);
        }
        else if (r.Bottom + GridLayout.Gap > _offset.Y + _viewport.Height)
        {
            SetVerticalOffset(r.Bottom + GridLayout.Margin - _viewport.Height);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GridPeer(this);

    private sealed class GridPeer(ImageGridView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        protected override string GetClassNameCore() => nameof(ImageGridView);

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }

    // ───────────── IScrollInfo ─────────────

    public bool CanVerticallyScroll { get; set; } = true;

    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth => _viewport.Width;

    public double ExtentHeight => Layout.ExtentHeight;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => 0;

    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    private double WheelStep => Math.Max(48, Layout.RowPitch * 0.6);

    public void LineUp() => SetVerticalOffset(_offset.Y - 32);

    public void LineDown() => SetVerticalOffset(_offset.Y + 32);

    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - WheelStep);

    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + WheelStep);

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
        _offset.Y = offset;
        InvalidateScroll();
    }

    private void CoerceOffset()
    {
        _offset.Y = Math.Max(0, Math.Min(_offset.Y, ExtentHeight - _viewport.Height));
        _offset.X = 0;
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
