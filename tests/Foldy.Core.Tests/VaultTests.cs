using Foldy.Core.Vault;

namespace Foldy.Core.Tests;

public sealed class VaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "foldy-vault-" + Guid.NewGuid().ToString("N"));

    public VaultTests() => Directory.CreateDirectory(_root);

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
    public void Locked_file_round_trips_exact_bytes_and_rejects_wrong_password()
    {
        var f = Path.Combine(_root, "계약서.pdf");
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        File.WriteAllBytes(f, bytes);

        var (ok, lockPath) = FileLocker.Lock(f, "비밀-1234");
        ok.Should().BeTrue();
        lockPath.Should().EndWith("계약서.pdf.foldylock");
        File.ReadAllBytes(lockPath).Should().NotContainInOrder(bytes.Take(64));

        SecureZip.Verify(lockPath, "비밀-1234").Should().BeTrue();
        SecureZip.Verify(lockPath, "틀림").Should().BeFalse();
        FluentActions.Invoking(() => SecureZip.ReadEntry(lockPath, "틀림", "계약서.pdf")).Should().Throw<Exception>();

        File.Delete(f);
        var back = FileLocker.Unlock(lockPath, "비밀-1234");
        File.ReadAllBytes(back).Should().Equal(bytes, "잠글 때와 똑같은 바이트로 돌아온다");
        FileLocker.LooksPreviewable("계약서.pdf", bytes.AsSpan(0, 16)).Should().BeFalse("PDF 머리가 아니면 미리보기 대신 원래 프로그램으로");
        FileLocker.LooksPreviewable("a.pdf", "%PDF-1.7"u8).Should().BeTrue();
    }

    [Fact]
    public void Locked_note_keeps_markdown_and_images_in_one_encrypted_file()
    {
        var path = Path.Combine(_root, "비밀번호" + SecureZip.MemoSuffix);
        var n = new LockedNote { Markdown = "# 와이파이\n암호: abc\n" };
        var img = n.AddImage([0x89, 0x50, 0x4E, 0x47, 1, 2, 3], ".png");
        n.Markdown += $"\n![캡처]({img})\n";
        n.AddImage([1, 2, 3], ".png"); // 본문에서 안 쓰는 그림은 저장하지 않는다
        n.Save(path, "pw");

        var back = LockedNote.Load(path, "pw");
        back.Markdown.Should().Contain("암호: abc");
        back.Images.Keys.Should().Equal(img);
    }

    [Theory]
    [InlineData("../evil.bat")]
    [InlineData("..\\evil.bat")]
    [InlineData("a/../../evil.bat")]
    [InlineData("C:\\Windows\\evil.bat")]
    [InlineData("C:evil.bat")]
    [InlineData("/evil.bat")]
    [InlineData("\\\\server\\share\\evil.bat")]
    [InlineData("ok.txt:hidden")]
    [InlineData("..")]
    [InlineData("")]
    public void SafeDestination_refuses_names_that_leave_the_folder(string name)
    {
        SecureZip.SafeDestination(Path.Combine(_root, "out"), name).Should().BeNull();
    }

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("sub/b.txt", "sub\\b.txt")]
    [InlineData("sub/../c.txt", "c.txt")]
    public void SafeDestination_keeps_names_inside_the_folder(string name, string expected)
    {
        var dest = Path.Combine(_root, "out");
        SecureZip.SafeDestination(dest, name).Should().Be(Path.Combine(dest, expected.Replace('\\', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void ExtractAll_never_writes_outside_the_destination()
    {
        // 다른 사람이 만든 금고: 이름에 "..\"를 넣어 꺼낼 폴더 밖(예: 시작프로그램)에 쓰려는 경우
        var vault = Path.Combine(_root, "evil" + SecureZip.VaultExtension);
        SecureZip.Write(vault, [("../escaped.txt", () => new MemoryStream("x"u8.ToArray())), ("fine.txt", () => new MemoryStream("y"u8.ToArray()))], "pw");
        var dest = Path.Combine(_root, "out");

        SecureZip.ExtractAll(vault, "pw", dest).Should().Be(1);
        File.Exists(Path.Combine(_root, "escaped.txt")).Should().BeFalse();
        File.Exists(Path.Combine(dest, "fine.txt")).Should().BeTrue();
    }

    [Fact]
    public void Vault_holds_a_whole_folder_and_extracts_without_overwriting()
    {
        var folder = Path.Combine(_root, "중요");
        Directory.CreateDirectory(Path.Combine(folder, "하위"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "A");
        File.WriteAllText(Path.Combine(folder, "하위", "b.txt"), "B");

        var vault = VaultMaker.Create(folder, "pw");
        vault.Should().EndWith("중요.foldyvault");
        SecureZip.Verify(vault, "pw").Should().BeTrue();
        SecureZip.List(vault, "pw").Select(e => e.Name).Should().BeEquivalentTo(["a.txt", "하위/b.txt"]);

        var dst = Path.Combine(_root, "꺼냄");
        SecureZip.ExtractAll(vault, "pw", dst).Should().Be(2);
        File.ReadAllText(Path.Combine(dst, "하위", "b.txt")).Should().Be("B");
        SecureZip.ExtractAll(vault, "pw", dst).Should().Be(2);
        File.Exists(Path.Combine(dst, "a (2).txt")).Should().BeTrue("이미 있으면 덮어쓰지 않는다");
    }

    [Fact]
    public void Extract_ignores_entries_that_escape_the_destination_zip_slip()
    {
        var vault = Path.Combine(_root, "evil.foldyvault");
        using (var fs = File.Create(vault))
        using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipOutputStream(fs) { Password = "pw" })
        {
            foreach (var name in new[] { "../../escaped.txt", "ok.txt" })
            {
                zip.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry(name) { AESKeySize = 256 });
                zip.Write("x"u8);
                zip.CloseEntry();
            }
        }

        var dst = Path.Combine(_root, "out", "deep");
        SecureZip.ExtractAll(vault, "pw", dst).Should().Be(1);
        File.Exists(Path.Combine(dst, "ok.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "escaped.txt")).Should().BeFalse("zip 밖으로 나가는 이름은 꺼내지 않는다");
        SecureZip.SafeDestination(dst, "../x").Should().BeNull();
        SecureZip.SafeDestination(dst, @"C:\Windows\x").Should().BeNull();
        SecureZip.SafeDestination(dst, "a/b.txt").Should().Be(Path.Combine(dst, "a", "b.txt"));
    }
}
