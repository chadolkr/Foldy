using Foldy.Core.Managed;
using Foldy.Core.Tags;

namespace Foldy.Core.Tests;

[Collection("FileTags")] // 태그 캐시는 전역이라 하나씩
public sealed class FileTagsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "foldy-tags-" + Guid.NewGuid().ToString("N"));

    public FileTagsTests()
    {
        Directory.CreateDirectory(_root);
        FileTags.Load(Path.Combine(_root, "local.json"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Make(string rel)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
        return p;
    }

    [Fact]
    public void Tags_live_in_the_folders_foldy_md_and_follow_renames()
    {
        var a = Make(@"work\보고서.docx");
        var b = Make(@"work\견적.xlsx");
        FileTags.Toggle([a, b], "작업 완료");
        FileTags.Toggle([a], "반영 완료");

        var md = Path.Combine(_root, "work", ManagedFolders.FileName);
        File.GetAttributes(md).Should().HaveFlag(FileAttributes.Hidden);
        File.ReadAllText(md).Should().Contain("- `보고서.docx`: 작업 완료, 반영 완료");
        FileTags.Counts([a, b]).Select(c => (c.Tag.Name, c.Count)).Should().Equal(("작업 완료", 2), ("반영 완료", 1));

        // 폴더를 다른 곳(다른 디스크라고 치고)으로 옮겨도 태그가 따라온다
        var moved = Path.Combine(_root, "elsewhere");
        Directory.Move(Path.Combine(_root, "work"), moved);
        FileTags.Get(Path.Combine(moved, "보고서.docx")).Should().Equal("작업 완료", "반영 완료");

        FileTags.Moved(Path.Combine(moved, "보고서.docx"), Path.Combine(moved, "보고서_최종.docx"));
        FileTags.Get(Path.Combine(moved, "보고서_최종.docx")).Should().Contain("작업 완료");
        FileTags.Get(Path.Combine(moved, "보고서.docx")).Should().BeEmpty();

        FileTags.Toggle([Path.Combine(moved, "보고서_최종.docx"), Path.Combine(moved, "견적.xlsx")], "작업 완료"); // 모두 있으면 모두 끔
        FileTags.Has(Path.Combine(moved, "견적.xlsx"), "작업 완료").Should().BeFalse();
    }

    [Fact]
    public void Managed_method_and_tags_share_the_file_without_losing_each_other()
    {
        var a = Make(@"para\메모.txt");
        var folder = Path.Combine(_root, "para");
        FileTags.Toggle([a], "검토 중");
        ManagedFolders.SetMethod(folder, FolderMethod.Para);
        Thread.Sleep(2100); // 태그 캐시가 파일을 다시 읽도록
        FileTags.Has(a, "검토 중").Should().BeTrue("관리 방법을 정해도 태그 칸은 남는다");
        ManagedFolders.ClearMethod(folder);
        Thread.Sleep(2100);
        FileTags.Has(a, "검토 중").Should().BeTrue("관리를 꺼도 태그는 남는다");
        File.Exists(Path.Combine(folder, ManagedFolders.FileName)).Should().BeTrue();
    }

    [Fact]
    public void In_a_git_repo_the_tag_file_is_excluded_until_shared()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repo", ".git"));
        var f = Make(@"repo\src\a.cs");
        FileTags.Toggle([f], "작업 완료");
        var exclude = Path.Combine(_root, "repo", ".git", "info", "exclude");
        File.ReadAllText(exclude).Should().Contain(".foldy.md");
        FileTags.IsSharedInGit(Path.Combine(_root, "repo", "src")).Should().BeFalse();

        FileTags.SetSharedInGit(Path.Combine(_root, "repo", "src"), true);
        File.ReadAllText(exclude).Should().NotContain(".foldy.md");
        FileTags.IsSharedInGit(Path.Combine(_root, "repo", "src")).Should().BeTrue();
    }

    [Fact]
    public void Render_keeps_other_content_and_parse_reads_it_back()
    {
        var text = FileTags.Render("# 메모\n\n정리 방법: PARA\n", new Dictionary<string, List<string>> { ["a b.txt"] = ["작업 완료"] });
        text.Should().StartWith("# 메모").And.Contain("정리 방법: PARA").And.Contain(FileTags.SectionTitle);
        FileTags.Parse(text)["a b.txt"].Should().Equal("작업 완료");
        FileTags.Render(text, new Dictionary<string, List<string>>()).Should().NotContain(FileTags.SectionTitle);
    }
}
