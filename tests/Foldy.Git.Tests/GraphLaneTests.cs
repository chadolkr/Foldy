namespace Foldy.Git.Tests;

public class GraphLaneTests
{
    private static GraphRow[] Run(params (string hash, string[] parents)[] commits)
    {
        var calc = new GraphLaneCalculator();
        return commits.Select(c => calc.Next(c.hash, c.parents)).ToArray();
    }

    [Fact]
    public void Linear_history_stays_in_column_zero()
    {
        var rows = Run(("c", ["b"]), ("b", ["a"]), ("a", []));
        rows.Select(r => r.Column).Should().AllBeEquivalentTo(0);
        rows.Select(r => r.LaneCount).Should().AllBeEquivalentTo(1);
        rows[0].IncomingColumns.Should().BeEmpty();
        rows[1].IncomingColumns.Should().Equal(0);
        rows[2].ParentColumns.Should().BeEmpty();
    }

    [Fact]
    public void Merge_opens_second_lane_and_joins_back()
    {
        // M(B,C) -> B(A), C(A), A
        var rows = Run(("M", ["B", "C"]), ("B", ["A"]), ("C", ["A"]), ("A", []));
        rows[0].Column.Should().Be(0);
        rows[0].ParentColumns.Should().Equal(0, 1);
        rows[1].Column.Should().Be(0);
        rows[1].PassThroughColumns.Should().Equal(1);
        rows[2].Column.Should().Be(1);
        rows[2].PassThroughColumns.Should().Equal(0);
        rows[3].Column.Should().Be(0);
        rows[3].IncomingColumns.Should().Equal(0, 1);
    }

    [Fact]
    public void Unrelated_branch_tip_takes_new_column()
    {
        // 두 브랜치 끝 X, Y 가 같은 부모 R 을 가짐
        var rows = Run(("X", ["R"]), ("Y", ["R"]), ("R", []));
        rows[0].Column.Should().Be(0);
        rows[1].Column.Should().Be(1);
        rows[1].IncomingColumns.Should().BeEmpty();
        rows[1].PassThroughColumns.Should().Equal(0);
        rows[2].IncomingColumns.Should().Equal(0, 1);
    }

    [Fact]
    public void Freed_column_is_reused()
    {
        // 머지로 열린 열 1 이 닫힌 뒤 새 브랜치 끝이 그 열을 재사용
        var rows = Run(("M", ["B", "C"]), ("C", ["A"]), ("B", ["A"]), ("A", []), ("Z", []));
        rows[3].Column.Should().Be(0);
        rows[4].Column.Should().Be(0);
    }

    [Fact]
    public void State_carries_across_pages()
    {
        var calc = new GraphLaneCalculator();
        calc.Next("M", ["B", "C"]);
        calc.OpenLaneCount.Should().Be(2);
        var next = calc.Next("B", ["A"]); // 다음 페이지에서 이어서 계산
        next.PassThroughColumns.Should().Equal(1);
    }

    [Fact]
    public void Compute_helper_matches_stepwise()
    {
        var nodes = new[]
        {
            new CommitNode("M", ["B", "C"], "a", "a@x", default, "m", []),
            new CommitNode("B", ["A"], "a", "a@x", default, "b", []),
        };
        GraphLaneCalculator.Compute(nodes).Should().HaveCount(2);
    }
}
