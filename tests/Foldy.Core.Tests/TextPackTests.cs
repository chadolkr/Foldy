using System.IO.Compression;
using Foldy.Core.Vault;

namespace Foldy.Core.Tests;

public sealed class TextPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "foldy-textpack-" + Guid.NewGuid().ToString("N"));

    public TextPackTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void Memo_with_images_round_trips_as_textbundle_with_original_bytes()
    {
        var path = Path.Combine(_root, "회의 메모" + TextPack.Extension);
        var note = new LockedNote { Markdown = "# 회의\n" };
        var png = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        var img = note.AddImage(png, ".png", TextPack.Assets);
        note.AddImage([1, 2, 3], ".png", TextPack.Assets); // 본문에서 안 쓰는 그림은 빠진다
        note.Markdown += $"![캡처]({img})\n";
        TextPack.Save(path, note);

        using (var zip = ZipFile.OpenRead(path))
        {
            zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(["info.json", "text.md", img]);
        }

        var back = TextPack.Load(path);
        back.Markdown.Should().Contain("# 회의");
        back.Images[img].Should().Equal(png, "그림은 원본 그대로");
    }

    [Fact]
    public void Unsafe_entry_names_are_ignored_when_reading()
    {
        var path = Path.Combine(_root, "evil" + TextPack.Extension);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "text.md", "../escape.png", "assets/../../x.png", "C:/abs.png", "assets/ok.png" })
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write("x"u8);
            }
        }

        var note = TextPack.Load(path);
        note.Images.Keys.Should().Equal("assets/ok.png");
    }
}
