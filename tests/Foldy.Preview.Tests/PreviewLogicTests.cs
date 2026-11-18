using System.Text;
using Foldy.Presentation.Preview;

namespace Foldy.Preview.Tests;

internal static class Req
{
    public static PreviewRequest Of(string name, long length = 100, FileAttributes attrs = FileAttributes.Normal, bool remote = false, string? path = null) =>
        new(path ?? $@"C:\data\{name}", name, length, DateTime.UtcNow, attrs, remote);
}

public class ProviderSelectionTests
{
    // 셸 처리기가 .pdf·.docx에만 있다고 가정한 모음
    private static PreviewProviderRegistry Registry() =>
        PreviewProviderRegistry.CreateDefault(ext => ext is ".pdf" or ".docx");

    [Theory]
    [InlineData("a.md", PreviewKind.Markdown)]
    [InlineData("A.MARKDOWN", PreviewKind.Markdown)]
    [InlineData("pic.PNG", PreviewKind.Image)]
    [InlineData("pic.jpeg", PreviewKind.Image)]
    [InlineData("Program.cs", PreviewKind.Text)]
    [InlineData("notes.txt", PreviewKind.Text)]
    [InlineData("LICENSE", PreviewKind.Text)]
    [InlineData(".gitignore", PreviewKind.Text)]
    [InlineData("report.pdf", PreviewKind.Shell)]
    [InlineData("memo.docx", PreviewKind.Shell)]
    public void Chooses_provider_by_extension(string name, PreviewKind expected) =>
        Registry().Select(Req.Of(name))!.Kind.Should().Be(expected);

    [Fact]
    public void Unknown_extension_has_no_provider() =>
        Registry().Select(Req.Of("data.xyz123")).Should().BeNull();

    [Fact]
    public void Without_shell_provider_pdf_is_not_previewable() =>
        PreviewProviderRegistry.CreateDefault().Select(Req.Of("a.pdf")).Should().BeNull();

    [Fact]
    public void Registering_same_id_replaces_and_order_decides()
    {
        var r = new PreviewProviderRegistry().Register(new TextPreviewProvider()).Register(new MarkdownPreviewProvider());
        r.Providers.Select(p => p.Id).Should().Equal("markdown", "text");
        r.Register(new MarkdownPreviewProvider());
        r.Providers.Should().HaveCount(2);
    }

    [Fact]
    public void Image_codec_check_is_injectable()
    {
        var r = PreviewProviderRegistry.CreateDefault(isImage: ext => ext == ".heic");
        r.Select(Req.Of("a.heic"))!.Kind.Should().Be(PreviewKind.Image);
        r.Select(Req.Of("a.png")).Should().BeNull();
    }

    [Theory]
    [InlineData("a.cs", "cs")]
    [InlineData("a.json", "json")]
    [InlineData("a.ps1", "ps1")]
    [InlineData("a.txt", null)]
    public void Language_keys_match_code_highlighter(string name, string? lang) =>
        TextPreviewProvider.LanguageFor(Path.GetExtension(name)).Should().Be(lang);
}

public class PreviewPolicyTests
{
    private static PreviewProviderRegistry Registry() => PreviewProviderRegistry.CreateDefault(ext => ext == ".pdf");

    [Theory]
    [InlineData(FileAttributes.Offline)]
    [InlineData((FileAttributes)0x0040_0000)] // RECALL_ON_DATA_ACCESS
    [InlineData((FileAttributes)0x0004_0000)] // RECALL_ON_OPEN
    public void Cloud_only_files_are_never_read(FileAttributes attrs)
    {
        foreach (var name in new[] { "a.txt", "a.md", "a.pdf", "a.cs" })
        {
            var d = PreviewPolicy.Evaluate(Req.Of(name, attrs: attrs), Registry());
            d.Status.Should().Be(PreviewStatus.CloudOnly, name);
            d.StatusText.Should().Be("클라우드 파일 — 내려받지 않음");
            d.Provider.Should().BeNull();
        }
    }

    [Fact]
    public async Task Cloud_only_image_uses_shell_cache_without_reading_content()
    {
        var req = Req.Of("a.jpg", attrs: (FileAttributes)0x0040_0000);
        var d = PreviewPolicy.Evaluate(req, Registry());
        d.IsReady.Should().BeTrue();
        var content = (FilePreviewContent)await d.Provider!.LoadAsync(req, CancellationToken.None);
        content.AllowContentRead.Should().BeFalse();
    }

    [Fact]
    public void Network_drive_waits_for_the_user()
    {
        var req = Req.Of("a.txt", remote: true);
        var d = PreviewPolicy.Evaluate(req, Registry());
        d.Status.Should().Be(PreviewStatus.NetworkDrive);
        d.StatusText.Should().Be("네트워크 드라이브 — [미리보기]");
        d.CanForce.Should().BeTrue();

        PreviewPolicy.Evaluate(req with { UserForced = true }, Registry()).IsReady.Should().BeTrue();
    }

    [Fact]
    public void Cloud_wins_over_network_and_cannot_be_forced()
    {
        var req = Req.Of("a.txt", attrs: FileAttributes.Offline, remote: true) with { UserForced = true };
        PreviewPolicy.Evaluate(req, Registry()).Status.Should().Be(PreviewStatus.CloudOnly);
    }

    [Fact]
    public void Folders_and_unknown_types_are_not_previewable()
    {
        PreviewPolicy.Evaluate(Req.Of("dir", attrs: FileAttributes.Directory), Registry()).Status.Should().Be(PreviewStatus.NotPreviewable);
        var d = PreviewPolicy.Evaluate(Req.Of("a.xyz123"), Registry());
        d.Status.Should().Be(PreviewStatus.NotPreviewable);
        d.StatusText.Should().Be("미리 볼 수 없음");
    }

    [Fact]
    public void Size_limits_apply_per_provider()
    {
        // 텍스트는 앞부분만 읽으므로 제한 없음, Markdown·셸은 제한 있음
        PreviewPolicy.Evaluate(Req.Of("big.log", 5L << 30), Registry()).IsReady.Should().BeTrue();
        PreviewPolicy.Evaluate(Req.Of("big.md", 9L << 20), Registry()).Status.Should().Be(PreviewStatus.TooLarge);
        PreviewPolicy.Evaluate(Req.Of("big.pdf", 300L << 20), Registry()).Status.Should().Be(PreviewStatus.TooLarge);
        PreviewPolicy.Evaluate(Req.Of("ok.pdf", 10L << 20), Registry()).IsReady.Should().BeTrue();
    }

    [Theory]
    [InlineData(@"\\server\share\a.txt", true)]
    [InlineData("sftp://host/a.txt", true)]
    [InlineData(@"C:\Windows\notepad.exe", false)]
    [InlineData("", false)]
    public void Remote_path_detection(string path, bool expected) =>
        PreviewGuards.IsRemotePath(path).Should().Be(expected);
}

public class TextDecodingTests
{
    [Fact]
    public void Utf8_without_bom()
    {
        var r = TextFileReader.Decode(Encoding.UTF8.GetBytes("안녕 hello"), false, 12);
        r.Text.Should().Be("안녕 hello");
        r.EncodingName.Should().Be("UTF-8");
    }

    [Fact]
    public void Utf8_bom_is_stripped()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("abc")).ToArray();
        var r = TextFileReader.Decode(bytes, false, bytes.Length);
        r.Text.Should().Be("abc");
        r.EncodingName.Should().Be("UTF-8 (BOM)");
    }

    [Fact]
    public void Utf16_le_and_be_with_bom()
    {
        var le = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("한글ABC")).ToArray();
        TextFileReader.Decode(le, false, le.Length).Should().Match<TextReadResult>(r => r.Text == "한글ABC" && r.EncodingName == "UTF-16 LE");
        var be = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("한글ABC")).ToArray();
        TextFileReader.Decode(be, false, be.Length).Should().Match<TextReadResult>(r => r.Text == "한글ABC" && r.EncodingName == "UTF-16 BE");
    }

    [Fact]
    public void Utf16_without_bom_is_guessed_from_nul_pattern()
    {
        var bytes = Encoding.Unicode.GetBytes("hello world, plain text");
        var r = TextFileReader.Decode(bytes, false, bytes.Length);
        r.Text.Should().Be("hello world, plain text");
        r.EncodingName.Should().Be("UTF-16 LE");
    }

    [Fact]
    public void Cp949_is_detected_when_not_valid_utf8()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(949).GetBytes("한글 문서입니다\r\n두 번째 줄");
        var r = TextFileReader.Decode(bytes, false, bytes.Length);
        r.Text.Should().Be("한글 문서입니다\r\n두 번째 줄");
        r.EncodingName.Should().Be("CP949");
    }

    [Fact]
    public void Binary_is_rejected()
    {
        var bytes = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0, 1, 2, 0, 0, 0, 9, 8, 7, 0 };
        TextFileReader.Decode(bytes, false, bytes.Length).Text.Should().BeNull();
        var ctrl = Enumerable.Range(0, 200).Select(i => (byte)(i % 8 + 1)).Concat([(byte)0xFF]).ToArray();
        TextFileReader.Decode(ctrl, false, ctrl.Length).Text.Should().BeNull();
    }

    [Fact]
    public void Truncated_read_drops_a_split_multibyte_character()
    {
        var full = Encoding.UTF8.GetBytes("가나다");
        // 마지막 글자(3바이트) 중간에서 자른다
        var cut = full[..(full.Length - 1)];
        var r = TextFileReader.Decode(cut, truncated: true, full.Length);
        r.Text.Should().Be("가나");
        r.EncodingName.Should().Be("UTF-8");
        r.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_reads_only_the_first_limit_bytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foldy-prev-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, new string('a', 5000) + "END");
            var r = await TextFileReader.ReadAsync(path, limit: 1000);
            r.Text!.Length.Should().Be(1000);
            r.Truncated.Should().BeTrue();
            r.TotalBytes.Should().Be(5003);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class PreviewViewModelTests
{
    private static string Temp(string ext, string content)
    {
        var p = Path.Combine(Path.GetTempPath(), $"foldy-prev-{Guid.NewGuid():N}{ext}");
        File.WriteAllText(p, content);
        return p;
    }

    private static PreviewViewModel Vm() => new(PreviewProviderRegistry.CreateDefault()) { Debounce = TimeSpan.Zero };

    private static PreviewRequest For(string path, FileAttributes attrs = FileAttributes.Normal, bool remote = false)
    {
        var fi = new FileInfo(path);
        return new PreviewRequest(path, fi.Name, fi.Length, fi.LastWriteTimeUtc, attrs, remote);
    }

    [Fact]
    public async Task Shows_text_file_with_header()
    {
        var p = Temp(".txt", "첫 줄\nsecond");
        try
        {
            var vm = Vm();
            await vm.ShowAsync(For(p));
            vm.Status.Should().Be(PreviewStatus.Ready);
            vm.Kind.Should().Be(PreviewKind.Text);
            ((TextPreviewContent)vm.Content!).Text.Should().Be("첫 줄\nsecond");
            vm.FileName.Should().Be(Path.GetFileName(p));
            vm.SizeText.Should().NotBeEmpty();
            vm.ModifiedText.Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public async Task Network_file_is_not_read_until_forced()
    {
        // 존재하지 않는 경로여도 읽기 전에 멈춘다 = 디스크·네트워크 접근 없음
        var vm = Vm();
        await vm.ShowAsync(Req.Of("a.txt", remote: true, path: @"\\nowhere\share\a.txt"));
        vm.Status.Should().Be(PreviewStatus.NetworkDrive);
        vm.StatusText.Should().Be("네트워크 드라이브 — [미리보기]");
        vm.CanForce.Should().BeTrue();
        vm.ForceCommand.CanExecute(null).Should().BeTrue();
        vm.Content.Should().BeNull();
    }

    [Fact]
    public async Task Force_reads_the_network_file()
    {
        var p = Temp(".txt", "remote text");
        try
        {
            var vm = Vm();
            await vm.ShowAsync(For(p, remote: true));
            vm.Status.Should().Be(PreviewStatus.NetworkDrive);
            await vm.ForceAsync();
            vm.Status.Should().Be(PreviewStatus.Ready);
            ((TextPreviewContent)vm.Content!).Text.Should().Be("remote text");
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public async Task Cloud_file_shows_status_and_does_not_open_the_file()
    {
        var vm = Vm();
        await vm.ShowAsync(Req.Of("a.txt", attrs: FileAttributes.Offline, path: @"C:\does\not\exist\a.txt"));
        vm.Status.Should().Be(PreviewStatus.CloudOnly);
        vm.StatusText.Should().Be("클라우드 파일 — 내려받지 않음");
        vm.CanForce.Should().BeFalse();
    }

    [Fact]
    public async Task Binary_file_reports_not_previewable()
    {
        var p = Path.Combine(Path.GetTempPath(), $"foldy-prev-{Guid.NewGuid():N}.txt");
        await File.WriteAllBytesAsync(p, [0, 1, 2, 0, 0, 3, 0, 0xFF]);
        try
        {
            var vm = Vm();
            await vm.ShowAsync(For(p));
            vm.Status.Should().Be(PreviewStatus.NotPreviewable);
            vm.StatusText.Should().Be("미리 볼 수 없음");
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public async Task Missing_file_fails_without_throwing()
    {
        var vm = Vm();
        await vm.ShowAsync(Req.Of("gone.txt", path: Path.Combine(Path.GetTempPath(), "foldy-gone-" + Guid.NewGuid().ToString("N") + ".txt")));
        vm.Status.Should().Be(PreviewStatus.Failed);
    }

    [Fact]
    public async Task A_newer_selection_wins_over_a_pending_one()
    {
        var a = Temp(".txt", "A");
        var b = Temp(".txt", "B");
        try
        {
            var vm = new PreviewViewModel(PreviewProviderRegistry.CreateDefault()) { Debounce = TimeSpan.FromMilliseconds(200) };
            var first = vm.ShowAsync(For(a));
            await vm.ShowAsync(For(b));
            await first;
            ((TextPreviewContent)vm.Content!).Text.Should().Be("B");
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public async Task Null_clears()
    {
        var vm = Vm();
        await vm.ShowAsync(Req.Of("a.xyz123"));
        await vm.ShowAsync((PreviewRequest?)null);
        vm.Status.Should().Be(PreviewStatus.Empty);
        vm.FileName.Should().BeEmpty();
    }
}
