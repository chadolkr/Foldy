using Foldy.Core.Compare;
using Foldy.Core.History;

namespace Foldy.Core.Tests;

[Collection("VersionHistory")]
public sealed class FolderCompareTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "foldy-cmp-" + Guid.NewGuid().ToString("N"));

    public FolderCompareTests()
    {
        Directory.CreateDirectory(_root);
        VersionHistory.RootOverride = _ => Path.Combine(_root, ".foldy", VersionHistory.HistoryName);
    }

    public void Dispose()
    {
        VersionHistory.RootOverride = null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string L => Path.Combine(_root, "left");

    private string R => Path.Combine(_root, "right");

    private static void Put(string side, string rel, string text, DateTime utc)
    {
        var p = Path.Combine(side, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        File.SetLastWriteTimeUtc(p, utc);
    }

    [Fact]
    public void Compare_finds_one_sided_and_newer_files_and_skips_project_folders()
    {
        Put(L, "same.txt", "a", T0);
        Put(R, "same.txt", "a", T0.AddSeconds(1)); // 2초 안은 같음
        Put(L, "only-left.txt", "l", T0);
        Put(R, Path.Combine("sub", "only-right.txt"), "r", T0);
        Put(L, "doc.txt", "old", T0);
        Put(R, "doc.txt", "new!", T0.AddHours(1));
        Directory.CreateDirectory(Path.Combine(L, "repo", ".git"));
        Put(L, Path.Combine("repo", "code.cs"), "x", T0);

        var diff = FolderCompare.Compare(L, R);
        diff.Select(d => (d.RelPath, d.Kind)).Should().BeEquivalentTo(new[]
        {
            ("only-left.txt", DiffKind.LeftOnly),
            (Path.Combine("sub", "only-right.txt"), DiffKind.RightOnly),
            ("doc.txt", DiffKind.RightNewer),
        });
    }

    [Fact]
    public void Plan_never_overwrites_newer_files_with_older_ones_and_never_deletes()
    {
        Put(L, "doc.txt", "old", T0);
        Put(R, "doc.txt", "new!", T0.AddHours(1));
        Put(L, "only-left.txt", "l", T0);
        var diff = FolderCompare.Compare(L, R);

        FolderCompare.Plan(L, R, diff, MergeDirection.LeftToRight).Select(s => s.RelPath).Should().Equal("only-left.txt");
        FolderCompare.Plan(L, R, diff, MergeDirection.Both).Select(s => s.RelPath).Should().BeEquivalentTo(["only-left.txt", "doc.txt"]);
    }

    [Fact]
    public void A_copy_that_fails_after_preserving_puts_the_old_file_back()
    {
        // 1.0 점검 B1: 이전 버전으로 옮긴 뒤 원본이 열려 있어 복사가 안 되면, 오른쪽 파일이 사라졌다
        Put(L, "a.txt", "left newer", T0.AddHours(2));
        Put(R, "a.txt", "right older", T0.AddHours(1));
        var steps = FolderCompare.Plan(L, R, FolderCompare.Compare(L, R), MergeDirection.LeftToRight);
        steps.Should().ContainSingle();
        var journal = Path.Combine(_root, "j.json");
        (IReadOnlyList<MergeRecord> Done, IReadOnlyList<(string Path, string Reason)> Skipped) r;
        using (new FileStream(Path.Combine(L, "a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            r = FolderCompare.Apply(steps, journal);
        }

        r.Done.Should().BeEmpty();
        r.Skipped.Should().ContainSingle();
        File.ReadAllText(Path.Combine(R, "a.txt")).Should().Be("right older", "복사하지 못했으면 원래 파일이 제자리에 있어야 한다");
        File.Exists(Path.Combine(R, "a.txt.foldy-bak")).Should().BeFalse();
    }

    [Fact]
    public void Conflicts_need_a_choice_and_apply_can_be_undone()
    {
        Put(L, "a.txt", "left edit", T0.AddHours(2));
        Put(R, "a.txt", "right edit!", T0.AddHours(3));
        var diff = FolderCompare.Compare(L, R, lastSyncUtc: T0.AddHours(1));
        diff.Single().Kind.Should().Be(DiffKind.Conflict);
        FolderCompare.Plan(L, R, diff, MergeDirection.Both).Should().BeEmpty("양쪽 다 바뀐 파일은 저절로 고르지 않는다");

        var steps = FolderCompare.Plan(L, R, diff, MergeDirection.Both, new Dictionary<string, bool> { ["a.txt"] = true });
        var journal = Path.Combine(_root, "j.json");
        FolderCompare.Apply(steps, journal).Done.Should().ContainSingle();
        File.ReadAllText(Path.Combine(R, "a.txt")).Should().Be("left edit");

        FolderCompare.Undo(journal).Should().Be(1);
        File.ReadAllText(Path.Combine(R, "a.txt")).Should().Be("right edit!");
    }
}
