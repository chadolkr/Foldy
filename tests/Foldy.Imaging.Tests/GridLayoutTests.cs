using System.Windows;
using System.Windows.Controls;
using Foldy.UI.Imaging;

namespace Foldy.Imaging.Tests;

public class GridLayoutTests
{
    // 칸 = 160 + 2×6 = 172 폭, 160 + 18 + 3×6 = 196 높이, 간격 8, 여백 8
    private static GridLayout Make(double width = 800, int count = 100) => new(width, 160, count, 18);

    [Fact]
    public void Columns_rows_and_extent_follow_viewport_width()
    {
        var g = Make();
        g.CellWidth.Should().Be(172);
        g.CellHeight.Should().Be(196);
        g.Columns.Should().Be(4); // (800 - 16 + 8) / 180 = 4.4
        g.Rows.Should().Be(25);
        g.ExtentHeight.Should().Be(2 * 8 + 25 * 196 + 24 * 8);

        Make(width: 50).Columns.Should().Be(1); // 좁아도 최소 한 열
        Make(count: 0).ExtentHeight.Should().Be(0);
    }

    [Fact]
    public void Index_to_rect_and_back_round_trips()
    {
        var g = Make();
        for (var i = 0; i < g.Count; i++)
        {
            var r = g.CellRect(i);
            g.IndexAt(new Point(r.X + 1, r.Y + 1)).Should().Be(i);
            g.IndexAt(new Point(r.Right - 1, r.Bottom - 1)).Should().Be(i);
            var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            g.IndexAt(center).Should().Be(i);
        }

        g.CellRect(5).Should().Be(new Rect(8 + 1 * 180, 8 + 1 * 204, 172, 196));
        g.ThumbnailRect(0).Should().Be(new Rect(14, 14, 160, 160));
        g.LabelRect(0).Top.Should().Be(8 + 12 + 160);
    }

    [Fact]
    public void Gaps_margins_and_empty_tail_hit_nothing()
    {
        var g = Make(count: 6); // 2줄째는 2칸만
        g.IndexAt(new Point(3, 3)).Should().Be(-1);                    // 여백
        g.IndexAt(new Point(8 + 172 + 4, 20)).Should().Be(-1);         // 열 간격
        g.IndexAt(new Point(20, 8 + 196 + 4)).Should().Be(-1);         // 행 간격
        g.IndexAt(new Point(8 + 3 * 180 + 10, 8 + 204 + 10)).Should().Be(-1); // 빈 자리
        g.IndexAt(new Point(790, 20)).Should().Be(-1);                 // 오른쪽 남는 폭
    }

    [Fact]
    public void Visible_range_covers_partially_visible_rows()
    {
        var g = Make();
        g.VisibleRange(0, 600).Should().Be((0, 11));        // 행 0~2 (2행은 일부)
        g.VisibleRange(204, 196).Should().Be((4, 7));       // 정확히 1행만
        g.VisibleRange(210, 196).Should().Be((4, 7));       // 2행 위쪽(416)에는 아직 닿지 않음
        g.VisibleRange(300, 196).Should().Be((4, 11));      // 1행 아래쪽 + 2행 위쪽
        g.VisibleRange(100_000, 600).Should().Be((0, -1));  // 범위 밖
        g.VisibleRange(g.ExtentHeight - 300, 300).Last.Should().Be(99);
    }

    [Fact]
    public void Keyboard_moves_by_columns_and_stops_at_edges()
    {
        var g = Make(count: 10); // 4열: 0..3 / 4..7 / 8..9
        g.Move(-1, 1, 0).Should().Be(0);
        g.Move(0, -1, 0).Should().Be(0);
        g.Move(1, 0, 1).Should().Be(5);
        g.Move(6, 0, 1).Should().Be(9);  // 마지막 줄이 짧으면 마지막 항목으로
        g.Move(9, 0, 1).Should().Be(9);
        g.Move(2, 0, -1).Should().Be(2); // 맨 윗줄
        g.Move(9, 1, 0).Should().Be(9);
        g.RowsPerPage(600).Should().Be(2);
    }

    [Fact]
    public void Ctrl_wheel_steps_between_presets()
    {
        ImageGridView.StepSize(96, 1).Should().Be(160);
        ImageGridView.StepSize(160, 1).Should().Be(256);
        ImageGridView.StepSize(256, 1).Should().Be(256);
        ImageGridView.StepSize(256, -1).Should().Be(160);
        ImageGridView.StepSize(96, -1).Should().Be(96);
        ImageGridView.StepSize(200, -1).Should().Be(160);
    }

    [Fact]
    public void Grid_view_lays_out_inside_scroll_viewer_and_selects()
    {
        Sta.Run(() =>
        {
            var items = Enumerable.Range(0, 50).Select(i => $@"C:\nowhere\img{i:000}.jpg").ToList();
            var grid = new ImageGridView { ItemsSource = items, ThumbnailSize = 96, AllowContentDecode = false };
            var sv = new ScrollViewer { Content = grid, CanContentScroll = true, VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
            sv.Measure(new Size(600, 400));
            sv.Arrange(new Rect(0, 0, 600, 400));

            grid.Layout.Columns.Should().BeGreaterThan(1);
            grid.ExtentHeight.Should().Be(grid.Layout.ExtentHeight);
            grid.ScrollOwner.Should().BeSameAs(sv);

            grid.SelectIndex(30);
            grid.FocusedIndex.Should().Be(30);
            grid.SelectedCount.Should().Be(1);
            grid.VerticalOffset.Should().BeGreaterThan(0); // 보이게 스크롤

            grid.ThumbnailSize = 256;
            grid.Layout.ThumbnailSize.Should().Be(256);
            grid.SelectAll();
            grid.SelectedCount.Should().Be(50);
            grid.InfoAt(3).Name.Should().Be("img003.jpg");
        });
    }
}
