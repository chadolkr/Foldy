using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Foldy.UI.Imaging;

/// <summary>빠른 보기에서 호스트에 넘기는 파일 작업.</summary>
public enum QuickLookCommand
{
    /// <summary>Delete: 휴지통으로.</summary>
    Delete,

    /// <summary>Shift+Delete: 영구 삭제 (호스트가 확인을 받는다).</summary>
    DeletePermanently,

    /// <summary>F2: 이름 바꾸기.</summary>
    Rename,

    /// <summary>Ctrl+C</summary>
    Copy,

    /// <summary>Ctrl+X</summary>
    Cut,
}

public sealed class QuickLookCommandEventArgs(QuickLookCommand command, int index, object item) : EventArgs
{
    public QuickLookCommand Command { get; } = command;

    public int Index { get; } = index;

    public object Item { get; } = item;

    /// <summary>호스트가 처리했으면 true.</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// 스페이스 키 빠른 보기 (설계서 5.12): 창 전체 크기, ← → 이전/다음, 휠·더블클릭 확대(맞춤 ↔ 100%),
/// R 화면 회전, Esc·스페이스 닫기. Delete·F2·복사·잘라내기는 <see cref="CommandRequested"/>로 호스트에 넘긴다
/// (파일 작업은 호스트가 IFileOperation 등으로 하고, 목록이 바뀌면 <see cref="UpdateItems"/>를 부른다).
/// 스레드 소유권: UI 스레드 전용.
/// </summary>
public sealed class QuickLookWindow : Window
{
    private readonly ZoomImageView _view = new() { WheelZoom = true };
    private readonly TextBlock _title = new();
    private readonly TextBlock _info = new();
    private readonly ImagePresenter _presenter;
    private IList _items;
    private int _index;

    /// <param name="items">보여 줄 항목 목록 (보통 이미지만 걸러 낸 ItemList).</param>
    /// <param name="index">처음 보여 줄 위치.</param>
    public QuickLookWindow(IList items, int index)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items;
        _index = Math.Clamp(index, 0, Math.Max(0, items.Count - 1));
        _presenter = new ImagePresenter(_view, s => _info.Text = s);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowState = WindowState.Maximized;
        Title = Foldy.Core.Localization.Loc.T("ql.title");
        SetResourceReference(BackgroundProperty, "Brush.Window");

        foreach (var tb in new[] { _title, _info })
        {
            tb.Margin = new Thickness(16, 10, 16, 10);
            tb.TextAlignment = TextAlignment.Center;
            tb.TextTrimming = TextTrimming.CharacterEllipsis;
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "Font.UI");
            tb.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size");
        }

        _title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _info.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_title, Dock.Top);
        DockPanel.SetDock(_info, Dock.Bottom);
        dock.Children.Add(_title);
        dock.Children.Add(_info);
        dock.Children.Add(_view);
        Content = dock;

        _view.ViewChanged += async (_, _) => await _presenter.EnsureResolutionAsync();
        Loaded += (_, _) => ShowCurrent();
        Closed += (_, _) => _presenter.Clear();
    }

    /// <summary>Delete·F2·Ctrl+C·Ctrl+X. 인덱스는 현재 목록 기준.</summary>
    public event EventHandler<QuickLookCommandEventArgs>? CommandRequested;

    /// <summary>← →로 보는 항목이 바뀌었다 (호스트가 격자 포커스를 맞춘다).</summary>
    public event EventHandler<int>? CurrentIndexChanged;

    public Func<object, ImageItemInfo>? ItemInfoSelector { get; set; }

    public ThumbnailLoader? Loader { get => _presenter.Loader; set => _presenter.Loader = value; }

    public bool AllowContentDecode { get => _presenter.AllowContentDecode; set => _presenter.AllowContentDecode = value; }

    public int CurrentIndex => _index;

    public ZoomImageView View => _view;

    /// <summary>목록이 바뀐 뒤(삭제·이름 변경) 새 목록과 위치를 넣는다. 비었으면 창을 닫는다.</summary>
    public void UpdateItems(IList items, int index)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items;
        if (items.Count == 0)
        {
            Close();
            return;
        }

        _index = Math.Clamp(index, 0, items.Count - 1);
        ShowCurrent(force: true);
    }

    public void Next() => Go(_index + 1);

    public void Previous() => Go(_index - 1);

    private void Go(int index)
    {
        if (index < 0 || index >= _items.Count || index == _index)
        {
            return;
        }

        _index = index;
        ShowCurrent();
        CurrentIndexChanged?.Invoke(this, index);
    }

    private ImageItemInfo InfoAt(int i) => (ItemInfoSelector ?? ImageItemAdapter.Default)(_items[i]!);

    private void ShowCurrent(bool force = false)
    {
        if (_items.Count == 0)
        {
            _presenter.Clear();
            return;
        }

        var item = InfoAt(_index);
        _title.Text = $"{item.Name}    {_index + 1} / {_items.Count}";
        if (!force && _presenter.Current is { } cur && cur == item)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var edge = (int)Math.Max(1024, Math.Max(ActualWidth, ActualHeight) * dpi);
        _ = _presenter.ShowAsync(item, edge);

        // 다음·이전 항목 썸네일을 미리 데워 둔다 (넘길 때 즉시 무언가 보이게)
        var loader = Loader ?? ThumbnailLoader.Shared;
        foreach (var n in new[] { _index + 1, _index - 1 })
        {
            if (n >= 0 && n < _items.Count)
            {
                _ = loader.GetThumbnailAsync(InfoAt(n), (int)Math.Round(256 * dpi), AllowContentDecode, null, CancellationToken.None);
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Escape:
            case Key.Space when mods == ModifierKeys.None:
                Close();
                break;
            case Key.Right:
            case Key.Down:
            case Key.PageDown:
                Next();
                break;
            case Key.Left:
            case Key.Up:
            case Key.PageUp:
                Previous();
                break;
            case Key.Home:
                Go(0);
                break;
            case Key.End:
                Go(_items.Count - 1);
                break;
            case Key.R when mods == ModifierKeys.None:
                _view.RotateClockwise();
                break;
            case Key.Add or Key.OemPlus:
                _view.ZoomBy(1.25);
                break;
            case Key.Subtract or Key.OemMinus:
                _view.ZoomBy(1 / 1.25);
                break;
            case Key.D0 or Key.NumPad0:
                _view.ToggleFitActual();
                break;
            case Key.Delete:
                Raise((mods & ModifierKeys.Shift) != 0 ? QuickLookCommand.DeletePermanently : QuickLookCommand.Delete);
                break;
            case Key.F2:
                Raise(QuickLookCommand.Rename);
                break;
            case Key.C when mods == ModifierKeys.Control:
                Raise(QuickLookCommand.Copy);
                break;
            case Key.X when mods == ModifierKeys.Control:
                Raise(QuickLookCommand.Cut);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Raise(QuickLookCommand command)
    {
        if (_index >= 0 && _index < _items.Count && _items[_index] is { } item)
        {
            CommandRequested?.Invoke(this, new QuickLookCommandEventArgs(command, _index, item));
        }
    }
}
