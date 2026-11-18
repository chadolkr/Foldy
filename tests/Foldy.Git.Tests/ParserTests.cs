using Foldy.Core.FileSystem;

namespace Foldy.Git.Tests;

public class ParserTests
{
    // 실제 `git status --porcelain=v2 -z --branch` 출력 형태 (NUL 구분)
    private static string Z(params string[] records) => string.Join('\0', records) + '\0';

    private const string Oid = "1111111111111111111111111111111111111111";
    private const string Zero = "0000000000000000000000000000000000000000";

    [Fact]
    public void Status_parses_branch_header_and_ahead_behind()
    {
        var s = GitStatusParser.Parse(Z(
            $"# branch.oid {Oid}", "# branch.head main", "# branch.upstream origin/main", "# branch.ab +2 -1"));
        s.BranchName.Should().Be("main");
        s.HeadOid.Should().Be(Oid);
        s.Upstream.Should().Be("origin/main");
        s.Ahead.Should().Be(2);
        s.Behind.Should().Be(1);
        s.IsDetached.Should().BeFalse();
        s.Files.Should().BeEmpty();
    }

    [Fact]
    public void Status_initial_repo_and_detached_head()
    {
        var init = GitStatusParser.Parse(Z("# branch.oid (initial)", "# branch.head main"));
        init.HeadOid.Should().BeNull();
        var det = GitStatusParser.Parse(Z($"# branch.oid {Oid}", "# branch.head (detached)"));
        det.IsDetached.Should().BeTrue();
        det.BranchName.Should().BeNull();
        det.HasUpstream.Should().BeFalse();
    }

    [Fact]
    public void Status_parses_all_entry_kinds()
    {
        var s = GitStatusParser.Parse(Z(
            "# branch.oid " + Oid, "# branch.head main",
            $"1 .M N... 100644 100644 100644 {Oid} {Oid} src/한글 파일.cs",
            $"1 A. N... 000000 100644 100644 {Zero} {Oid} added.txt",
            $"1 MM N... 100644 100644 100644 {Oid} {Oid} both.txt",
            $"1 .D N... 100644 100644 000000 {Oid} {Oid} gone.txt",
            $"2 R. N... 100644 100644 100644 {Oid} {Oid} R100 new name.txt", "old name.txt",
            $"u UU N... 100644 100644 100644 100644 {Oid} {Oid} {Oid} conflict.txt",
            "? untracked.txt",
            "? newdir/",
            "! ignored.log"));

        s.Files.Should().HaveCount(9);
        s.Files[0].Path.Should().Be("src/한글 파일.cs");
        s.Files[0].ToBadge().Should().Be(GitFileStatus.Modified);
        s.Files[1].ToBadge().Should().Be(GitFileStatus.Added);
        s.Files[1].Staged.Should().Be(GitChange.Added);
        s.Files[2].Staged.Should().Be(GitChange.Modified);
        s.Files[2].Unstaged.Should().Be(GitChange.Modified);
        s.Files[3].Unstaged.Should().Be(GitChange.Deleted);
        s.Files[4].Path.Should().Be("new name.txt");
        s.Files[4].OrigPath.Should().Be("old name.txt");
        s.Files[4].Staged.Should().Be(GitChange.Renamed);
        s.Files[5].IsConflicted.Should().BeTrue();
        s.Files[5].ToBadge().Should().Be(GitFileStatus.Conflicted);
        s.Files[6].ToBadge().Should().Be(GitFileStatus.Untracked);
        s.Files[7].Path.Should().Be("newdir/");
        s.HasConflicts.Should().BeTrue();
        s.ChangeCount.Should().Be(8); // ignored 제외
    }

    [Fact]
    public void Status_ignored_is_excluded_from_change_count()
    {
        var s = GitStatusParser.Parse(Z("# branch.head main", "! a.log", "? b.txt"));
        s.ChangeCount.Should().Be(1);
        s.Files[0].ToBadge().Should().Be(GitFileStatus.Ignored);
    }

    [Fact]
    public void BadgeMap_propagates_to_folders_and_inherits_untracked()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo-x");
        var s = GitStatusParser.Parse(Z(
            "# branch.head main",
            $"1 .M N... 100644 100644 100644 {Oid} {Oid} src/a/b.cs",
            "? newdir/",
            "! build/"));
        var map = new GitBadgeMap(root, s);
        map.Get(Path.Combine(root, "src", "a", "b.cs")).Should().Be(GitFileStatus.Modified);
        map.Get(Path.Combine(root, "src", "a")).Should().Be(GitFileStatus.Modified);
        map.Get(Path.Combine(root, "src")).Should().Be(GitFileStatus.Modified);
        map.Get(Path.Combine(root, "newdir", "deep", "x.txt")).Should().Be(GitFileStatus.Untracked);
        map.Get(Path.Combine(root, "build", "o.obj")).Should().Be(GitFileStatus.Ignored);
        map.Get(Path.Combine(root, "clean.txt")).Should().Be(GitFileStatus.None);
    }

    [Fact]
    public void Log_parses_records_refs_and_parents()
    {
        const string h1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", h2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", h3 = "cccccccccccccccccccccccccccccccccccccccc";
        var us = '\u001f';
        var output =
            $"{h1}{us}{h2} {h3}{us}홍길동{us}a@x.com{us}1700000000{us}HEAD -> main, origin/main, origin/HEAD, tag: v1.0{us}Merge 한글 제목" + '\0' +
            $"{h2}{us}{us}Bob{us}b@x.com{us}1699999999{us}{us}root" + '\0';
        var list = GitLogParser.Parse(output);
        list.Should().HaveCount(2);
        list[0].Parents.Should().Equal(h2, h3);
        list[0].IsMerge.Should().BeTrue();
        list[0].AuthorName.Should().Be("홍길동");
        list[0].Subject.Should().Be("Merge 한글 제목");
        list[0].Date.ToUnixTimeSeconds().Should().Be(1700000000);
        list[0].Refs.Select(r => (r.Name, r.Kind)).Should().Equal(
            ("main", GitRefKind.LocalBranch), ("origin/main", GitRefKind.RemoteBranch), ("v1.0", GitRefKind.Tag));
        list[0].Refs[0].IsCurrentHead.Should().BeTrue();
        list[1].Parents.Should().BeEmpty();
        list[1].Refs.Should().BeEmpty();
        list[1].ShortHash.Should().Be("bbbbbbb");
    }

    [Fact]
    public void Log_args_include_paging_and_filters()
    {
        var args = GitLogParser.BuildArgs(new LogFilter { Skip = 1000, Count = 500, Author = "kim", Path = "a/b.txt" });
        args.Should().Contain("--topo-order").And.Contain("-z").And.Contain("--skip=1000").And.Contain("-n500")
            .And.Contain("--author=kim").And.Contain("--all");
        args[^2].Should().Be("--");
        args[^1].Should().Be("a/b.txt");
    }

    [Theory]
    [InlineData("Receiving objects:  45% (45/100), 1.2 MiB | 1 MiB/s", "Receiving objects", 45)]
    [InlineData("remote: Counting objects: 100% (5/5), done.", "Counting objects", 100)]
    [InlineData("Resolving deltas:   7% (1/14)", "Resolving deltas", 7)]
    [InlineData("Compressing objects: 0% (0/3)", "Compressing objects", 0)]
    public void Progress_lines_are_parsed(string line, string stage, int pct)
    {
        ProgressParser.TryParse(line, out var p).Should().BeTrue();
        p.Stage.Should().Be(stage);
        p.Percent.Should().Be(pct);
    }

    [Theory]
    [InlineData("From github.com:foo/bar")]
    [InlineData("remote: Enumerating objects: 12, done.")]
    [InlineData("")]
    public void Non_progress_lines_are_ignored(string line)
        => ProgressParser.TryParse(line, out _).Should().BeFalse();

    [Fact]
    public void Classify_detects_dubious_ownership_with_path()
    {
        var err = "fatal: detected dubious ownership in repository at 'C:/x/y'\n" +
                  "To add an exception for this directory, call:\n\n\tgit config --global --add safe.directory C:/x/y\n";
        var r = GitCli.Classify(128, "", err);
        r.Failure.Should().Be(GitFailure.DubiousOwnership);
        r.SafeDirectoryCandidate.Should().Be("C:/x/y");
    }

    [Theory]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git", GitFailure.NotARepository)]
    [InlineData("fatal: Authentication failed for 'https://x'", GitFailure.AuthenticationFailed)]
    [InlineData("fatal: could not read Username for 'https://x': terminal prompts disabled", GitFailure.AuthenticationFailed)]
    [InlineData("error: Pulling is not possible because you have unmerged files.", GitFailure.MergeConflict)]
    [InlineData("something else", GitFailure.Other)]
    public void Classify_maps_stderr(string stderr, GitFailure expected)
        => GitCli.Classify(1, "", stderr).Failure.Should().Be(expected);

    [Fact]
    public void Branch_and_stash_parsers()
    {
        var us = '\u001f';
        var b = GitService.ParseBranches(
            $"refs/heads/main{us}*{us}origin/main{us}ahead 2, behind 1{us}abc\n" +
            $"refs/heads/dev{us} {us}{us}{us}def\n" +
            $"refs/remotes/origin/HEAD{us} {us}{us}{us}abc\n" +
            $"refs/remotes/origin/main{us} {us}{us}{us}abc\n");
        b.Should().HaveCount(3);
        b[0].IsCurrent.Should().BeTrue();
        (b[0].Ahead, b[0].Behind).Should().Be((2, 1));
        b[1].Upstream.Should().BeNull();
        b[2].IsRemote.Should().BeTrue();

        var st = GitService.ParseStashes($"stash@{{0}}{us}h0{us}1700000000{us}On main: wip\0");
        st.Should().ContainSingle().Which.Message.Should().Be("On main: wip");
    }
}
