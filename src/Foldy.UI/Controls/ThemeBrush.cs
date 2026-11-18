using System.Windows;
using System.Windows.Controls;

namespace Foldy.UI.Controls;

/// <summary>
/// 탭 색 번호(0~7)를 테마 토큰 Brush.Tab{n}에 연결하는 첨부 속성.
/// SetResourceReference를 쓰므로 라이트/다크/레트로 전환 때 색이 바로 바뀐다.
/// </summary>
public static class ThemeBrush
{
    public static readonly DependencyProperty TabBackgroundProperty = DependencyProperty.RegisterAttached(
        "TabBackground", typeof(int), typeof(ThemeBrush), new PropertyMetadata(-1, OnTabBackground));

    public static int GetTabBackground(DependencyObject d) => (int)d.GetValue(TabBackgroundProperty);

    public static void SetTabBackground(DependencyObject d, int value) => d.SetValue(TabBackgroundProperty, value);

    private static void OnTabBackground(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not int i || i < 0)
        {
            return;
        }

        var key = "Brush.Tab" + (i % 8).ToString(System.Globalization.CultureInfo.InvariantCulture);
        switch (d)
        {
            case Border b:
                b.SetResourceReference(Border.BackgroundProperty, key);
                break;
            case Control c:
                c.SetResourceReference(Control.BackgroundProperty, key);
                break;
            case Panel p:
                p.SetResourceReference(Panel.BackgroundProperty, key);
                break;
            case System.Windows.Shapes.Shape s:
                s.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
                break;
        }
    }
}
