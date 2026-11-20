using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Foldy.App;

/// <summary>
/// 처음 실행 때의 기능 소개를 쇼츠처럼 (사용자 요청 2026-11-21: "글을 읽기 싫어하니까 동영상 화면으로").
/// 세로 화면에 한 장면씩 저절로 넘어가고, 실제 Foldy 화면이 천천히 다가가며(줌·이동) 누를 곳에서 동그라미가 톡톡 뛴다.
/// 글은 큰 한 줄 + 작은 한 줄뿐. 위쪽 막대로 몇 번째인지 보이고, 오른쪽을 누르면 다음, 왼쪽은 이전, Space 멈춤, Esc 그만 보기.
/// 문구는 <see cref="Scenes"/> 한 곳에 (나중에 언어별 리소스로). 그림은 도움말과 같은 화면 캡처(help\images)를 쓴다.
/// </summary>
internal sealed class ShortsWindow : Window
{
    /// <summary>장면 하나: 그림, 다가갈 곳(그림 안 비율), 확대 시작·끝, 큰 글, 작은 글.</summary>
    public sealed record Scene(string Image, double FocusX, double FocusY, double ZoomFrom, double ZoomTo, string Title, string Sub);

    /// <summary>장면 목록 (다국어 준비: 문구는 여기만).</summary>
    public static readonly Scene[] Scenes =
    [
        new("main.png", 0.55, 0.4, 1.7, 2.1, "끌어다 놓으면 복사 끝", "두 창 사이로 끌거나 F5 · 이동은 F6"),
        new("quicksearch.png", 0.38, 0.32, 1.0, 1.2, "Ctrl+Space 한 번이면 다 찾아요", "파일·앱·설정·계산까지, 말하듯 써도 돼요"),
        new("analysis.png", 0.72, 0.3, 1.5, 1.9, "용량 범인을 한눈에", "큰 상자가 큰 폴더 · 지워도 되는 것만 골라 줘요"),
        new("guide.png", 0.45, 0.25, 1.05, 1.3, "쌓인 바탕화면, 대화로 정리해요", "버리지 않고 보관해요 · 되돌리기 한 번이면 원래대로"),
        new("memo.png", 0.72, 0.18, 1.0, 1.3, "비밀번호는 잠긴 메모에", "5분 동안 안 쓰면 저절로 잠겨요"),
        new("template-picker.png", 0.78, 0.3, 1.2, 1.5, "폴더 구조를 클릭 한 번에", "Java·PARA·Claude 프로젝트 등 · 미리 보고 만들어요"),
        new("git-changes.png", 0.5, 0.35, 1.5, 1.8, "프로젝트 폴더면 Git이 저절로", "받기·올리기·커밋을 버튼으로"),
    ];

    public const string EndTitle = "이제 시작해 볼까요?";
    public const string EndSub = "모르는 게 있으면 F1 · 이 소개는 더 보기 → 둘러보기에서 다시 볼 수 있어요";

    private static readonly TimeSpan SceneLength = TimeSpan.FromSeconds(5.5);

    private readonly Grid _stage = new() { ClipToBounds = true };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Ellipse _tap = new() { Width = 46, Height = 46, StrokeThickness = 4, IsHitTestVisible = false };
    private readonly TextBlock _title = new() { FontSize = 26, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _sub = new() { FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _bars = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 14, 16, 10) };
    private readonly Button _skip = new() { Content = "건너뛰기", MinHeight = 34, MinWidth = 88, Padding = new Thickness(12, 2, 12, 2) };
    private readonly Button _start = new() { Content = "시작하기", MinHeight = 44, MinWidth = 160, FontSize = 17, IsDefault = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 18, 0, 0) };
    private readonly Canvas _canvas = new();
    private readonly DispatcherTimer _clock;
    private readonly string _imageDir;
    private int _index;
    private TimeSpan _elapsed;
    private DateTime _last = DateTime.UtcNow;
    private bool _paused;
    private BitmapImage? _bitmap;

    public ShortsWindow()
    {
        Title = "Foldy 짧은 소개";
        SetResourceReference(StyleProperty, "Foldy.Window");
        SetResourceReference(BackgroundProperty, "Brush.Window");
        Width = 440;
        Height = 800;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        _imageDir = System.IO.Path.Combine(AppContext.BaseDirectory, "help", "images");

        for (var i = 0; i <= Scenes.Length; i++)
        {
            var track = new Border { Width = 30, Height = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(3, 0, 3, 0), ClipToBounds = true };
            track.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
            var fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(2.5) };
            fill.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
            track.Child = fill;
            _bars.Children.Add(track);
        }

        _canvas.Children.Add(_image);
        _tap.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        _canvas.Children.Add(_tap);
        var frame = new Border { Child = _canvas, CornerRadius = new CornerRadius(18), Margin = new Thickness(18, 0, 18, 0), ClipToBounds = true, BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        frame.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        _stage.Children.Add(frame);

        _title.SetResourceReference(ForegroundProperty, "Brush.Text");
        _sub.SetResourceReference(ForegroundProperty, "Brush.TextSecondary");
        var caption = new StackPanel { Margin = new Thickness(26, 20, 26, 22) };
        caption.Children.Add(_title);
        caption.Children.Add(_sub);
        caption.Children.Add(_start);
        _start.HorizontalAlignment = HorizontalAlignment.Center;

        var top = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(_skip, Dock.Right);
        _skip.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(_skip);
        top.Children.Add(_bars);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(caption, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(caption);
        root.Children.Add(_stage);
        Content = root;
        System.Windows.Automation.AutomationProperties.SetName(this, Title);

        _skip.Click += (_, _) => Close();
        _start.Click += (_, _) => Close();
        _stage.MouseLeftButtonUp += (_, e) =>
        {
            if (e.GetPosition(_stage).X < _stage.ActualWidth / 3)
            {
                Go(_index - 1);
            }
            else
            {
                Go(_index + 1);
            }
        };
        _stage.Cursor = Cursors.Hand;
        PreviewKeyDown += OnKey;
        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick(), Dispatcher);
        Loaded += (_, _) =>
        {
            Go(0);
            _clock.Start();
        };
        Closed += (_, _) => _clock.Stop();
        if (App.UiScale > 1.001)
        {
            Width *= Math.Min(App.UiScale, 1.15);
            Height *= Math.Min(App.UiScale, 1.15);
        }
    }

    /// <summary>테스트·문서 캡처: 몇 번째 장면인지, 그 장면으로 가기.</summary>
    public int Index => _index;

    public void Go(int i)
    {
        _index = Math.Clamp(i, 0, Scenes.Length);
        _elapsed = TimeSpan.Zero;
        _last = DateTime.UtcNow;
        var end = _index == Scenes.Length;
        _start.Visibility = end ? Visibility.Visible : Visibility.Collapsed;
        _skip.Visibility = end ? Visibility.Collapsed : Visibility.Visible;
        if (end)
        {
            _title.Text = EndTitle;
            _sub.Text = EndSub;
            _bitmap = Load("main.png");
            _start.Focus();
        }
        else
        {
            _title.Text = Scenes[_index].Title;
            _sub.Text = Scenes[_index].Sub;
            _bitmap = Load(Scenes[_index].Image);
        }

        _image.Source = _bitmap;
        _image.Opacity = end ? 0.25 : 1;
        Render();
    }

    private BitmapImage? Load(string name)
    {
        try
        {
            var path = System.IO.Path.Combine(_imageDir, name);
            if (!File.Exists(path))
            {
                return null;
            }

            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Right:
            case Key.PageDown:
                Go(_index + 1);
                break;
            case Key.Left:
            case Key.PageUp:
                Go(_index - 1);
                break;
            case Key.Space:
                _paused = !_paused;
                _last = DateTime.UtcNow;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        if (!_paused && _index < Scenes.Length)
        {
            _elapsed += now - _last;
            if (_elapsed >= SceneLength)
            {
                Go(_index + 1);
                return;
            }
        }

        _last = now;
        Render();
    }

    private void Render()
    {
        var u = Math.Clamp(_elapsed.TotalMilliseconds / SceneLength.TotalMilliseconds, 0, 1);
        for (var i = 0; i < _bars.Children.Count; i++)
        {
            var track = (Border)_bars.Children[i];
            var fill = (Border)track.Child;
            fill.Width = track.Width * (i < _index ? 1 : i == _index ? (_index == Scenes.Length ? 1 : u) : 0);
        }

        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        if (_bitmap is null || w <= 0 || h <= 0)
        {
            _tap.Visibility = Visibility.Collapsed;
            return;
        }

        var scene = _index < Scenes.Length ? Scenes[_index] : new Scene("main.png", 0.5, 0.5, 1, 1, "", "");
        var iw = _bitmap.PixelWidth / 2.0; // 캡처는 2배로 찍혀 있다
        var ih = _bitmap.PixelHeight / 2.0;
        var ease = u * u * (3 - (2 * u));
        var cover = Math.Max(w / iw, h / ih);
        var fit = Math.Min(w / iw, h / ih);
        var baseScale = iw / ih > 3 ? h * 0.4 / ih : Math.Max(fit, cover * 0.55); // 넓은 화면은 너무 잘리지 않게 (위아래 여백 허용), 긴 띠(농구 장면)는 크게 보이며 옆으로 흐른다
        var s = baseScale * (scene.ZoomFrom + ((scene.ZoomTo - scene.ZoomFrom) * ease));
        var dw = iw * s;
        var dh = ih * s;
        var fx = iw / ih > 3 ? Lerp(0.15, 0.85, ease) : scene.FocusX;
        var left = Math.Clamp((w / 2) - (fx * dw), Math.Min(0, w - dw), Math.Max(0, w - dw));
        var top = Math.Clamp((h / 2) - (scene.FocusY * dh), Math.Min(0, h - dh), Math.Max(0, h - dh));
        if (dw <= w)
        {
            left = (w - dw) / 2;
        }

        if (dh <= h)
        {
            top = (h - dh) / 2;
        }

        _image.Width = dw;
        _image.Height = dh;
        Canvas.SetLeft(_image, left);
        Canvas.SetTop(_image, top);

        // 누를 곳에서 동그라미가 톡톡 (1.4초마다 커지며 흐려진다)
        var showTap = _index < Scenes.Length && iw / ih <= 3;
        _tap.Visibility = showTap ? Visibility.Visible : Visibility.Collapsed;
        if (showTap)
        {
            var phase = (_elapsed.TotalSeconds % 1.4) / 1.4;
            var size = 30 + (phase * 36);
            _tap.Width = _tap.Height = size;
            _tap.Opacity = 1 - phase;
            Canvas.SetLeft(_tap, left + (scene.FocusX * dw) - (size / 2));
            Canvas.SetTop(_tap, top + (scene.FocusY * dh) - (size / 2));
        }
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
