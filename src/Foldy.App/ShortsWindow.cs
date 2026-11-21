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
        // 순서는 사이드바 맨 위 대표 기능 넷과 같게 (사용자 2026-11-21). 글은 언어 파일(assets/i18n)의 tour.*
        // ① 무엇이든 찾기 (Ctrl+Space)
        S("quicksearch.png", 0.3, 0.24, 1.6, 1.9, "tour.qs"),
        S("quicksearch-settings.png", 0.3, 0.3, 1.8, 2.1, "tour.qs2"),
        S("quicksearch-calc.png", 0.3, 0.3, 1.8, 2.1, "tour.qs3"),
        S("quicksearch-talk.png", 0.3, 0.3, 1.8, 2.1, "tour.qs4"),
        // ② 공간 늘리기
        S("analysis.png", 0.8, 0.28, 1.65, 1.90, "tour.space"),
        S("rebuildable.png", 0.45, 0.45, 1.5, 1.75, "tour.junk"),
        // ③ 바탕화면, 다운로드, 폴더 정리 (PARA로)
        S("guide.png", 0.5, 0.22, 1.70, 1.95, "tour.desk"),
        S("para-organize.png", 0.55, 0.32, 1.6, 1.85, "tour.para"),
        // ④ 템플릿으로 폴더 만들기
        S("para-template.png", 0.7, 0.3, 1.6, 1.85, "tour.tpl"),
        // 그 밖의 좋은 기능
        S("main.png", 0.62, 0.38, 1.65, 1.95, "tour.copy"),
        S("mcp.png", 0.35, 0.25, 1.55, 1.80, "tour.ai"),
        S("memo.png", 0.72, 0.17, 1.75, 2.05, "tour.memo"),
        S("git-changes.png", 0.55, 0.32, 1.65, 1.90, "tour.git"),
    ];

    private static Scene S(string image, double fx, double fy, double zf, double zt, string key) =>
        new(image, fx, fy, zf, zt, Foldy.Core.Localization.Loc.T(key + ".t"), Foldy.Core.Localization.Loc.T(key + ".s"));

    /// <summary>마지막 장: 한 줄 소개 (사용자 2026-11-21).</summary>
    public static string EndTitle => Foldy.Core.Branding.Tagline;

    public static string EndSub => Foldy.Core.Branding.TaglineSub + " · " + Foldy.Core.Localization.Loc.T("tour.end.s");

    private static readonly TimeSpan SceneLength = TimeSpan.FromSeconds(5.5);

    private readonly Grid _stage = new() { ClipToBounds = true };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly MatrixTransform _move = new();
    private readonly TranslateTransform _tapMove = new();
    private readonly ScaleTransform _tapScale = new();
    private readonly Ellipse _tap = new() { Width = 46, Height = 46, StrokeThickness = 4, IsHitTestVisible = false };
    private readonly TextBlock _title = new() { FontSize = 26, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _sub = new() { FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _bars = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 14, 16, 10) };
    private readonly Button _skip = new() { Content = "건너뛰기", MinHeight = 34, MinWidth = 88, Padding = new Thickness(12, 2, 12, 2) };
    private readonly Button _start = new() { Content = "시작하기", MinHeight = 44, MinWidth = 160, FontSize = 17, IsDefault = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 18, 0, 0) };
    private readonly Canvas _canvas = new();
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
            var track = new Border { Width = 20, Height = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(3, 0, 3, 0), ClipToBounds = true };
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
        // 움직임은 화면을 새로 그릴 때마다(모니터 주사율) 변환만 바꾼다: 배치를 다시 하지 않아 끊기지 않는다
        _image.RenderTransform = _move;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Linear);
        _image.CacheMode = new BitmapCache { RenderAtScale = 2 };
        _tap.RenderTransform = new TransformGroup { Children = { _tapScale, _tapMove } };
        _tap.Width = _tap.Height = 40;
        _tapScale.CenterX = _tapScale.CenterY = 20;
        Loaded += (_, _) =>
        {
            Go(0);
            CompositionTarget.Rendering += OnFrame;
        };
        Closed += (_, _) => CompositionTarget.Rendering -= OnFrame;
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
        if (_bitmap is not null)
        {
            _image.Width = _bitmap.PixelWidth / 2.0; // 캡처는 2배로 찍혀 있다: 원래 크기로 두고 변환으로만 확대
            _image.Height = _bitmap.PixelHeight / 2.0;
        }

        // 장면이 바뀔 때 부드럽게 (0.35초 동안 서서히 나타남)
        _canvas.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
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

    private void OnFrame(object? sender, EventArgs e) => Tick();

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
            fill.Width = track.Width;
            var amount = i < _index ? 1 : i == _index ? (_index == Scenes.Length ? 1 : u) : 0;
            if (fill.RenderTransform is not ScaleTransform st)
            {
                fill.RenderTransform = st = new ScaleTransform(0, 1);
            }

            st.ScaleX = amount;
        }

        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        if (_bitmap is null || w <= 0 || h <= 0)
        {
            _tap.Visibility = Visibility.Collapsed;
            return;
        }

        var end = _index >= Scenes.Length;
        var scene = end ? new Scene("main.png", 0.5, 0.5, 1, 1, "", "") : Scenes[_index];
        var iw = _bitmap.PixelWidth / 2.0;
        var ih = _bitmap.PixelHeight / 2.0;
        var ease = u * u * (3 - (2 * u));
        // 확대: 장면마다 정한 배율(원래 크기 대비, 글씨가 잘 보이게). 마지막 장은 전체가 흐릿하게 보이게 맞춤
        var s = end ? Math.Min(w / iw, h / ih) : scene.ZoomFrom + ((scene.ZoomTo - scene.ZoomFrom) * ease);
        var dw = iw * s;
        var dh = ih * s;
        var left = dw <= w ? (w - dw) / 2 : Math.Clamp((w / 2) - (scene.FocusX * dw), w - dw, 0);
        var top = dh <= h ? (h - dh) / 2 : Math.Clamp((h / 2) - (scene.FocusY * dh), h - dh, 0);
        _move.Matrix = new Matrix(s, 0, 0, s, left, top);

        // 누를 곳에서 동그라미가 톡톡 (1.4초마다 커지며 흐려진다)
        _tap.Visibility = end ? Visibility.Collapsed : Visibility.Visible;
        if (!end)
        {
            var phase = (_elapsed.TotalSeconds % 1.4) / 1.4;
            _tapScale.ScaleX = _tapScale.ScaleY = 0.75 + (phase * 0.9);
            _tap.Opacity = 1 - phase;
            _tapMove.X = left + (scene.FocusX * dw) - 20;
            _tapMove.Y = top + (scene.FocusY * dh) - 20;
        }
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
