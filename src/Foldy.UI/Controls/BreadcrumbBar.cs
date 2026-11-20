using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Foldy.Presentation.Navigation;

namespace Foldy.UI.Controls;

/// <summary>
/// 경로바: 평소엔 브레드크럼(조각 버튼), 빈 곳을 누르거나 Ctrl+L이면 직접 입력 (설계서 2장 경로바).
/// Enter로 이동 요청(<see cref="NavigateRequested"/>), Esc로 취소. 디스크에 접근하지 않는다.
/// </summary>
public sealed class BreadcrumbBar : Border
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(BreadcrumbBar), new PropertyMetadata("", (d, _) => ((BreadcrumbBar)d).Rebuild()));

    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _edit = new() { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly Grid _root = new();
    private readonly Rectangle _progress;

    public BreadcrumbBar()
    {
        SetResourceReference(StyleProperty, "Foldy.InputBorder");
        Cursor = Cursors.IBeam;
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _crumbs,
            Margin = new Thickness(4, 0, 4, 0),
            Focusable = false,
        };
        _progress = new Rectangle { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Width = 0, RadiusX = 1, RadiusY = 1 };
        _progress.SetResourceReference(Shape.FillProperty, "Brush.Progress");
        _root.Children.Add(scroller);
        _root.Children.Add(_edit);
        _root.Children.Add(_progress);
        Child = _root;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not Button)
            {
                BeginEdit();
                e.Handled = true;
            }
        };
        _edit.KeyDown += OnEditKey;
        _edit.LostKeyboardFocus += (_, _) => EndEdit();
        scroller.Loaded += (_, _) => scroller.ScrollToRightEnd();
        _crumbs.SizeChanged += (_, _) => scroller.ScrollToRightEnd();
    }

    public string Path
    {
        get => (string)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    /// <summary>사용자가 입력하거나 조각을 눌러 이동을 요청했을 때.</summary>
    public event Action<string>? NavigateRequested;

    public bool IsEditing => _edit.Visibility == Visibility.Visible;

    /// <summary>0~1이면 진행선 표시, null이면 숨김 (300ms 이후에만 보이게 하는 것은 호출자 책임).</summary>
    public void SetProgress(double? fraction)
    {
        _progress.Width = fraction is { } f ? Math.Max(0, ActualWidth * f) : 0;
    }

    public void BeginEdit()
    {
        _edit.Text = Path;
        _edit.Visibility = Visibility.Visible;
        _crumbs.Visibility = Visibility.Hidden;
        _edit.Focus();
        _edit.SelectAll();
    }

    private void EndEdit()
    {
        _edit.Visibility = Visibility.Collapsed;
        _crumbs.Visibility = Visibility.Visible;
    }

    private void OnEditKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = _edit.Text;
            EndEdit();
            NavigateRequested?.Invoke(text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndEdit();
            e.Handled = true;
        }
    }

    private void Rebuild()
    {
        _crumbs.Children.Clear();
        var segs = PathUtil.Segments(Path ?? "");
        for (var i = 0; i < segs.Count; i++)
        {
            if (i > 0)
            {
                var sep = new TextBlock { Text = "", FontSize = 9, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
                sep.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
                sep.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
                _crumbs.Children.Add(sep);
            }

            var target = segs[i].Path;
            var b = new Button
            {
                Content = segs[i].Name,
                Padding = new Thickness(6, 2, 6, 2),
                MinHeight = 28, // 누르는 곳은 넉넉하게
                MinWidth = 28,
                VerticalAlignment = VerticalAlignment.Stretch,
                Cursor = Cursors.Hand,
                ToolTip = target,
                Focusable = false,
            };
            b.SetResourceReference(StyleProperty, "Foldy.CrumbButton");
            b.Click += (_, _) => NavigateRequested?.Invoke(target);
            _crumbs.Children.Add(b);
        }
    }
}
