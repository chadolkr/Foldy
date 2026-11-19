using System.Text;
using Foldy.Platform.SystemInfo;

namespace Foldy.Platform.Tests;

/// <summary>사용자 프록시 업데이트의 관리자 설치 스크립트 (실행은 하지 않고 내용만 본다).</summary>
public sealed class ElevatedMsiInstallTests
{
    private const string Sha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void Paths_with_quotes_stay_inside_one_string()
    {
        var script = ElevatedMsiInstall.BuildScript(@"C:\Users\O'Neil\AppData\Foldy\Updates\Foldy-1.0.0-x64.msi", Sha, @"C:\Users\O'Neil\Foldy\Foldy.exe");
        script.Should().Contain(@"$src = 'C:\Users\O''Neil\AppData\Foldy\Updates\Foldy-1.0.0-x64.msi'");
        script.Should().Contain(@"$exe = 'C:\Users\O''Neil\Foldy\Foldy.exe'");
    }

    [Fact]
    public void Installs_only_a_verified_copy_in_an_admin_only_folder()
    {
        var script = ElevatedMsiInstall.BuildScript(@"C:\u\a.msi", Sha, @"C:\u\Foldy.exe");
        var copy = script.IndexOf("Copy-Item", StringComparison.Ordinal);
        var check = script.IndexOf("Get-FileHash -LiteralPath $msi", StringComparison.Ordinal);
        var install = script.IndexOf("msiexec.exe", StringComparison.Ordinal);
        var acl = script.IndexOf("icacls", StringComparison.Ordinal);
        acl.Should().BePositive().And.BeLessThan(copy, "복사 전에 폴더를 잠근다");
        copy.Should().BeLessThan(check);
        check.Should().BeLessThan(install, "복사본을 확인한 다음에만 설치한다");
        script.Should().Contain(Sha.ToUpperInvariant());
        script.Should().Contain("@('/i', ('\"' + $msi + '\"')", "원본(사용자 폴더)이 아니라 확인한 복사본을 설치한다");
    }

    [Fact]
    public void Relaunches_the_installed_foldy_after_install_not_the_exe_that_started_it()
    {
        var script = ElevatedMsiInstall.BuildScript(@"C:\u\a.msi", Sha, @"C:\dev\bin\Foldy.exe");
        var install = script.IndexOf("msiexec.exe", StringComparison.Ordinal);
        var pick = script.IndexOf("if (Test-Path -LiteralPath $installed) { $exe = $installed }", StringComparison.Ordinal);
        var relaunch = script.IndexOf("explorer.exe", StringComparison.Ordinal);
        script.Should().Contain("Join-Path $env:ProgramFiles 'Foldy\\Foldy.exe'");
        install.Should().BeLessThan(pick, "설치가 끝난 뒤에 설치 폴더를 확인한다");
        pick.Should().BePositive().And.BeLessThan(relaunch);
    }

    [Fact]
    public void Start_info_uses_encoded_command_and_runas()
    {
        var psi = ElevatedMsiInstall.BuildStartInfo(@"C:\u\a b.msi", Sha, @"C:\u\Foldy.exe");
        psi.Verb.Should().Be("runas");
        psi.FileName.Should().EndWith("powershell.exe");
        var b64 = psi.Arguments.Split(' ')[^1];
        Encoding.Unicode.GetString(Convert.FromBase64String(b64)).Should().Contain(@"$src = 'C:\u\a b.msi'");
    }

    [Theory]
    [InlineData("")]
    [InlineData("xyz")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85'")]
    public void Rejects_bad_hashes(string sha)
    {
        FluentActions.Invoking(() => ElevatedMsiInstall.BuildScript(@"C:\u\a.msi", sha, @"C:\u\Foldy.exe")).Should().Throw<ArgumentException>();
    }
}
