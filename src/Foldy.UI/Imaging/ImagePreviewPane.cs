using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Foldy.UI.Imaging;

/// <summary>
/// 오른쪽 미리보기 창 (설계서 5.12, F3 또는 선택 시). 화면에 맞게 그리고, 아래에 해상도·파일 크기·촬영일·카메라 한 줄.
/// 더블클릭 = 맞춤 ↔ 100%, R = 화면 회전(파일은 그대로). 호스트는 선택이 바뀔 때 <see cref="Show"/>를 부른다.
/// 스레드 소유권: UI 스레드 전용.
/// </summary>
public sealed class ImagePreviewPane : Border
{
    private readonly ZoomImageView _view = new();
    private readonly TextBlock _info = new();
    private readonly ImagePresenter _presenter;

    public ImagePreviewPane()
    {
        Focusable = true;
        FocusVisualStyle = null;
        _presenter = new ImagePresenter(_view, s => _info.Text = s);

        _info.Margin = new Thickness(10, 6, 10, 8);
        _info.TextTrimming = TextTrimming.CharacterEllipsis;
        _info.TextAlignment = TextAlignment.Center;
        _info.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        _info.SetResourceReference(TextBlock.FontFamilyProperty, "Font.UI");
        _info.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size");
        SetResourceReference(BackgroundProperty, "Brush.Surface");
        SetResourceReference(BorderBrushProperty, "Brush.Border");
        BorderThickness = new Thickness(1, 0, 0, 0);

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_info, Dock.Bottom);
        dock.Children.Add(_info);
        dock.Children.Add(_view);
        Child = dock;

        _view.ViewChanged += async (_, _) => await _presenter.EnsureResolutionAsync();
        Unloaded += (_, _) => _presenter.Clear();
    }

    /// <summary>썸네일 공급자 (격자와 같은 것을 넣으면 썸네일이 즉시 먼저 보인다). null이면 공용.</summary>
    public ThumbnailLoader? Loader { get => _presenter.Loader; set => _presenter.Loader = value; }

    /// <summary>false면 셸 캐시만 (네트워크 드라이브).</summary>
    public bool AllowContentDecode { get => _presenter.AllowContentDecode; set => _presenter.AllowContentDecode = value; }

    public ImageItemInfo? Current => _presenter.Current;

    /// <summary>정보 줄 문자열 (테스트·접근성용).</summary>
    public string InfoText => _info.Text;

    public ZoomImageView View => _view;

    /// <summary>항목을 보여 준다. null이면 비운다.</summary>
    public void Show(ImageItemInfo? item)
    {
        if (item is not { } it)
        {
            _presenter.Clear();
            return;
        }

        if (_presenter.Current is { } cur && cur == it)
        {
            return;
        }

        _ = _presenter.ShowAsync(it, PreviewEdge());
    }

    private int PreviewEdge()
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var edge = Math.Max(ActualWidth, ActualHeight) * dpi;
        // 창 크기가 조금 바뀌어도 다시 디코딩하지 않도록 넉넉히 (최소 1024)
        return (int)Math.Max(1024, Math.Ceiling(edge / 512) * 512);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.None)
        {
            _view.RotateClockwise();
            e.Handled = true;
        }
        else if (e.Key is Key.D0 or Key.NumPad0 && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _view.ToggleFitActual();
            e.Handled = true;
        }
    }
}
