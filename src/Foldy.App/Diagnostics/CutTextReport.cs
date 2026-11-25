using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Foldy.Core.Localization;

namespace Foldy.App.Diagnostics;

/// <summary>
/// 시험용: 화면 글이 잘리거나 창 밖으로 밀려 안 보이는 곳을 찾는다 (사용자 2026-11-25: "단어나 문장이 길어져 화면이 밀려 안 보이는 게 있는지 점검").
/// FOLDY_CUT_TEXT_REPORT=&lt;파일&gt;일 때만 켜진다. 1.5초마다 열린 창을 모두 살펴, 언어 파일에 있는 글(버튼·제목·설명)이
/// 자기 칸보다 길거나, 감싸는 칸·창 밖으로 나가면 그 파일에 적는다. 파일 이름·경로 같은 사용자 자료는 언어 파일 글이 아니라서 보지 않는다.
/// 스크롤되는 칸 안은 내려서 볼 수 있으니 세로로 넘친 것은 괜찮다.
/// </summary>
internal static class CutTextReport
{
    private const double Slack = 2.0;
    private static readonly SortedSet<string> Found = new(StringComparer.Ordinal);
    private static HashSet<string>? _texts;
    private static string? _path;

    public static void StartIfAsked(Dispatcher dispatcher)
    {
        if (Environment.GetEnvironmentVariable("FOLDY_CUT_TEXT_REPORT") is not { Length: > 0 } path)
        {
            return;
        }

        _path = path;
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher) { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => Scan();
        timer.Start();
    }

    private static void Scan()
    {
        _texts ??= Loc.Table(Loc.Current).Values.Select(v => v.Trim()).Where(v => v.Length > 1).ToHashSet(StringComparer.Ordinal);
        var before = Found.Count;
        foreach (Window w in Application.Current.Windows)
        {
            if (w.IsVisible && w.Content is FrameworkElement root && root.ActualWidth > 0)
            {
                Walk(root, root, w.GetType().Name);
            }
        }

        if (Found.Count != before || !File.Exists(_path))
        {
            try
            {
                File.WriteAllLines(_path!, Found);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Walk(DependencyObject node, FrameworkElement root, string window)
    {
        if (node is UIElement { IsVisible: false })
        {
            return;
        }

        if (node is TextBlock tb)
        {
            Check(tb, root, window);
        }

        var n = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < n; i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i), root, window);
        }
    }

    private static void Check(TextBlock tb, FrameworkElement root, string window)
    {
        var text = tb.Text?.Trim();
        if (string.IsNullOrEmpty(text) || !_texts!.Contains(text) || tb.ActualWidth <= 0 || tb.Opacity <= 0.01)
        {
            return;
        }

        var ft = new FormattedText(
            tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
            tb.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(tb).PixelsPerDip);
        var padW = tb.Padding.Left + tb.Padding.Right;
        var padH = tb.Padding.Top + tb.Padding.Bottom;
        string? why = null;
        if (tb.TextWrapping == TextWrapping.NoWrap)
        {
            if (ft.WidthIncludingTrailingWhitespace + padW > tb.ActualWidth + Slack)
            {
                why = $"칸보다 길어요 (글 {ft.WidthIncludingTrailingWhitespace + padW:0} > 칸 {tb.ActualWidth:0})";
            }
        }
        else
        {
            ft.MaxTextWidth = Math.Max(1, tb.ActualWidth - padW);
            if (ft.Height + padH > tb.ActualHeight + Slack && !InScroller(tb))
            {
                why = $"아래가 잘려요 (글 높이 {ft.Height + padH:0} > 칸 {tb.ActualHeight:0})";
            }
        }

        why ??= OutsideClip(tb, root);
        if (why is not null)
        {
            Found.Add($"{Loc.Current} {window}: \"{text}\" {why}");
        }
    }

    private static bool InScroller(DependencyObject e)
    {
        for (var p = VisualTreeHelper.GetParent(e); p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is ScrollContentPresenter)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>글 칸이 그것을 감싼 칸(크기가 정해져 잘라 내는 칸)이나 창 밖으로 나가는지. 스크롤 칸 안이면 가로로 나간 것만 본다.</summary>
    private static string? OutsideClip(TextBlock tb, FrameworkElement root)
    {
        Rect me;
        try
        {
            me = tb.TransformToAncestor(root).TransformBounds(new Rect(tb.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var scrolled = false;
        for (var p = VisualTreeHelper.GetParent(tb); p is not null && !ReferenceEquals(p, root); p = VisualTreeHelper.GetParent(p))
        {
            if (p is ScrollContentPresenter)
            {
                scrolled = true;
                continue;
            }

            if (p is not FrameworkElement fe || fe.ActualWidth <= 0)
            {
                continue;
            }

            var clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(fe)?.Bounds;
            if (clip is null && !fe.ClipToBounds)
            {
                continue;
            }

            Rect box;
            try
            {
                box = fe.TransformToAncestor(root).TransformBounds(clip ?? new Rect(fe.RenderSize));
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (me.Right > box.Right + Slack || me.Left < box.Left - Slack || (!scrolled && me.Bottom > box.Bottom + Slack))
            {
                return $"{fe.GetType().Name}{(fe.Name is { Length: > 0 } n ? "#" + n : "")} 밖으로 나가요";
            }
        }

        if (me.Right > root.ActualWidth + Slack || (!scrolled && me.Bottom > root.ActualHeight + Slack))
        {
            return "창 밖으로 나가요";
        }

        return null;
    }
}
