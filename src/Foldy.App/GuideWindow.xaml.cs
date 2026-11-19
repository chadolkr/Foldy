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
        Closed += (_, _) => _pending?.TrySetResult(ClosedChoice);
    }

    public bool IsClosed { get; private set; }

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
    public async Task<(string Choice, IReadOnlyList<T> Picked)> PickAsync<T>(string text, IEnumerable<(T Item, string Label, bool Selected)> items, params (string Id, string Label)[] choices)
    {
        Say(text);
        var rows = items.Select(i => (i.Item, Box: new CheckBox { Content = new TextBlock { Text = i.Label, TextWrapping = TextWrapping.Wrap }, IsChecked = i.Selected, Margin = new Thickness(0, 2, 0, 2) })).ToList();
        var panel = new StackPanel();
        var toggle = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("모두 고르기 / 모두 풀기"));
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

    /// <summary>읽기만 하는 목록 (자세히 보기).</summary>
    public void ShowList(IEnumerable<string> lines, int max = 200)
    {
        var all = lines.ToList();
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Brush.TextSecondary") };
        tb.Text = string.Join("\n", all.Take(max)) + (all.Count > max ? $"\n… 외 {all.Count - max:N0}개" : "");
        Add(new ScrollViewer { Content = tb, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, mine: false);
    }

    private Task<string> ChooseAsync((string Id, string Label)[] choices)
    {
        var tcs = _pending = new TaskCompletionSource<string>();
        Choices.Children.Clear();
        foreach (var (id, label) in choices)
        {
            var b = new Button { Content = label, MinWidth = 80, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
            b.Click += (_, _) =>
            {
                Choices.Children.Clear();
                Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, mine: true);
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
