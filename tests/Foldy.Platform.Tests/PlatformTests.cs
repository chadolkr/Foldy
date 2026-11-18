using Foldy.Platform.Shell;
using Foldy.Platform.SystemInfo;

namespace Foldy.Platform.Tests;

/// <summary>IFileOperation 실제 동작 (충돌이 없는 경우라 확인 창이 뜨지 않는다).</summary>
public sealed class FileOperationsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-fo-" + Guid.NewGuid().ToString("N"));

    public FileOperationsTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "dst"));
        File.WriteAllText(Path.Combine(_dir, "src", "한글.txt"), "내용");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static Task<FileOpOutcome> Run(FileOpRequest r) =>
        FileOperations.RunAsync(r, IntPtr.Zero, null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Copy_move_rename_newfolder_delete()
    {
        var src = Path.Combine(_dir, "src", "한글.txt");
        var dst = Path.Combine(_dir, "dst");

        var copy = await Run(new FileOpRequest(FileOpKind.Copy, [src], dst));
        copy.Succeeded.Should().BeTrue(copy.Error);
        File.ReadAllText(Path.Combine(dst, "한글.txt")).Should().Be("내용");

        var rename = await Run(new FileOpRequest(FileOpKind.Rename, [Path.Combine(dst, "한글.txt")], NewName: "바뀜.txt"));
        rename.Succeeded.Should().BeTrue(rename.Error);
        File.Exists(Path.Combine(dst, "바뀜.txt")).Should().BeTrue();

        var nf1 = await Run(new FileOpRequest(FileOpKind.NewFolder, [dst], dst, "새 폴더"));
        var nf2 = await Run(new FileOpRequest(FileOpKind.NewFolder, [dst], dst, "새 폴더"));
        nf1.Succeeded.Should().BeTrue(nf1.Error);
        nf2.Succeeded.Should().BeTrue(nf2.Error);
        nf2.CreatedPath.Should().NotBe(nf1.CreatedPath, "같은 이름이면 자동으로 다른 이름 (새 폴더 (2))");
        Directory.GetDirectories(dst).Should().HaveCount(2);

        var move = await Run(new FileOpRequest(FileOpKind.Move, [Path.Combine(dst, "바뀜.txt")], Path.Combine(_dir, "src")));
        move.Succeeded.Should().BeTrue(move.Error);
        File.Exists(Path.Combine(_dir, "src", "바뀜.txt")).Should().BeTrue();

        var del = await Run(new FileOpRequest(FileOpKind.Delete, [Path.Combine(_dir, "src", "바뀜.txt")], NoConfirm: true));
        del.Succeeded.Should().BeTrue(del.Error);
        File.Exists(Path.Combine(_dir, "src", "바뀜.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Missing_source_reports_error_without_throwing()
    {
        var r = await Run(new FileOpRequest(FileOpKind.Copy, [Path.Combine(_dir, "없음.txt")], _dir, NoConfirm: true));
        r.Succeeded.Should().BeFalse();
        r.Error.Should().NotBeNullOrEmpty();
    }
}

public class ShellInfoTests
{
    [Fact]
    public void Type_names_and_icons_without_disk_access()
    {
        ShellInfo.TypeName("a.txt", false).Should().NotBeNullOrWhiteSpace();
        ShellInfo.TypeName("폴더", true).Should().Be("파일 폴더");
        var icon = ShellInfo.GenericIcon(".txt", isFolder: false);
        icon.Should().NotBe(IntPtr.Zero);
        ShellInfo.DestroyIcon(icon);
    }

    [Fact]
    public void Drives_list_contains_system_drive()
    {
        var sys = Path.GetPathRoot(Environment.SystemDirectory)!;
        Drives.List().Should().Contain(d => string.Equals(d.Root, sys, StringComparison.OrdinalIgnoreCase) && d.Type == NativeDriveType.Fixed);
        Drives.Details(sys).Total.Should().BeGreaterThan(0UL);
    }
}

public class TerminalTests
{
    [Fact]
    public void Admin_cmd_on_unc_uses_pushd()
    {
        var psi = Terminals.Build(@"\\server\share\dir", ShellKind.Cmd, elevated: true, useWindowsTerminal: false);
        psi.Arguments.Should().Contain("pushd \"\\\\server\\share\\dir\"");
        psi.Verb.Should().Be("runas");
        psi.WorkingDirectory.Should().BeEmpty("UNC는 작업 폴더로 못 쓴다");
    }

    [Fact]
    public void Admin_powershell_passes_path_as_command_with_quotes_escaped()
    {
        var psi = Terminals.Build(@"C:\it's here", ShellKind.WindowsPowerShell, elevated: true, useWindowsTerminal: false);
        psi.Arguments.Should().Contain("Set-Location -LiteralPath 'C:\\it''s here'");
    }

    [Fact]
    public void Drive_root_does_not_break_quotes()
    {
        var psi = Terminals.Build(@"C:\", ShellKind.WindowsPowerShell, elevated: true, useWindowsTerminal: false);
        psi.Arguments.Should().Contain(@"Set-Location -LiteralPath 'C:\.'");
        psi.Arguments.Should().NotContain(@"\'""");
    }

    [Fact]
    public void Admin_never_goes_through_windows_terminal()
    {
        var psi = Terminals.Build(@"C:\Windows", ShellKind.Cmd, elevated: true, useWindowsTerminal: true);
        Path.GetFileName(psi.FileName).Should().BeEquivalentTo("cmd.exe");
        psi.Verb.Should().Be("runas");
    }

    [Fact]
    public void Windows_terminal_gets_shell_by_name_and_safe_dir()
    {
        var psi = Terminals.Build(@"D:\", ShellKind.Cmd, elevated: false, useWindowsTerminal: true);
        if (Path.GetFileName(psi.FileName).Equals("wt.exe", StringComparison.OrdinalIgnoreCase))
        {
            psi.Arguments.Should().Be("-d \"D:\\.\" cmd.exe");
        }
    }

    [Fact]
    public void Normal_local_uses_working_directory()
    {
        var psi = Terminals.Build(@"C:\Windows", ShellKind.Cmd, elevated: false, useWindowsTerminal: false);
        psi.WorkingDirectory.Should().Be(@"C:\Windows");
        psi.Verb.Should().BeNullOrEmpty();
    }
}

public class AssociationTests
{
    [Fact]
    public void Txt_has_an_association_and_unknown_does_not()
    {
        Associations.Executable(".txt").Should().NotBeNull("메모장이 기본 연결");
        Associations.Executable(".foldy-unknown-ext-zz").Should().BeNull();
    }
}
