using System.IO;
using System.Text;
using Foldy.UI.Markdown;

namespace Foldy.UI.Tests;

public class MarkdownEncodingTests
{
    private const string Korean = "# 안녕하세요\n폴더 설명";

    [Fact]
    public void Utf8_bom_is_detected_and_stripped()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Korean)).ToArray();
        MarkdownText.Decode(bytes, out var enc).Should().Be(Korean);
        enc.CodePage.Should().Be(65001);
    }

    [Fact]
    public void Utf16_le_and_be_boms_are_detected()
    {
        var le = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Korean)).ToArray();
        var be = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(Korean)).ToArray();

        MarkdownText.Decode(le, out var encLe).Should().Be(Korean);
        encLe.CodePage.Should().Be(1200);
        MarkdownText.Decode(be, out var encBe).Should().Be(Korean);
        encBe.CodePage.Should().Be(1201);
    }

    [Fact]
    public void Utf8_without_bom_is_preferred()
    {
        MarkdownText.Decode(Encoding.UTF8.GetBytes(Korean), out var enc).Should().Be(Korean);
        enc.CodePage.Should().Be(65001);
    }

    [Fact]
    public void Invalid_utf8_falls_back_to_cp949()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp949 = Encoding.GetEncoding(949).GetBytes(Korean);

        MarkdownText.Decode(cp949, out var enc).Should().Be(Korean);
        enc.CodePage.Should().Be(949);
    }

    [Fact]
    public async Task ReadFileAsync_reads_file_shared()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foldy-md-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, Korean, new UTF8Encoding(true), TestContext.Current.CancellationToken);
        try
        {
            // 다른 프로그램이 쓰기로 열어 둔 상태에서도 읽힌다
            await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var (text, _) = await MarkdownText.ReadFileAsync(path, TestContext.Current.CancellationToken);
            text.Should().Be(Korean);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class MarkdownLinkTests
{
    private static readonly string Base = @"C:\docs\proj";

    private static MarkdownLinkTarget C(string url, params string[] dirs) =>
        MarkdownLinks.Classify(url, Base, p => dirs.Contains(p, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Anchor_links()
    {
        C("#설치-방법").Should().Be(new MarkdownLinkTarget(MarkdownLinkKind.Anchor, "", "설치-방법"));
    }

    [Theory]
    [InlineData("https://example.com/a")]
    [InlineData("http://example.com")]
    [InlineData("mailto:me@example.com")]
    public void Web_links(string url) => C(url).Kind.Should().Be(MarkdownLinkKind.Web);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("vbscript:x")]
    [InlineData("ms-settings:privacy")]
    [InlineData("")]
    public void Dangerous_or_unknown_schemes_are_blocked(string url) => C(url).Kind.Should().Be(MarkdownLinkKind.Blocked);

    [Fact]
    public void Relative_markdown_with_fragment_and_escapes()
    {
        var t = C("docs/My%20Guide.md#시작하기");
        t.Kind.Should().Be(MarkdownLinkKind.Markdown);
        t.Path.Should().Be(@"C:\docs\proj\docs\My Guide.md");
        t.Fragment.Should().Be("시작하기");
    }

    [Fact]
    public void Parent_relative_markdown() =>
        C("../README.markdown").Should().Be(new MarkdownLinkTarget(MarkdownLinkKind.Markdown, @"C:\docs\README.markdown", null));

    [Fact]
    public void Folder_by_trailing_slash_or_existence()
    {
        C("src/").Should().Be(new MarkdownLinkTarget(MarkdownLinkKind.Folder, @"C:\docs\proj\src", null));
        C("assets", @"C:\docs\proj\assets").Kind.Should().Be(MarkdownLinkKind.Folder);
    }

    [Fact]
    public void Other_files_and_absolute_paths()
    {
        C("build.ps1").Should().Be(new MarkdownLinkTarget(MarkdownLinkKind.File, @"C:\docs\proj\build.ps1", null));
        C(@"D:\data\report.pdf").Path.Should().Be(@"D:\data\report.pdf");
        C("file:///C:/temp/a.md").Should().Be(new MarkdownLinkTarget(MarkdownLinkKind.Markdown, @"C:\temp\a.md", null));
        C("LICENSE?raw=true").Path.Should().Be(@"C:\docs\proj\LICENSE");
    }

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("run.CMD", true)]
    [InlineData("script.ps1", true)]
    [InlineData("photo.png", false)]
    [InlineData("notes.md", false)]
    public void Executable_detection(string path, bool expected) => MarkdownLinks.IsExecutable(path).Should().Be(expected);
}

public class MarkdownOutlineTests
{
    [Fact]
    public void Extracts_headings_with_levels_lines_and_github_slugs()
    {
        var md = "# Foldy 소개\n\n텍스트\n\n## 설치 방법\n\n### `build.ps1` 실행하기!\n\n## 설치 방법\n\n> ## 인용 안 제목\n\n#### **굵은** _기울임_\n";
        var headings = MarkdownOutline.Extract(MarkdownParsing.Parse(md));

        headings.Select(h => h.Level).Should().Equal(1, 2, 3, 2, 2, 4);
        headings.Select(h => h.Text).Should().Equal("Foldy 소개", "설치 방법", "build.ps1 실행하기!", "설치 방법", "인용 안 제목", "굵은 기울임");
        headings.Select(h => h.Anchor).Should().Equal("foldy-소개", "설치-방법", "buildps1-실행하기", "설치-방법-1", "인용-안-제목", "굵은-기울임");
        headings[0].Line.Should().Be(0);
        headings[1].Line.Should().Be(4);
        headings[2].Indent.Should().Be(24);
    }

    [Fact]
    public void Empty_document_has_no_headings() =>
        MarkdownOutline.Extract(MarkdownParsing.Parse("그냥 문단")).Should().BeEmpty();
}

public class CodeHighlighterTests
{
    [Fact]
    public void Csharp_keywords_strings_comments_numbers()
    {
        var tokens = CodeHighlighter.Tokenize("public int x = 42; // 답\nvar s = \"hi\";", "csharp");
        tokens.Should().Contain(new CodeToken(CodeTokenKind.Keyword, "public"));
        tokens.Should().Contain(new CodeToken(CodeTokenKind.Keyword, "int"));
        tokens.Should().Contain(new CodeToken(CodeTokenKind.Number, "42"));
        tokens.Should().Contain(new CodeToken(CodeTokenKind.Comment, "// 답"));
        tokens.Should().Contain(new CodeToken(CodeTokenKind.StringLiteral, "\"hi\""));
        string.Concat(tokens.Select(t => t.Text)).Should().Be("public int x = 42; // 답\nvar s = \"hi\";");
    }

    [Fact]
    public void Unknown_language_is_plain_text()
    {
        CodeHighlighter.Tokenize("if x then", "cobol").Should().Equal(new CodeToken(CodeTokenKind.Text, "if x then"));
        CodeHighlighter.IsSupported("python").Should().BeTrue();
        CodeHighlighter.IsSupported(null).Should().BeFalse();
    }

    [Fact]
    public void Markup_tags_and_attributes()
    {
        var tokens = CodeHighlighter.Tokenize("<a href=\"x\">t</a><!-- c -->", "xml");
        tokens.Should().Contain(new CodeToken(CodeTokenKind.Keyword, "<a"));
        tokens.Should().Contain(new CodeToken(CodeTokenKind.StringLiteral, "\"x\""));
        tokens.Should().Contain(new CodeToken(CodeTokenKind.Comment, "<!-- c -->"));
        string.Concat(tokens.Select(t => t.Text)).Should().Be("<a href=\"x\">t</a><!-- c -->");
    }
}
