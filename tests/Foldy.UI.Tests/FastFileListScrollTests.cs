using System.Windows;
using Foldy.UI.Controls;

namespace Foldy.UI.Tests;

/// <summary>
/// 고른 항목이 화면 안에 보인다. 찾기 창에서 Ctrl+Enter로 "들어 있는 폴더 보기"를 하면 찾은 파일이
/// 스크롤해야 보이는 일이 있었다 (사용자 2026-11-26).
/// </summary>
public class FastFileListScrollTests
{
    private const double Row = 20;

    private static FastFileList Make(int n)
    {
        var list = new FastFileList { RowHeight = Row };
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
        var top = index * Row;
        return top >= list.VerticalOffset && top + Row <= list.VerticalOffset + list.ViewportHeight;
    }

    [Fact]
    public void Item_chosen_before_the_list_is_on_screen_is_shown_once_it_is()
    {
        Sta.Run(() =>
        {
            var list = Make(500);
            list.FocusIndex(300, select: true, center: true); // 창이 막 떠서 아직 높이를 모른다
            Layout(list, 222);
            Visible(list, 300).Should().BeTrue("창이 뜬 뒤에 고른 항목이 화면 안에 있어야 한다");
        });
    }

    [Fact]
    public void Far_item_is_brought_to_the_middle()
    {
        Sta.Run(() =>
        {
            var list = Make(500);
            Layout(list, 222);
            list.FocusIndex(300, select: true, center: true);
            Visible(list, 300).Should().BeTrue();
            var middle = list.VerticalOffset + list.ViewportHeight / 2;
            Math.Abs(300 * Row + Row / 2 - middle).Should().BeLessThan(Row, "맨 아래 줄에 걸리지 않고 가운데쯤에");
        });
    }

    [Fact]
    public void Visible_item_does_not_move_and_arrow_keys_scroll_to_the_edge()
    {
        Sta.Run(() =>
        {
            var list = Make(500);
            Layout(list, 222);
            list.FocusIndex(3, select: true, center: true);
            list.VerticalOffset.Should().Be(0, "이미 보이면 움직이지 않는다");

            list.FocusIndex(40); // 키보드로 옮길 때는 예전처럼 가장자리까지만
            Visible(list, 40).Should().BeTrue();
            (40 * Row + Row).Should().BeApproximately(list.VerticalOffset + list.ViewportHeight, 0.01);
        });
    }

    [Fact]
    public void Item_near_the_end_stays_visible()
    {
        Sta.Run(() =>
        {
            var list = Make(30);
            Layout(list, 222);
            list.FocusIndex(29, select: true, center: true);
            Visible(list, 29).Should().BeTrue("가운데로 못 올 만큼 끝에 있으면 맨 아래에");
        });
    }
}
