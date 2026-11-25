using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Foldy.UI.Controls;

/// <summary>
/// 마우스 포인터 바로 아래에 뜨는 짧은 안내 (디자인 원칙 11 "안내는 시선이 있는 곳에 (마우스 포인터 바로 아래)").
/// 아래 상태 칸은 누르거나 끄는 동안 눈이 가지 않으므로, 안 되는 이유를 그 자리에 보여 준다.
/// <list type="bullet">
/// <item>끄는 중: <see cref="Show"/>로 커서 바로 아래 (끌기가 끝나면 <see cref="Hide"/>)</item>
/// <item>누른 뒤: <see cref="ShowAtPointer"/> — 포인터 바로 아래 (포인터가 창 밖이면 고른 줄 아래). 잠시 뒤나 다음에 누르면 사라진다</item>
/// </list>
/// 한 번에 하나만 있으면 되므로 모든 창이 함께 쓴다. 포인터보다 아래에 두어 누를 곳을 가리지 않는다.
/// </summary>
public static class CursorHint
{
    /// <summary>커서 끝에서 오른쪽·아래로 떨어진 거리. 화살표 커서(약 20px)에 걸리지 않게 그 아래에 둔다.</summary>
    private const double OffsetX = 4;
    private const double OffsetY = 26;

    private static Popup? _popup;
    private static TextBlock? _title;
    private static TextBlock? _tip;
    private static DispatcherTimer? _timer;
    private static bool _watchingInput;

    public static bool IsOpen => _popup?.IsOpen == true;

    /// <summary>끄는 중: <paramref name="over"/>가 있는 창에서 커서(<paramref name="e"/>의 위치) 바로 아래에 안내를 띄우거나 옮긴다.</summary>
    public static void Show(UIElement over, DragEventArgs e, string title, string? tip = null)
    {
        StopAutoHide();
        if (Window.GetWindow(over) is not { } win)
        {
            return;
        }

        var at = e.GetPosition(win);
        Place(win, at.X + OffsetX, at.Y + OffsetY, title, tip);
    }

    /// <summary>
    /// 누른 뒤의 안내 (안 되는 이유·다음 할 일). 마우스 포인터 바로 아래 (키보드로 했어도). 포인터가 창 밖이면 포커스가 있는 줄(칸) 바로 아래.
    /// 글 길이에 맞춰 몇 초 보이고, 그 전에 다른 곳을 누르거나 키를 치면 사라진다. 보일 곳이 없으면 false (부르는 쪽이 상태 칸에 쓴다).
    /// </summary>
    public static bool ShowAtPointer(string text, Window? fallbackWindow = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var win = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? fallbackWindow;
        if (win is not { IsVisible: true } || win.WindowState == WindowState.Minimized)
        {
            return false;
        }

        // 시선은 마우스 포인터에 있다: 키보드로 했어도 포인터가 이 창 위에 있으면 포인터 바로 아래 (사용자 2026-11-25)
        // 위치는 창 기준으로 잰다 (창 안 내용은 화면 크기 배율로 늘어날 수 있다)
        var pointer = Mouse.GetPosition(win);
        if (pointer.X >= 0 && pointer.Y >= 0 && pointer.X <= win.ActualWidth && pointer.Y <= win.ActualHeight)
        {
            Place(win, pointer.X + OffsetX, pointer.Y + OffsetY, text, null);
            StartAutoHide(text);
            return true;
        }

        // 포인터가 창 밖이면 포커스가 있는 곳 아래
        if (Keyboard.FocusedElement is FrameworkElement { IsVisible: true } focused && Window.GetWindow(focused) == win)
        {
            // 파일 목록이면 고른 줄 바로 아래, 큰 칸이면 맨 윗줄 아래, 작은 칸(단추·글 칸)이면 그 바로 아래
            var rect = focused is FastFileList list && list.FocusedRowRect is { } row
                ? row
                : new Rect(0, 0, focused.ActualWidth, Math.Min(focused.ActualHeight, 40));
            var at = focused.TranslatePoint(new Point(Math.Min(rect.Left + 24, Math.Max(0, focused.ActualWidth - 40)), Math.Clamp(rect.Bottom, 0, focused.ActualHeight) + 2), win);
            Place(win, at.X, at.Y, text, null);
            StartAutoHide(text);
            return true;
        }

        return false;
    }

    public static void Hide()
    {
        StopAutoHide();
        if (_popup is { IsOpen: true })
        {
            _popup.IsOpen = false;
        }
    }

    private static void Place(UIElement over, double x, double y, string title, string? tip)
    {
        var popup = _popup ??= Create();
        _title!.Text = title;
        _title.FontWeight = string.IsNullOrEmpty(tip) ? FontWeights.Normal : FontWeights.SemiBold; // 한 줄짜리 안내는 굵지 않게
        _tip!.Text = tip ?? "";
        _tip.Visibility = string.IsNullOrEmpty(tip) ? Visibility.Collapsed : Visibility.Visible;
        if (!ReferenceEquals(popup.PlacementTarget, over))
        {
            popup.IsOpen = false;
            popup.PlacementTarget = over;
        }

        popup.HorizontalOffset = x;
        popup.VerticalOffset = y;
        var wasOpen = popup.IsOpen;
        popup.IsOpen = true;
        if (!wasOpen && UIElementAutomationPeer.FromElement(_title) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged); // 화면 읽기가 읽어 주게
        }
    }

    /// <summary>읽을 시간: 3초 + 글자 수만큼(한 글자 60ms), 최대 9초.</summary>
    private static void StartAutoHide(string text)
    {
        _timer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher);
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Min(9000, 3000 + text.Length * 60));
        _timer.Tick -= OnTimer;
        _timer.Tick += OnTimer;
        _timer.Start();
        if (!_watchingInput)
        {
            // 안내를 띄운 바로 그 입력이 끝난 다음부터 지켜본다 (띄우자마자 닫히지 않게)
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_timer?.IsEnabled == true && !_watchingInput)
                {
                    InputManager.Current.PreProcessInput += OnInput;
                    _watchingInput = true;
                }
            }));
        }
    }

    private static void StopAutoHide()
    {
        _timer?.Stop();
        if (_watchingInput)
        {
            InputManager.Current.PreProcessInput -= OnInput;
            _watchingInput = false;
        }
    }

    private static void OnTimer(object? sender, EventArgs e) => Hide();

    private static void OnInput(object sender, PreProcessInputEventArgs e)
    {
        var ev = e.StagingItem.Input.RoutedEvent;
        if (ev == Mouse.PreviewMouseDownEvent || ev == Keyboard.PreviewKeyDownEvent || ev == Mouse.PreviewMouseWheelEvent)
        {
            Hide();
        }
    }

    private static Popup Create()
    {
        _title = new TextBlock { TextWrapping = TextWrapping.Wrap };
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
            MaxWidth = 360,
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
