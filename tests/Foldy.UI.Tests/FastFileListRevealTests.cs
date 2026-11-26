using System.Windows;
using Foldy.UI.Controls;

namespace Foldy.UI.Tests;

/// <summary>
/// 무엇이든 찾기에서 Ctrl+Enter로 들어 있는 폴더를 열면 찾은 파일이 스크롤 없이 화면에 보여야 한다 (1.14.4).
/// 창을 막 띄워 목록 크기가 아직 없을 때도, 나중에 크기가 바뀌어도.
/// </summary>
public class FastFileListRevealTests
{
    private static FastFileList Make(int n)
    {
        var list = new FastFileList();
        list.Columns.Add(new FastColumn("이름", 200, o => (string)o));
        list.ItemsSource = Enumerable.Range(0, n).Select(i => "파일" + i).ToList();
        return list;
    }

    private static void Layout(FastFileList list, double height)
    {
        list.Measure(new Size(400, height));
        list.Arrange(new Rect(0, 0, 400, height));
    }

    private static bool Visible(FastFileList list, int index)
    {
        var top = index * list.RowHeight;
        return top >= list.VerticalOffset && top + list.RowHeight <= list.VerticalOffset + list.ViewportHeight;
    }

    [Fact]
    public void Found_item_far_below_is_shown_near_the_middle()
    {
        Sta.Run(() =>
        {
            var list = Make(500);
            Layout(list, 300);
            list.RevealIndex(400);
            list.IsSelected(400).Should().BeTrue();
            Visible(list, 400).Should().BeTrue();
            var middle = list.VerticalOffset + list.ViewportHeight / 2;
            Math.Abs(400 * list.RowHeight - middle).Should().BeLessThan(list.RowHeight * 2, "끝 줄에 걸치지 않고 가운데쯤");
        });
    }

    [Fact]
    public void Found_item_is_shown_even_before_the_list_has_a_size()
    {
        Sta.Run(() =>
        {
            var list = Make(500);
            list.RevealIndex(321); // 주 창을 막 띄워 아직 크기가 0
            Layout(list, 120);
            Visible(list, 321).Should().BeTrue();
            Layout(list, 600); // 창이 커져도
            Visible(list, 321).Should().BeTrue();
        });
    }

    [Fact]
    public void After_the_user_scrolls_a_resize_does_not_jump_back()
    {
        Sta.Run(() =>
        {
            var list = Make(500);
            Layout(list, 300);
            list.RevealIndex(400);
            list.SetVerticalOffset(0);
            Layout(list, 250);
            list.VerticalOffset.Should().Be(0);
        });
    }

    [Fact]
    public void Item_already_on_screen_does_not_move()
    {
        Sta.Run(() =>
        {
            var list = Make(500);
            Layout(list, 300);
            list.RevealIndex(2);
            list.VerticalOffset.Should().Be(0);
        });
    }
}
