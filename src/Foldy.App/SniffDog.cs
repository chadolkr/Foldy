using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Foldy.App;

/// <summary>
/// 빠른 검색의 "냄새 잘 맡는 개" (사용자 결정 2026-11-20): 축 늘어진 귀와 주름의 블러드하운드가
/// 코를 바닥에 대고 킁킁 → 귀를 펄럭이며 쌩 달려가 → 파일을 물고 꼬리를 흔든다 (0.5초). 못 찾으면 고개를 갸웃.
/// Foldy 고유의 단순한 도형 그림이라 말을 몰라도 통한다. 대사는 <see cref="Text"/> 한 곳에 (나중에 언어별 리소스로).
/// 애니메이션 설정을 끄면 글자만.
/// </summary>
internal sealed class SniffDog : Border
{
    /// <summary>화면 문구 (다국어 준비: 여기만 바꾸면 된다).</summary>
    public static class Text
    {
        public static string Found(long count, TimeSpan elapsed) =>
            Foldy.Core.Localization.Loc.F("dog.found", Seconds(elapsed), count);

        /// <summary>0.03초, 아주 빠르면 0.004초처럼 0이 아닌 숫자가 보이게.</summary>
        public static string Seconds(TimeSpan t) =>
            t.TotalSeconds >= 0.01 ? t.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture)
            : Math.Max(t.TotalSeconds, 0.001).ToString("0.000", System.Globalization.CultureInfo.CurrentCulture);

        public static string NotFound => Foldy.Core.Localization.Loc.T("dog.notFound");
    }

    private readonly Canvas _stage = new() { Width = 170, Height = 50, ClipToBounds = false };
    private readonly Canvas _dog = new() { Width = 74, Height = 46 };
    private readonly TranslateTransform _run = new();
    private readonly TranslateTransform _sniff = new();
    private readonly RotateTransform _tilt = new(0, 12, 14);
    private readonly RotateTransform _ear = new(0, 20, 9);
    private readonly RotateTransform _tail = new(0, 62, 19);
    private readonly Canvas _folder;
    private readonly TranslateTransform _folderMove = new();
    private readonly TextBlock _result = new() { FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap };

    private const double DogStart = 92, FolderX = 6;

    public SniffDog()
    {
        Padding = new Thickness(14, 2, 14, 0);
        Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(this, "냄새 잘 맡는 개");
        BuildDog();
        Canvas.SetLeft(_dog, DogStart);
        Canvas.SetTop(_dog, 4);
        _dog.RenderTransform = _run;
        _stage.Children.Add(_dog);

        _folder = FolderShape();
        Canvas.SetLeft(_folder, FolderX);
        Canvas.SetTop(_folder, 34);
        _folder.RenderTransform = _folderMove;
        _stage.Children.Add(_folder);

        _result.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        var dock = new DockPanel();
        DockPanel.SetDock(_stage, Dock.Left);
        dock.Children.Add(_stage);
        dock.Children.Add(_result);
        Child = dock;
    }

    public void Reset()
    {
        Visibility = Visibility.Collapsed;
        foreach (var (t, p) in new (Animatable, DependencyProperty)[]
        {
            (_run, TranslateTransform.XProperty), (_sniff, TranslateTransform.YProperty), (_tilt, RotateTransform.AngleProperty),
            (_ear, RotateTransform.AngleProperty), (_tail, RotateTransform.AngleProperty),
            (_folderMove, TranslateTransform.XProperty), (_folderMove, TranslateTransform.YProperty),
        })
        {
            t.BeginAnimation(p, null);
        }
    }

    /// <summary>찾았을 때: 킁킁 → 귀 펄럭이며 달려가 → 물고 꼬리 흔들기.</summary>
    public void PlayFound(long count, TimeSpan elapsed, bool animate)
    {
        Reset();
        _result.Text = Text.Found(count, elapsed);
        Show(animate);
        if (!animate)
        {
            return;
        }

        _folder.Visibility = Visibility.Visible;
        // 0~0.15초: 코를 바닥에 대고 킁킁 (머리 위아래 두 번)
        _sniff.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 3, new Duration(TimeSpan.FromMilliseconds(40))) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) });
        // 0.15~0.35초: 쌩! 왼쪽 폴더까지 달려간다, 귀는 펄럭펄럭
        var dash = DogStart - FolderX - 2;
        _run.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -dash, new Duration(TimeSpan.FromMilliseconds(200))) { BeginTime = TimeSpan.FromMilliseconds(150), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        _ear.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-10, 55, new Duration(TimeSpan.FromMilliseconds(50))) { BeginTime = TimeSpan.FromMilliseconds(150), AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) });
        // 0.35초~: 폴더를 입에 물고, 꼬리를 흔든다
        _folderMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -14, new Duration(TimeSpan.FromMilliseconds(80))) { BeginTime = TimeSpan.FromMilliseconds(350) });
        _tail.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-25, 25, new Duration(TimeSpan.FromMilliseconds(50))) { BeginTime = TimeSpan.FromMilliseconds(350), AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) });
    }

    /// <summary>못 찾았을 때: 고개를 갸웃.</summary>
    public void PlayNotFound(bool animate)
    {
        Reset();
        _result.Text = Text.NotFound;
        Show(animate);
        if (!animate)
        {
            return;
        }

        _folder.Visibility = Visibility.Collapsed;
        _sniff.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 3, new Duration(TimeSpan.FromMilliseconds(60))) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) });
        _tilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, -18, new Duration(TimeSpan.FromMilliseconds(160))) { BeginTime = TimeSpan.FromMilliseconds(260), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } });
    }

    private void Show(bool animate)
    {
        Visibility = Visibility.Visible;
        _stage.Visibility = animate ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>블러드하운드: 갈색 몸, 축 늘어진 짙은 귀, 이마 주름, 바닥을 향한 코. 왼쪽을 본다.</summary>
    private void BuildDog()
    {
        var coat = Frozen(Color.FromRgb(0xB8, 0x6B, 0x2E));
        var dark = Frozen(Color.FromRgb(0x7A, 0x3E, 0x12));
        var black = Frozen(Color.FromRgb(0x2A, 0x1A, 0x10));

        // 꼬리 (위로 휜 선)
        var tail = new Path { Stroke = coat, StrokeThickness = 3.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Data = Geometry.Parse("M62,19 Q70,14 71,5"), RenderTransform = _tail };
        _dog.Children.Add(tail);
        // 다리
        foreach (var x in new[] { 24.0, 31, 52, 58 })
        {
            var leg = new Rectangle { Width = 4.5, Height = 13, RadiusX = 2, RadiusY = 2, Fill = dark };
            Canvas.SetLeft(leg, x);
            Canvas.SetTop(leg, 29);
            _dog.Children.Add(leg);
        }

        // 몸
        var body = new Ellipse { Width = 46, Height = 22, Fill = coat };
        Canvas.SetLeft(body, 20);
        Canvas.SetTop(body, 13);
        _dog.Children.Add(body);

        // 머리 (킁킁·갸웃은 머리만 움직인다)
        var head = new Canvas { Width = 30, Height = 30, RenderTransform = new TransformGroup { Children = { _tilt, _sniff } } };
        Canvas.SetLeft(head, 2);
        Canvas.SetTop(head, 8);
        var skull = new Ellipse { Width = 22, Height = 19, Fill = coat };
        Canvas.SetLeft(skull, 7);
        Canvas.SetTop(skull, 2);
        head.Children.Add(skull);
        var snout = new Ellipse { Width = 14, Height = 11, Fill = coat };
        Canvas.SetLeft(snout, 0);
        Canvas.SetTop(snout, 12);
        head.Children.Add(snout);
        var nose = new Ellipse { Width = 6, Height = 5, Fill = black };
        Canvas.SetLeft(nose, -2);
        Canvas.SetTop(nose, 15);
        head.Children.Add(nose);
        var eye = new Ellipse { Width = 3, Height = 3, Fill = black };
        Canvas.SetLeft(eye, 12);
        Canvas.SetTop(eye, 8);
        head.Children.Add(eye);
        // 이마 주름 두 줄
        head.Children.Add(new Path { Stroke = dark, StrokeThickness = 1, Data = Geometry.Parse("M13,4 Q16,3 19,4 M12,6.5 Q16,5.5 20,6.5") });
        // 축 늘어진 귀 (달리면 펄럭)
        var ear = new Ellipse { Width = 9, Height = 22, Fill = dark, RenderTransform = _ear };
        Canvas.SetLeft(ear, 16);
        Canvas.SetTop(ear, 7);
        head.Children.Add(ear);
        _dog.Children.Add(head);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Foldy 아이콘과 같은 주황 폴더 (작게).</summary>
    private static Canvas FolderShape()
    {
        var c = new Canvas { Width = 16, Height = 13 };
        c.Children.Add(new Path { Fill = Frozen(Color.FromRgb(0xC9, 0x5A, 0x05)), Data = Geometry.Parse("M0,1.2 a1,1 0 0 1 1,-1 h3.6 l1.4,1.4 h9 a1,1 0 0 1 1,1 V12 a1,1 0 0 1 -1,1 H1 a1,1 0 0 1 -1,-1 z") });
        var front = new Rectangle { Width = 16, Height = 9.5, RadiusX = 1, RadiusY = 1, Fill = Frozen(Color.FromRgb(0xFF, 0x9F, 0x2E)) };
        Canvas.SetTop(front, 3.5);
        c.Children.Add(front);
        return c;
    }
}
