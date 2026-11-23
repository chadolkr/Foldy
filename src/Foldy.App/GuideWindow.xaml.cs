using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Foldy.App;

/// <summary>
/// 정리 도구가 같이 쓰는 대화 창. Foldy가 한 단계씩 말하고(<see cref="Say"/>), 질문하고(<see cref="AskAsync"/>),
/// 필요하면 목록을 보여 주고 고르게 한다(<see cref="PickAsync{T}"/>). 사용자가 고른 답도 오른쪽 말풍선으로 남는다.
/// 창을 닫으면 지금 질문은 <see cref="Closed"/> 값으로 끝난다.
/// </summary>
public partial class GuideWindow : Window
{
    public const string ClosedChoice = "__closed";

    private TaskCompletionSource<string>? _pending;

    public GuideWindow(string title)
    {
        InitializeComponent();
        Title = title;
        if (App.UiScale > 1.001)
        {
            Width *= App.UiScale;
            Height *= App.UiScale;
            App.ApplyScale(this);
        }
        Closed += (_, _) => _pending?.TrySetResult(ClosedChoice);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                Close(); // Esc = 그만두기 (아무것도 바꾸지 않은 채 닫힌다)
                e.Handled = true;
            }
        };
    }

    public bool IsClosed { get; private set; }

    /// <summary>고른 답을 오른쪽 말풍선으로 남길지 (둘러보기처럼 [다음]만 누르는 흐름에서는 끈다).</summary>
    public bool EchoChoices { get; set; } = true;

    /// <summary>지금까지의 말을 지운다 (한 장씩 넘기는 안내).</summary>
    public void ClearMessages() => Messages.Children.Clear();

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        base.OnClosed(e);
    }

    /// <summary>Foldy의 말 한마디.</summary>
    public TextBlock Say(string text)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        Add(tb, mine: false);
        return tb;
    }

    /// <summary>질문하고 고른 선택지의 id를 돌려준다. 창을 닫으면 <see cref="ClosedChoice"/>.</summary>
    public Task<string> AskAsync(string? text, params (string Id, string Label)[] choices)
    {
        if (text is not null)
        {
            Say(text);
        }

        return ChooseAsync(choices);
    }

    /// <summary>목록을 보여 주고 고르게 한다 (체크 상자). 그 아래 선택지로 무엇을 할지 묻는다.</summary>
    public Task<(string Choice, IReadOnlyList<T> Picked)> PickAsync<T>(string text, IEnumerable<(T Item, string Label, bool Selected)> items, params (string Id, string Label)[] choices) =>
        PickAsync(text, items, null, choices);

    /// <summary>고르기 + 항목마다 빨간 글씨 경고 (예: 최근에 쓴 .git). <paramref name="warning"/>이 null을 주면 경고 없음.</summary>
    public async Task<(string Choice, IReadOnlyList<T> Picked)> PickAsync<T>(string text, IEnumerable<(T Item, string Label, bool Selected)> items, Func<T, string?>? warning, params (string Id, string Label)[] choices)
    {
        Say(text);
        object Content(T item, string label)
        {
            var label1 = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
            if (warning?.Invoke(item) is not { Length: > 0 } w)
            {
                return label1;
            }

            var red = new TextBlock { Text = "⚠ " + w, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            red.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Error");
            return new StackPanel { Children = { label1, red } };
        }

        var rows = items.Select(i => (i.Item, Box: new CheckBox { Content = Content(i.Item, i.Label), IsChecked = i.Selected, Margin = new Thickness(0, 2, 0, 2) })).ToList();
        var panel = new StackPanel();
        var toggle = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(Foldy.Core.Localization.Loc.T("guide.toggleAll")));
        link.Click += (_, _) =>
        {
            var all = rows.All(r => r.Box.IsChecked == true);
            foreach (var r in rows)
            {
                r.Box.IsChecked = !all;
            }
        };
        toggle.Inlines.Add(link);
        panel.Children.Add(toggle);
        var list = new StackPanel();
        foreach (var r in rows)
        {
            list.Children.Add(r.Box);
        }

        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Add(panel, mine: false);
        var choice = await ChooseAsync(choices);
        foreach (var r in rows)
        {
            r.Box.IsEnabled = false;
        }

        return (choice, rows.Where(r => r.Box.IsChecked == true).Select(r => r.Item).ToList());
    }

    /// <summary>
    /// 암호를 받는다. <paramref name="confirm"/>이면 두 번 입력해 같은지 확인한다. 취소·닫기면 null.
    /// 입력 칸은 답을 받은 뒤 바로 비운다 (암호는 화면·기록에 남기지 않는다).
    /// </summary>
    /// <summary>새로 정하는 암호의 최소 길이. 금고 키는 PBKDF2 60만 회라도 짧은 암호는 결국 맞혀진다.</summary>
    public const int MinNewPasswordLength = 8;

    public async Task<string?> AskPasswordAsync(string text, bool confirm, string? okLabel = null)
    {
        okLabel ??= Foldy.Core.Localization.Loc.T("dlg.ok");
        Say(text);
        var p1 = new PasswordBox { MinWidth = 260, Margin = new Thickness(0, 2, 0, 2) };
        var p2 = new PasswordBox { MinWidth = 260, Margin = new Thickness(0, 2, 0, 2) };
        var err = new TextBlock { Foreground = (Brush)FindResource("Brush.Error"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var panel = new StackPanel();
        // 새 암호는 길이 제한을 미리 보여 준다 (사용자 원칙: 제한은 미리 알리기)
        panel.Children.Add(new TextBlock { Text = confirm ? Foldy.Core.Localization.Loc.F("guide.passwordNew", MinNewPasswordLength) : Foldy.Core.Localization.Loc.T("guide.password") });
        panel.Children.Add(p1);
        if (confirm)
        {
            panel.Children.Add(new TextBlock { Text = Foldy.Core.Localization.Loc.T("guide.again"), Margin = new Thickness(0, 4, 0, 0) });
            panel.Children.Add(p2);
        }

        panel.Children.Add(err);
        Add(panel, mine: false);
        p1.Focus();
        while (true)
        {
            var c = await ChooseAsync([("ok", okLabel), ("cancel", Foldy.Core.Localization.Loc.T("dlg.cancel"))]);
            if (c != "ok")
            {
                p1.Clear();
                p2.Clear();
                panel.IsEnabled = false;
                return null;
            }

            var a = p1.Password;
            if (a.Length == 0 || (confirm && a.Length < MinNewPasswordLength))
            {
                // 새 암호만 길이를 본다 (예전에 짧게 만든 금고도 열 수 있어야 한다)
                err.Text = a.Length == 0 ? Foldy.Core.Localization.Loc.T("guide.enterPw") : Foldy.Core.Localization.Loc.F("guide.pwMin", MinNewPasswordLength);
            }
            else if (confirm && a != p2.Password)
            {
                err.Text = Foldy.Core.Localization.Loc.T("guide.mismatch");
                p2.Clear();
            }
            else
            {
                p1.Clear();
                p2.Clear();
                panel.IsEnabled = false;
                return a;
            }

            err.Visibility = Visibility.Visible;
            Messages.Children.RemoveAt(Messages.Children.Count - 1); // "확인" 말풍선은 지운다
        }
    }

    /// <summary>그림이나 글을 그대로 보여 준다 (메모리에서 푼 내용 미리 보기).</summary>
    public void ShowContent(FrameworkElement content) => Add(content, mine: false);

    /// <summary>읽기만 하는 목록 (자세히 보기).</summary>
    public void ShowList(IEnumerable<string> lines, int max = 200)
    {
        var all = lines.ToList();
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Brush.TextSecondary") };
        tb.Text = string.Join("\n", all.Take(max)) + (all.Count > max ? Foldy.Core.Localization.Loc.F("guide.andMore", all.Count - max) : "");
        Add(new ScrollViewer { Content = tb, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, mine: false);
    }

    private Task<string> ChooseAsync((string Id, string Label)[] choices)
    {
        var tcs = _pending = new TaskCompletionSource<string>();
        Choices.Children.Clear();
        foreach (var (id, label) in choices)
        {
            var b = new Button { Content = label, MinWidth = 88, MinHeight = 38, FontSize = FontSize, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(6, 3, 0, 3) }; // 안내창 단추는 크게
            b.Click += (_, _) =>
            {
                Choices.Children.Clear();
                if (EchoChoices)
                {
                    Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, mine: true);
                }

                tcs.TrySetResult(id);
            };
            Choices.Children.Add(b);
        }

        if (Choices.Children.Count > 0)
        {
            ((Button)Choices.Children[0]).IsDefault = true;
        }

        return tcs.Task;
    }

    /// <summary>선택지 없이 기다리는 동안 (찾는 중, 옮기는 중).</summary>
    public void Busy() => Choices.Children.Clear();

    /// <summary>보여 준 말풍선 하나를 없앤다 (다 끝난 진행 안내는 스스로 사라지게).</summary>
    public void Remove(FrameworkElement content)
    {
        if (content.Parent is Border bubble && Messages.Children.Contains(bubble))
        {
            bubble.Child = null;
            Messages.Children.Remove(bubble);
        }
    }

    private void Add(FrameworkElement content, bool mine)
    {
        if (mine && content is TextBlock t)
        {
            t.Foreground = (Brush)FindResource("Brush.SelectionText");
        }

        var bubble = new Border
        {
            Child = content,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = mine ? new Thickness(80, 4, 0, 4) : new Thickness(0, 4, 60, 4),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = (Brush)FindResource(mine ? "Brush.Selection" : "Brush.SurfaceAlt"),
            BorderBrush = (Brush)FindResource("Brush.Border"),
            BorderThickness = new Thickness(mine ? 0 : 1),
        };
        Messages.Children.Add(bubble);
        Scroller.ScrollToEnd();
    }
}
