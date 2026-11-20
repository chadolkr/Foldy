using System.IO;

namespace Foldy.UI.Tests;

/// <summary>
/// 한글이 깨지지 않게 (2026-11-20 사용자 발견: 설정의 "스레드"가 "ㅅㄹㄷ"로 보임).
/// 원인은 글자 그리기 방식 Display: Pretendard를 작게 그리면 "스·레·드·그" 같은 글자의 획이 빠진다. 모든 화면은 Ideal을 쓴다.
/// 화면 문구가 자모로 쪼개져(NFD) 저장되는 것도 함께 막는다.
/// </summary>
public class HangulRenderingTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foldy.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("저장소 루트(Foldy.slnx)를 찾아야 한다");
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void No_screen_uses_display_text_formatting()
    {
        var offenders = SourceFiles()
            .Where(f => File.ReadLines(f).Any(l =>
                (l.Contains("TextFormattingMode=\"Display\"", StringComparison.Ordinal)
                 || l.Contains("TextFormattingMode.Display", StringComparison.Ordinal)
                 || (l.Contains("TextOptions.TextFormattingMode", StringComparison.Ordinal) && l.Contains("Value=\"Display\"", StringComparison.Ordinal)))
                && !l.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(Root(), f))
            .ToList();
        offenders.Should().BeEmpty("Display 방식은 Pretendard 한글 획을 빠뜨린다 (스·레·드 → ㅅㄹㄷ). Ideal을 쓴다");
    }

    [Fact]
    public void Screen_text_is_stored_as_composed_hangul()
    {
        var offenders = new List<string>();
        foreach (var f in SourceFiles())
        {
            var text = File.ReadAllText(f);
            // 조합용 한글 자모(첫소리·가운뎃소리·끝소리)가 있으면 완성형이 아니라 쪼개진 글자다
            if (text.Any(c => c is (>= '\u1100' and <= '\u11FF') or (>= '\uA960' and <= '\uA97F') or (>= '\uD7B0' and <= '\uD7FF')))
            {
                offenders.Add(Path.GetRelativePath(Root(), f));
            }
        }

        offenders.Should().BeEmpty("한글은 완성형(NFC)으로 저장해야 한다. 자모로 쪼개진 글자는 화면에서 깨져 보인다");
    }
}

public class GitBadgeWordsTests
{
    [Fact]
    public void Every_git_mark_has_a_plain_explanation()
    {
        foreach (var s in new[] { Foldy.Core.FileSystem.GitFileStatus.Modified, Foldy.Core.FileSystem.GitFileStatus.Added, Foldy.Core.FileSystem.GitFileStatus.Untracked, Foldy.Core.FileSystem.GitFileStatus.Conflicted, Foldy.Core.FileSystem.GitFileStatus.Ignored })
        {
            Foldy.UI.Views.PaneView.GitExplain(s).Should().NotBeNullOrWhiteSpace(s.ToString());
        }

        Foldy.UI.Views.PaneView.GitExplain(Foldy.Core.FileSystem.GitFileStatus.None).Should().BeNull();
    }
}
