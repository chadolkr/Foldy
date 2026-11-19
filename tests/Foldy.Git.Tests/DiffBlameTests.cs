namespace Foldy.Git.Tests;

public class DiffParserTests
{
    private const string Sample =
        "diff --git a/a.txt b/a.txt\nindex 111..222 100644\n--- a/a.txt\n+++ b/a.txt\n" +
        "@@ -1,4 +1,5 @@ void Foo()\n one\n-two\n+TWO\n+two-b\n three\n four\n" +
        "@@ -10 +11 @@\n-ten\n\\ No newline at end of file\n+TEN\n";

    [Fact]
    public void Parses_hunks_line_numbers_and_kinds()
    {
        var d = GitDiffParser.Parse(Sample);
        d.IsBinary.Should().BeFalse();
        d.AddedCount.Should().Be(3);
        d.RemovedCount.Should().Be(2);
        d.Lines[0].Kind.Should().Be(DiffLineKind.Hunk);
        d.Lines[0].Text.Should().StartWith("@@ -1,4 +1,5 @@");
        d.Lines[1].Should().Be(new DiffLine(DiffLineKind.Context, 1, 1, "one"));
        d.Lines[2].Should().Be(new DiffLine(DiffLineKind.Removed, 2, null, "two"));
        d.Lines[3].Should().Be(new DiffLine(DiffLineKind.Added, null, 2, "TWO"));
        d.Lines[4].Should().Be(new DiffLine(DiffLineKind.Added, null, 3, "two-b"));
        d.Lines[5].Should().Be(new DiffLine(DiffLineKind.Context, 3, 4, "three"));
        var second = d.Lines.Where(l => l.Kind == DiffLineKind.Hunk).Last();
        second.Text.Should().StartWith("@@ -10 +11 @@");
        d.Lines.Should().Contain(l => l.Kind == DiffLineKind.Meta && l.Text.StartsWith("\\ No newline"));
        d.Lines.Last().Should().Be(new DiffLine(DiffLineKind.Added, null, 11, "TEN"));
    }

    [Fact]
    public void Removed_line_that_looks_like_header_is_content()
    {
        // 내용이 "-- x" 인 줄을 지우면 "--- x" 로 보인다
        var d = GitDiffParser.Parse("@@ -1,2 +1 @@\n--- x\n keep\n");
        d.Lines[1].Should().Be(new DiffLine(DiffLineKind.Removed, 1, null, "-- x"));
        d.Lines[2].Kind.Should().Be(DiffLineKind.Context);
    }

    [Fact]
    public void Crlf_is_trimmed_and_empty_context_line_is_kept()
    {
        var d = GitDiffParser.Parse("@@ -1,2 +1,2 @@\r\n a\r\n\r\n");
        d.Lines[1].Text.Should().Be("a");
        d.Lines[2].Should().Be(new DiffLine(DiffLineKind.Context, 2, 2, ""));
    }

    [Theory]
    [InlineData("diff --git a/x.png b/x.png\nindex 1..2 100644\nBinary files a/x.png and b/x.png differ\n")]
    [InlineData("diff --git a/x b/x\nGIT binary patch\nliteral 3\n")]
    public void Binary_files_are_flagged(string text)
    {
        var d = GitDiffParser.Parse(text);
        d.IsBinary.Should().BeTrue();
        d.Lines.Should().BeEmpty();
    }

    [Fact]
    public void Empty_or_mode_only_is_empty()
    {
        GitDiffParser.Parse("").IsEmpty.Should().BeTrue();
        GitDiffParser.Parse("diff --git a/x b/x\nold mode 100644\nnew mode 100755\n").IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Truncates_at_max_lines_but_counts_all()
    {
        var sb = new System.Text.StringBuilder("@@ -0,0 +1,100 @@\n");
        for (var i = 0; i < 100; i++) sb.Append("+line").Append(i).Append('\n');
        var d = GitDiffParser.Parse(sb.ToString(), 30);
        d.Lines.Should().HaveCount(30);
        d.Truncated.Should().BeTrue();
        d.TotalLines.Should().Be(101);
        d.AddedCount.Should().Be(100);
        GitDiffParser.Parse(sb.ToString()).Truncated.Should().BeFalse();
    }

    [Fact]
    public void Side_by_side_pairs_removed_with_added()
    {
        var d = GitDiffParser.Parse(Sample);
        var rows = GitDiffParser.BuildSideBySide(d.Lines);
        rows[0].IsHeader.Should().BeTrue();
        rows[1].Left!.Text.Should().Be("one");
        rows[1].Right!.Text.Should().Be("one");
        // -two / +TWO 한 쌍, +two-b 는 오른쪽만
        rows[2].Left!.Text.Should().Be("two");
        rows[2].Right!.Text.Should().Be("TWO");
        rows[3].Left.Should().BeNull();
        rows[3].Right!.Text.Should().Be("two-b");
    }

    [Fact]
    public void Diff_args_per_target()
    {
        GitService.BuildDiffArgs(new GitDiffTarget(GitDiffKind.Unstaged, "a b.txt"))
            .Should().Equal("diff", "--no-color", "--no-ext-diff", "--", "a b.txt");
        GitService.BuildDiffArgs(new GitDiffTarget(GitDiffKind.Staged, "n.txt", "o.txt"))
            .Should().Equal("diff", "--no-color", "--no-ext-diff", "-M", "--cached", "--", "o.txt", "n.txt");
        GitService.BuildDiffArgs(new GitDiffTarget(GitDiffKind.Commit, "a.txt", Hash: "abc123"))
            .Should().Equal("show", "--no-color", "--no-ext-diff", "--format=", "-m", "--first-parent", "-M", "abc123", "--", "a.txt");
        GitService.BuildDiffArgs(new GitDiffTarget(GitDiffKind.Unstaged, "new.txt", Untracked: true))
            .Should().Contain("--no-index").And.Contain("/dev/null");
    }
}

public class BlameParserTests
{
    private const string H1 = "1111111111111111111111111111111111111111";
    private const string H2 = "2222222222222222222222222222222222222222";
    private const string Zero = "0000000000000000000000000000000000000000";

    private static readonly string Porcelain =
        $"{H1} 1 1 2\nauthor Kim\nauthor-mail <k@x>\nauthor-time 1700000000\nauthor-tz +0900\ncommitter Kim\ncommitter-time 1700000000\ncommitter-tz +0900\nsummary first\nboundary\nfilename a.txt\n\tline one\n" +
        $"{H1} 2 2\n\tline two\n" +
        $"{H2} 3 3 1\nauthor Lee\nauthor-mail <l@x>\nauthor-time 1700086400\nauthor-tz -0330\nsummary second change\nprevious {H1} a.txt\nfilename a.txt\n\tline three\n" +
        $"{Zero} 4 4 1\nauthor Not Committed Yet\nauthor-time 1700090000\nauthor-tz +0000\nsummary Version of a.txt from a.txt\nfilename a.txt\n\twip\r\n" +
        $"{H1} 3 5 1\n\tback to first\n";

    [Fact]
    public void Parses_lines_authors_and_blocks()
    {
        var l = GitBlameParser.Parse(Porcelain);
        l.Should().HaveCount(5);
        l[0].Should().Match<BlameLine>(x => x.LineNo == 1 && x.Hash == H1 && x.Author == "Kim" && x.Summary == "first" && x.Text == "line one" && x.IsBoundary && x.IsBlockStart && x.BlockIndex == 0);
        l[0].Date.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        l[0].Date.Offset.Should().Be(TimeSpan.FromHours(9));
        l[0].ShortHash.Should().Be("1111111");
        // 같은 커밋이 이어지면 같은 덩어리, 첫 줄만 시작
        l[1].Should().Match<BlameLine>(x => x.Author == "Kim" && x.Text == "line two" && !x.IsBlockStart && x.BlockIndex == 0);
        l[2].Should().Match<BlameLine>(x => x.Author == "Lee" && x.IsBlockStart && x.BlockIndex == 1 && !x.IsBoundary);
        l[2].Date.Offset.Should().Be(-new TimeSpan(3, 30, 0));
        // 커밋하지 않은 줄 (CRLF 는 잘림)
        l[3].Should().Match<BlameLine>(x => x.IsUncommitted && x.Text == "wip" && x.BlockIndex == 2);
        // 이전 커밋이 다시 나오면 새 덩어리, 정보는 처음 것을 재사용
        l[4].Should().Match<BlameLine>(x => x.Hash == H1 && x.Author == "Kim" && x.IsBlockStart && x.BlockIndex == 3 && x.LineNo == 5);
    }

    [Fact]
    public void Empty_output_gives_no_lines() => GitBlameParser.Parse("").Should().BeEmpty();
}
