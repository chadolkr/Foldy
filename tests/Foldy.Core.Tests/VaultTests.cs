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
    public void SafeTarget_refuses_names_that_leave_the_folder(string name)
    {
        SecureZip.SafeTarget(Path.Combine(_root, "out"), name).Should().BeNull();
    }

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("sub/b.txt", "sub\\b.txt")]
    [InlineData("sub/../c.txt", "c.txt")]
    public void SafeTarget_keeps_names_inside_the_folder(string name, string expected)
    {
        var dest = Path.Combine(_root, "out");
        SecureZip.SafeTarget(dest, name).Should().Be(Path.Combine(dest, expected.Replace('\\', Path.DirectorySeparatorChar)));
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

        var made = VaultMaker.Create(folder, "pw");
        made.Complete.Should().BeTrue();
        made.Files.Should().Be(2);
        var vault = made.VaultPath;
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
        SecureZip.SafeTarget(dst, "../x").Should().BeNull();
        SecureZip.SafeTarget(dst, @"C:\Windows\x").Should().BeNull();
        SecureZip.SafeTarget(dst, "a/b.txt").Should().Be(Path.Combine(dst, "a", "b.txt"));
    }

    [Fact]
    public void New_files_use_strong_key_and_hide_names()
    {
        var vault = Path.Combine(_root, "v2" + SecureZip.VaultExtension);
        SecureZip.Write(vault, [("인사평가/2026_급여.xlsx", () => new MemoryStream("비밀"u8.ToArray()))], "correct horse battery");

        SecureZip.IsStrongFormat(vault).Should().BeTrue();
        using (var zf = new ICSharpCode.SharpZipLib.Zip.ZipFile(vault))
        {
            var names = zf.Cast<ICSharpCode.SharpZipLib.Zip.ZipEntry>().Select(e => e.Name).ToList();
            names.Should().NotContain(n => n.Contains("급여") || n.Contains("인사평가"), "암호 없이 파일 이름이 보이면 안 된다");
            names.Should().Contain(SecureZip.HeaderEntry);
            using var h = zf.GetInputStream(zf.GetEntry(SecureZip.HeaderEntry));
            var json = new StreamReader(h).ReadToEnd();
            json.Should().Contain("pbkdf2-sha256").And.Contain(SecureZip.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // 사용자 암호 그대로는 zip 암호가 아니다 (7-Zip에 넣어도 안 열림)
        using (var zf = new ICSharpCode.SharpZipLib.Zip.ZipFile(vault) { Password = "correct horse battery" })
        {
            FluentActions.Invoking(() =>
            {
                using var s = zf.GetInputStream(zf.GetEntry("d/000001"));
                s.CopyTo(Stream.Null);
            }).Should().Throw<Exception>();
        }

        SecureZip.List(vault, "correct horse battery").Should().Equal([("인사평가/2026_급여.xlsx", 6L)]);
        SecureZip.ReadEntry(vault, "correct horse battery", "인사평가/2026_급여.xlsx").Should().Equal("비밀"u8.ToArray());
        FluentActions.Invoking(() => SecureZip.List(vault, "wrong horse")).Should().Throw<WrongPasswordException>();
        SecureZip.Verify(vault, "wrong horse").Should().BeFalse();
    }

    [Fact]
    public void Old_standard_zip_files_still_open()
    {
        var old = Path.Combine(_root, "old" + SecureZip.LockExtension);
        using (var fs = File.Create(old))
        using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipOutputStream(fs) { Password = "1234" })
        {
            zip.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry("계약서.pdf") { AESKeySize = 256 });
            zip.Write("%PDF-1.7"u8);
            zip.CloseEntry();
        }

        SecureZip.IsStrongFormat(old).Should().BeFalse();
        FileLocker.InnerName(old, "1234").Should().Be("계약서.pdf");
        SecureZip.Verify(old, "1234").Should().BeTrue();
        SecureZip.Verify(old, "틀림").Should().BeFalse();
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(2_000_000_000)]
    public void Rejects_headers_with_weak_or_huge_iteration_counts(int iterations)
    {
        // 남이 만든 파일이 반복 횟수를 1,000으로 낮추거나(약하게) 20억으로 키워(앱 멈춤) 속이지 못한다
        var path = Path.Combine(_root, $"it{iterations}" + SecureZip.VaultExtension);
        using (var fs = File.Create(path))
        using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipOutputStream(fs))
        {
            zip.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry(SecureZip.HeaderEntry));
            zip.Write(System.Text.Encoding.UTF8.GetBytes($"{{\"format\":2,\"kdf\":\"pbkdf2-sha256\",\"iterations\":{iterations},\"salt\":\"AAAAAAAAAAAAAAAAAAAAAA==\"}}"));
            zip.CloseEntry();
        }

        FluentActions.Invoking(() => SecureZip.List(path, "pw")).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Vault_reports_folder_links_so_the_original_is_not_deleted()
    {
        var folder = Path.Combine(_root, "링크");
        var elsewhere = Path.Combine(_root, "다른곳");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "A");
        var hidden = Path.Combine(folder, ".숨김.txt");
        File.WriteAllText(hidden, "H");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        File.WriteAllText(Path.Combine(elsewhere, "x.txt"), "X");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(folder, "바로가기"), elsewhere);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // 링크를 만들 권한이 없는 환경 (Windows 개발자 모드 꺼짐)
        }

        var made = VaultMaker.Create(folder, "pw");
        made.Files.Should().Be(2, "숨김 파일도 넣는다");
        made.Complete.Should().BeFalse("폴더 링크 안의 파일은 들어가지 않았으므로 원본을 지우면 안 된다");
        made.Skipped.Should().ContainSingle(x => x.Path.EndsWith("바로가기"));
    }

    [Fact]
    public void Locked_note_save_and_open_stay_quick_with_the_strong_key()
    {
        // 메모 창은 Ctrl+S와 닫을 때마다 저장한다: 강한 키(PBKDF2 60만 번)여도 기다리게 하면 안 된다
        var path = Path.Combine(_root, "속도" + SecureZip.MemoSuffix);
        var n = new LockedNote { Markdown = new string('가', 20_000) };
        n.Save(path, "pw");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        n.Markdown += "더";
        n.Save(path, "pw");
        var save = sw.Elapsed;
        sw.Restart();
        LockedNote.Load(path, "pw").Markdown.Should().EndWith("더");
        var open = sw.Elapsed;
        save.Should().BeLessThan(TimeSpan.FromSeconds(5), $"저장 {save.TotalMilliseconds:N0}ms");
        open.Should().BeLessThan(TimeSpan.FromSeconds(5), $"열기 {open.TotalMilliseconds:N0}ms");
        Console.WriteLine($"memo save {save.TotalMilliseconds:N0}ms, open {open.TotalMilliseconds:N0}ms");
    }
}
