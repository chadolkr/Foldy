using System.Text;
using Foldy.Platform.SystemInfo;

namespace Foldy.Platform.Tests;

/// <summary>"나만 설치"(1.19.0)의 업데이트 스크립트 (실행은 하지 않고 내용만 본다).</summary>
public sealed class UserMsiInstallTests
{
    private const string Sha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void Checks_the_hash_before_installing_and_relaunches_the_user_install()
    {
        var script = UserMsiInstall.BuildScript(@"C:\Users\O'Neil\AppData\Local\Foldy\Updates\1.19.1\Foldy-1.19.1-x64-user.msi", Sha);
        script.Should().Contain(@"$msi = 'C:\Users\O''Neil\AppData\Local\Foldy\Updates\1.19.1\Foldy-1.19.1-x64-user.msi'");
        var check = script.IndexOf("Get-FileHash -LiteralPath $msi", StringComparison.Ordinal);
        var install = script.IndexOf("msiexec.exe", StringComparison.Ordinal);
        var relaunch = script.IndexOf("Start-Process -FilePath $exe", StringComparison.Ordinal);
        check.Should().BePositive().And.BeLessThan(install, "확인한 다음에만 설치한다");
        install.Should().BeLessThan(relaunch);
        script.Should().Contain(Sha.ToUpperInvariant());
        script.Should().Contain("Join-Path $env:LOCALAPPDATA 'Programs\\Foldy\\Foldy.exe'");
        script.Should().NotContain("icacls", "사용자 폴더 설치라 관리자 쪽 폴더를 쓰지 않는다");
    }

    [Fact]
    public void Runs_without_asking_for_admin_rights()
    {
        var psi = UserMsiInstall.BuildStartInfo(@"C:\u\a b.msi", Sha);
        psi.Verb.Should().BeNullOrEmpty("UAC 없이");
        psi.UseShellExecute.Should().BeFalse();
        var b64 = psi.Arguments.Split(' ')[^1];
        Encoding.Unicode.GetString(Convert.FromBase64String(b64)).Should().Contain(@"$msi = 'C:\u\a b.msi'");
    }

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\Foldy\", @"C:\Users\u\AppData\Local\Programs\Foldy", true)]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\Foldy\runtimes\x", @"C:\Users\u\AppData\Local\Programs\Foldy", true)]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\FoldyOther\", @"C:\Users\u\AppData\Local\Programs\Foldy", false)]
    [InlineData(@"C:\Program Files\Foldy\", @"C:\Users\u\AppData\Local\Programs\Foldy", false)]
    public void Knows_whether_it_runs_from_the_user_install_folder(string path, string folder, bool under)
    {
        UserMsiInstall.IsUnder(path, folder).Should().Be(under);
    }

    [Theory]
    [InlineData("")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85'")]
    public void Rejects_bad_hashes(string sha)
    {
        FluentActions.Invoking(() => UserMsiInstall.BuildScript(@"C:\u\a.msi", sha)).Should().Throw<ArgumentException>();
    }
}
