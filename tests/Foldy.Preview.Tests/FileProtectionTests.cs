using System.Text;
using Foldy.Presentation.Preview;

namespace Foldy.Preview.Tests;

/// <summary>
/// DRM·암호로 보호된 파일 알아보기 (사용자 2026-11-22: DRM 걸린 파일을 오류로 보이지 말고 DRM이라 못 보여 준다고 알릴 것).
/// 특정 보안 제품의 표시는 쓰지 않는다: 확장자와 다른 머리 + 암호문 같은 내용이면 DRM.
/// </summary>
public class FileProtectionTests
{
    private static readonly byte[] OleHead = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>문서 보안 프로그램이 만든 것 같은 파일: 자기 머리(글자) + 암호문.</summary>
    private static byte[] Wrapped(int cipherBytes = 8192)
    {
        var header = Encoding.ASCII.GetBytes("<!-- secure document container v2 -->\r\n" + new string(' ', 200));
        var cipher = new byte[cipherBytes];
        new Random(42).NextBytes(cipher);
        return [.. header, .. cipher];
    }

    private static byte[] Ole(params string[] utf16Names)
    {
        var b = new List<byte>(OleHead);
        b.AddRange(new byte[504]);
        foreach (var n in utf16Names)
        {
            b.AddRange(Encoding.Unicode.GetBytes(n));
            b.AddRange(new byte[64]);
        }

        b.AddRange(new byte[4096]);
        return [.. b];
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".XLSX")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    [InlineData(".hwp")]
    [InlineData(".hwpx")]
    [InlineData(".doc")]
    [InlineData(".jpg")]
    [InlineData(".txt")]
    [InlineData(".csv")]
    public void Encrypted_content_behind_a_foreign_header_is_drm(string ext) =>
        FileProtectionCheck.Classify(ext, Wrapped()).Should().Be(FileProtection.Drm);

    [Fact]
    public void Real_formats_are_not_protected()
    {
        var zip = new byte[8192];
        new Random(1).NextBytes(zip); // 압축된 내용도 고르게 흩어져 있다
        zip[0] = (byte)'P';
        zip[1] = (byte)'K';
        zip[2] = 3;
        zip[3] = 4;
        FileProtectionCheck.Classify(".docx", zip).Should().Be(FileProtection.None);
        FileProtectionCheck.Classify(".hwpx", zip).Should().Be(FileProtection.None);

        var pdf = new byte[8192];
        new Random(2).NextBytes(pdf);
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(pdf, 0);
        FileProtectionCheck.Classify(".pdf", pdf).Should().Be(FileProtection.None);

        var jpg = new byte[8192];
        new Random(3).NextBytes(jpg);
        jpg[0] = 0xFF;
        jpg[1] = 0xD8;
        jpg[2] = 0xFF;
        FileProtectionCheck.Classify(".jpg", jpg).Should().Be(FileProtection.None);

        FileProtectionCheck.Classify(".doc", Ole("WordDocument", "1Table")).Should().Be(FileProtection.None);
        FileProtectionCheck.Classify(".txt", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("회의록 2026년 11월 셋째 주, 다음 할 일: 견적 정리\n", 200)))).Should().Be(FileProtection.None);
    }

    [Fact]
    public void Renamed_or_odd_files_are_not_called_drm()
    {
        // 이름만 .pdf인 HTML (내려받기 실패 페이지 등), .doc로 저장한 RTF, 압축 파일 이름을 바꾼 것
        FileProtectionCheck.Classify(".pdf", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<html><body>Not found</body></html>\n", 100)))).Should().Be(FileProtection.None);
        FileProtectionCheck.Classify(".doc", Encoding.ASCII.GetBytes("{\\rtf1\\ansi " + new string('x', 4000))).Should().Be(FileProtection.None);
        var gz = new byte[8192];
        new Random(4).NextBytes(gz);
        gz[0] = 0x1F;
        gz[1] = 0x8B;
        FileProtectionCheck.Classify(".txt", gz).Should().Be(FileProtection.None);

        // 너무 작아 판단할 수 없는 것, 모르는 확장자
        FileProtectionCheck.Classify(".pdf", [1, 2, 3, 4, 5, 6]).Should().Be(FileProtection.None);
        FileProtectionCheck.Classify(".xyz", Wrapped()).Should().Be(FileProtection.None);
        FileProtectionCheck.Checks(".xyz").Should().BeFalse();
    }

    [Fact]
    public void Encrypted_office_package_tells_rights_management_from_a_password()
    {
        // 권한 관리(DRM)로 잠긴 docx: 복합 문서 안 데이터 공간 이름
        FileProtectionCheck.Classify(".docx", Ole("\u0006DataSpaces", "DRMEncryptedDataSpace", "DRMEncryptedTransform")).Should().Be(FileProtection.Drm);
        FileProtectionCheck.Classify(".xlsx", Ole("\u0009DRMContent")).Should().Be(FileProtection.Drm);

        // 암호 걸린 docx
        FileProtectionCheck.Classify(".docx", Ole("\u0006DataSpaces", "StrongEncryptionDataSpace", "EncryptionInfo", "EncryptedPackage")).Should().Be(FileProtection.Password);

        // 이름이 앞에 없으면 끝부분에서 찾고, 그래도 없으면 "암호나 DRM"
        FileProtectionCheck.Classify(".pptx", Ole(), tail: Encoding.Unicode.GetBytes("DRMEncryptedTransform")).Should().Be(FileProtection.Drm);
        FileProtectionCheck.Classify(".pptx", Ole()).Should().Be(FileProtection.Protected);

        // 예전 문서(doc)는 권한 관리 표시가 있을 때만
        FileProtectionCheck.Classify(".doc", Ole("WordDocument", "\u0009DRMContent")).Should().Be(FileProtection.Drm);
        FileProtectionCheck.Classify(".xls", Ole("Workbook", "EncryptionInfo")).Should().Be(FileProtection.None);
    }

    [Fact]
    public void Detect_reads_the_file_and_survives_missing_ones()
    {
        var p = Path.Combine(Path.GetTempPath(), $"foldy-drm-{Guid.NewGuid():N}.docx");
        File.WriteAllBytes(p, Wrapped(200_000));
        try
        {
            FileProtectionCheck.Detect(p, ".docx").Should().Be(FileProtection.Drm);
        }
        finally
        {
            File.Delete(p);
        }

        FileProtectionCheck.Detect(p, ".docx").Should().Be(FileProtection.None); // 없는 파일
    }

    [Fact]
    public void Entropy_threshold_separates_text_from_cipher()
    {
        var cipher = new byte[4096];
        new Random(5).NextBytes(cipher);
        FileProtectionCheck.LooksEncrypted(cipher).Should().BeTrue();
        FileProtectionCheck.LooksEncrypted(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. 다람쥐 헌 쳇바퀴에 타고파. ", 100)))).Should().BeFalse();
        FileProtectionCheck.LooksEncrypted(new byte[4096]).Should().BeFalse();
    }
}

public class PreviewProtectionTests
{
    private static PreviewViewModel Vm() => new(PreviewProviderRegistry.CreateDefault(ext => ext is ".pdf" or ".docx")) { Debounce = TimeSpan.Zero };

    private static PreviewRequest For(string path)
    {
        var fi = new FileInfo(path);
        return new PreviewRequest(path, fi.Name, fi.Length, fi.LastWriteTimeUtc, FileAttributes.Normal);
    }

    [Fact]
    public async Task Drm_document_says_so_instead_of_a_handler_error()
    {
        var p = Path.Combine(Path.GetTempPath(), $"foldy-drm-{Guid.NewGuid():N}.docx");
        var cipher = new byte[16384];
        new Random(7).NextBytes(cipher);
        File.WriteAllBytes(p, [.. Encoding.ASCII.GetBytes("<!-- container -->"), .. cipher]);
        try
        {
            var vm = Vm();
            await vm.ShowAsync(For(p));
            vm.Status.Should().Be(PreviewStatus.Protected);
            vm.StatusText.Should().Be(PreviewMessages.For(FileProtection.Drm));
            vm.StatusText.Should().Contain("복사 방지");
            vm.Content.Should().BeNull(); // 처리기를 부르지 않았다
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public async Task Cloud_only_files_are_not_opened_for_the_check()
    {
        // 클라우드 전용 이미지는 셸 썸네일만 쓴다: 보호 검사도 내용을 읽지 않는다
        var vm = Vm();
        await vm.ShowAsync(new PreviewRequest(@"C:\does\not\exist\a.png", "a.png", 10, DateTime.UtcNow, FileAttributes.Offline));
        vm.Status.Should().Be(PreviewStatus.Ready);
        vm.Kind.Should().Be(PreviewKind.Image);
    }

    [Fact]
    public async Task Check_can_be_turned_off()
    {
        var p = Path.Combine(Path.GetTempPath(), $"foldy-drm-{Guid.NewGuid():N}.docx");
        var cipher = new byte[16384];
        new Random(8).NextBytes(cipher);
        File.WriteAllBytes(p, cipher);
        try
        {
            var vm = Vm();
            vm.CheckProtection = false;
            await vm.ShowAsync(For(p));
            vm.Status.Should().Be(PreviewStatus.Ready);
            vm.Kind.Should().Be(PreviewKind.Shell);
        }
        finally
        {
            File.Delete(p);
        }
    }
}
