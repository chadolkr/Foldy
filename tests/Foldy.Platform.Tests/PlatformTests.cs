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

/// <summary>단일 인스턴스: 두 번째 Foldy는 첫 Foldy에 인자를 넘기고 끝난다.</summary>
public sealed class SingleInstanceTests
{
    private static string Key() => "FoldyTest" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Only_the_first_owns_and_args_reach_it()
    {
        var key = Key();
        using var first = SingleInstance.Acquire(key);
        first.IsFirst.Should().BeTrue();
        using var got = new ManualResetEventSlim();
        string[]? received = null;
        first.StartServer(a =>
        {
            received = a;
            got.Set();
        });

        // 두 번째 Foldy (실제로는 다른 프로세스)
        var sent = await Task.Run(() =>
        {
            using var second = SingleInstance.Acquire(key);
            second.IsFirst.Should().BeFalse();
            return second.TrySendToFirst([@"C:\한글 폴더", "--x"], TimeSpan.FromSeconds(5));
        });

        sent.Should().BeTrue();
        got.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        received.Should().Equal(@"C:\한글 폴더", "--x");
    }

    [Fact]
    public async Task Serves_more_than_one_request()
    {
        var key = Key();
        using var first = SingleInstance.Acquire(key);
        var count = 0;
        first.StartServer(_ => Interlocked.Increment(ref count));
        for (var i = 0; i < 3; i++)
        {
            (await Task.Run(() =>
            {
                using var other = SingleInstance.Acquire(key);
                return other.TrySendToFirst([], TimeSpan.FromSeconds(5));
            })).Should().BeTrue();
        }

        SpinWait.SpinUntil(() => Volatile.Read(ref count) == 3, TimeSpan.FromSeconds(5)).Should().BeTrue();
    }

    [Fact]
    public async Task Send_fails_fast_when_nobody_listens()
    {
        var key = Key();
        using var first = SingleInstance.Acquire(key); // 잡기만 하고 서버는 열지 않음 (멈춘 Foldy)
        var ok = await Task.Run(() =>
        {
            using var second = SingleInstance.Acquire(key);
            return second.TrySendToFirst(["a"], TimeSpan.FromMilliseconds(500));
        });
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task Released_after_dispose()
    {
        var key = Key();
        var first = SingleInstance.Acquire(key);
        first.IsFirst.Should().BeTrue();
        first.Dispose();
        (await Task.Run(() =>
        {
            using var next = SingleInstance.Acquire(key);
            return next.IsFirst;
        })).Should().BeTrue();
    }
}

public class RecycleBinTests
{
    [Fact]
    public void Query_returns_totals() =>
        Foldy.Platform.Shell.RecycleBin.Query().Should().NotBeNull("SHQueryRecycleBin은 휴지통이 비어 있어도 성공한다");
}
