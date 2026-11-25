using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Foldy.UI.Controls;

/// <summary>
/// 마우스 커서 바로 아래에 뜨는 짧은 안내 (끌어 놓기가 안 되는 이유 등).
/// 디자인 원칙 "안내는 손이 있는 자리에": 아래 상태 칸은 끄는 동안 눈이 가지 않으므로 커서 옆에 보여 준다.
/// 한 번에 하나만 있으면 되므로 모든 창이 함께 쓴다. 커서보다 아래에 두어 놓을 곳을 가리지 않는다.
/// </summary>
public static class CursorHint
{
    /// <summary>커서 끝에서 오른쪽·아래로 떨어진 거리. 화살표 커서(약 20px)에 걸리지 않게 그 아래에 둔다.</summary>
    private const double OffsetX = 4;
    private const double OffsetY = 26;

    private static Popup? _popup;
    private static TextBlock? _title;
    private static TextBlock? _tip;

    public static bool IsOpen => _popup?.IsOpen == true;

    /// <summary><paramref name="over"/> 안의 <paramref name="at"/>(커서 위치) 바로 아래에 안내를 띄우거나 옮긴다.</summary>
    public static void Show(UIElement over, Point at, string title, string? tip = null)
    {
        var popup = _popup ??= Create();
        _title!.Text = title;
        _tip!.Text = tip ?? "";
        _tip.Visibility = string.IsNullOrEmpty(tip) ? Visibility.Collapsed : Visibility.Visible;
        if (!ReferenceEquals(popup.PlacementTarget, over))
        {
            popup.IsOpen = false;
            popup.PlacementTarget = over;
        }

        popup.HorizontalOffset = at.X + OffsetX;
        popup.VerticalOffset = at.Y + OffsetY;
        popup.IsOpen = true;
    }

    public static void Hide()
    {
        if (_popup is { IsOpen: true })
        {
            _popup.IsOpen = false;
        }
    }

    private static Popup Create()
    {
        _title = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _tip = new TextBlock { Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        _tip.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_tip);
        var border = new Border
        {
            Child = stack,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 7),
            MaxWidth = 320,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.25 },
            Margin = new Thickness(0, 0, 8, 8), // 그림자 자리
        };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        AutomationProperties.SetAutomationId(_title, "CursorHint"); // 시험·화면 읽기 (Border는 자동화 트리에 없다)
        AutomationProperties.SetLiveSetting(_title, AutomationLiveSetting.Polite);
        return new Popup
        {
            Child = border,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Focusable = false,
            StaysOpen = true,
        };
    }
}
