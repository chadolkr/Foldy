using System.Windows;
using Foldy.UI.Controls;

namespace Foldy.UI.Tests;

/// <summary>끌어 놓을 때 커서 아래 줄을 찾고, 놓을 폴더 줄을 표시한다 (같은 창 안에서 하위 폴더로 옮기기).</summary>
public class FastFileListDropTargetTests
{
    private static FastFileList Make(int n)
    {
        var list = new FastFileList();
        list.Columns.Add(new FastColumn("이름", 200, o => (string)o));
        list.ItemsSource = Enumerable.Range(0, n).Select(i => "폴더" + i).ToList();
        return list;
    }

    [Fact]
    public void The_row_under_the_cursor_is_found()
    {
        Sta.Run(() =>
        {
            var list = Make(5);
            var row2 = list.RowRect(2);
            list.IndexAtPoint(new Point(10, row2.Y + row2.Height / 2)).Should().Be(2);
            list.IndexAtPoint(new Point(10, 1)).Should().Be(-1, "머리글 위는 줄이 아니다");
            list.IndexAtPoint(new Point(10, list.RowRect(9).Y)).Should().Be(-1, "목록 아래 빈 곳은 지금 연 폴더");
        });
    }

    [Fact]
    public void Drop_target_row_is_kept_only_inside_the_list()
    {
        Sta.Run(() =>
        {
            var list = Make(3);
            list.DropTargetIndex = 1;
            list.DropTargetIndex.Should().Be(1);
            list.DropTargetIndex = 7;
            list.DropTargetIndex.Should().Be(-1);
            list.DropTargetIndex = 2;
            list.ItemsSource = new List<string> { "새 목록" };
            list.DropTargetIndex.Should().Be(-1, "목록이 바뀌면 표시를 지운다");
        });
    }
}
