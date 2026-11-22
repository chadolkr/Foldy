using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Foldy.UI.Dialogs;

/// <summary>확인 창의 종류 (아이콘과 색).</summary>
public enum DialogKind { Info, Question, Warning, Danger }

/// <summary>
/// Foldy 테마를 따르는 확인 창 (Windows 기본 MessageBox 대신, 2026-11-20 사용자: "확인 창 디자인이 본 프로그램과 다르다").
/// 밝게·어둡게·레트로, 강조색, 화면 크기를 그대로 따른다. 큰 글씨, 넉넉한 단추, Esc = 그만두기.
/// 되살릴 수 없는 일(<see cref="DialogKind.Danger"/>)은 기본 단추를 [그만두기]로 둔다(Enter 실수 방지).
/// 확인 단추는 꼭 필요할 때만 쓴다(디자인 원칙 1.4): 알림은 상태 줄로.
/// </summary>
public sealed class FoldyDialog : Window
{
    /// <summary>앱이 연결한다: 테마 창 테두리(어두운 제목줄)와 화면 크기 배율을 적용.</summary>
    public static Action<Window>? Decorate { get; set; }

    private int _result = -1;

    private FoldyDialog(string title, string message, DialogKind kind, IReadOnlyList<string> buttons, int defaultIndex, int cancelIndex)
    {
        Title = title;
        SetResourceReference(StyleProperty, "Foldy.Window");
        SetResourceReference(BackgroundProperty, "Brush.Window");
        SizeToContent = SizeToContent.Height;
        Width = 480;
        MinHeight = 160;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontSize = 16;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        var (glyph, brush) = kind switch
        {
            DialogKind.Info => ("", "Brush.Accent"),
            DialogKind.Question => ("", "Brush.Accent"),
            DialogKind.Warning => ("", "Brush.Progress"),
            _ => ("", "Brush.Error"),
        };
        var icon = new TextBlock { Text = glyph, FontSize = 30, Margin = new Thickness(0, 2, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(FontFamilyProperty, "Font.Icon");
        icon.SetResourceReference(ForegroundProperty, brush);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 24 };
        text.SetResourceReference(ForegroundProperty, "Brush.Text");
        var body = new DockPanel { Margin = new Thickness(24, 22, 24, 22) };
        DockPanel.SetDock(icon, Dock.Left);
        body.Children.Add(icon);
        body.Children.Add(new ScrollViewer { Content = text, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var bar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i], MinWidth = 96, MinHeight = 38, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(8, 0, 0, 0), FontSize = 15 };
            b.IsDefault = i == defaultIndex;
            b.IsCancel = i == cancelIndex;
            b.Click += (_, _) =>
            {
                _result = index;
                Close();
            };
            bar.Children.Add(b);
        }

        var footer = new Border { Child = bar, Padding = new Thickness(20, 12, 20, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        footer.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;
        System.Windows.Automation.AutomationProperties.SetName(this, title);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _result = cancelIndex;
                Close();
                e.Handled = true;
            }
        };
        Loaded += (_, _) =>
        {
            if (defaultIndex >= 0 && defaultIndex < bar.Children.Count)
            {
                ((Button)bar.Children[defaultIndex]).Focus();
            }
        };
    }

    /// <summary>
    /// 확인 창을 띄우고 누른 단추의 번호를 돌려준다. 닫기(X)·Esc는 <paramref name="cancelIndex"/>(없으면 -1).
    /// </summary>
    public static int Show(Window? owner, string title, string message, DialogKind kind, IReadOnlyList<string> buttons, int defaultIndex = 0, int cancelIndex = -1)
    {
        var d = new FoldyDialog(title, message, kind, buttons, defaultIndex, cancelIndex);
        if (owner is { IsLoaded: true })
        {
            d.Owner = owner;
        }
        else
        {
            d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Decorate?.Invoke(d);
        d.ShowDialog();
        return d._result < 0 ? cancelIndex : d._result;
    }

    /// <summary>문서 캡처·화면 점검용: 띄우지 않은 창을 만든다.</summary>
    public static Window Preview(string title, string message, DialogKind kind, IReadOnlyList<string> buttons, int defaultIndex = 0) =>
        new FoldyDialog(title, message, kind, buttons, defaultIndex, buttons.Count - 1);

    /// <summary>알림 하나 (단추 하나). 되도록 상태 줄을 쓰고, 이유를 꼭 읽어야 하는 실패에만.</summary>
    public static void Info(Window? owner, string title, string message, DialogKind kind = DialogKind.Info) =>
        Show(owner, title, message, kind, [Foldy.Core.Localization.Loc.T("dlg.ok")], 0, 0);

    /// <summary>
    /// 예/아니요. <paramref name="danger"/>면 되살릴 수 없는 일이라 기본 단추가 [그만두기] 쪽이다.
    /// </summary>
    public static bool Confirm(Window? owner, string title, string message, string yes, string? no = null, bool danger = false)
    {
        no ??= Foldy.Core.Localization.Loc.T("dlg.cancel");
        var r = Show(owner, title, message, danger ? DialogKind.Danger : DialogKind.Question, [yes, no], defaultIndex: danger ? 1 : 0, cancelIndex: 1);
        return r == 0;
    }
}
