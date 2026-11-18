using Foldy.Providers.Sftp;

namespace Foldy.Sftp.Tests;

public sealed class SshTerminalLauncherTests
{
    private const string Wt = @"C:\Users\x\AppData\Local\Microsoft\WindowsApps\wt.exe";

    [Fact]
    public void Builds_wt_command_with_key_and_path()
    {
        var l = SshTerminalLauncher.Build(new SshTerminalRequest("host", 22, "me", "/home/me", @"C:\k\id"), Wt);

        l.FileName.Should().Be(Wt);
        l.Arguments.Should().Be("ssh -t -p 22 -i C:\\k\\id me@host \"cd '/home/me' && exec $SHELL -l\"");
    }

    [Fact]
    public void Key_path_with_spaces_is_quoted()
    {
        var l = SshTerminalLauncher.Build(new SshTerminalRequest("h", 2222, "u", "/", @"C:\My Keys\id"), Wt);
        l.Arguments.Should().Contain("-p 2222").And.Contain("-i \"C:\\My Keys\\id\"");
    }

    [Fact]
    public void Path_quoting_handles_spaces_single_quotes_and_metacharacters()
    {
        SshTerminalLauncher.BuildRemoteCommand("/a b/it's;rm -rf $HOME", sudo: false)
            .Should().Be("cd '/a b/it'\\''s;rm -rf $HOME' && exec $SHELL -l");
    }

    [Fact]
    public void Semicolon_is_escaped_for_wt_only()
    {
        var req = new SshTerminalRequest("h", 22, "u", "/a;b");
        SshTerminalLauncher.Build(req, Wt).Arguments.Should().Contain("/a\\;b");
        SshTerminalLauncher.Build(req, null).Arguments.Should().NotContain("\\;");
    }

    [Fact]
    public void Falls_back_to_ssh_exe_without_wt()
    {
        var l = SshTerminalLauncher.Build(new SshTerminalRequest("h", 22, "u", "/x"), null);
        l.FileName.Should().Be("ssh.exe");
        l.Arguments.Should().Be("-t -p 22 u@h \"cd '/x' && exec $SHELL -l\"");
        l.UseShellExecute.Should().BeTrue();
    }

    [Fact]
    public void Without_path_just_starts_login_shell()
    {
        SshTerminalLauncher.BuildRemoteCommand(null, false).Should().Be("exec $SHELL -l");
        SshTerminalLauncher.BuildRemoteCommand("", true).Should().Be("exec sudo -i");
    }

    [Fact]
    public void Sudo_variant_uses_sudo_i_and_keeps_path()
    {
        SshTerminalLauncher.BuildRemoteCommand("/var/www", true)
            .Should().Be("exec sudo -i sh -c 'cd '\\''/var/www'\\'' && exec \"$SHELL\" -l'");
        var l = SshTerminalLauncher.Build(new SshTerminalRequest("h", 22, "u", "/var/www", Sudo: true), null);
        l.Arguments.Should().Contain("sudo -i");
    }

    [Theory]
    [InlineData("-oProxyCommand=calc", "host")]
    [InlineData("user", "-oProxyCommand=calc")]
    [InlineData("us er", "host")]
    [InlineData("user", "ho st")]
    [InlineData("", "host")]
    public void Rejects_option_injection_and_bad_names(string user, string host)
    {
        var act = () => SshTerminalLauncher.Build(new SshTerminalRequest(host, 22, user), Wt);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_bad_port()
    {
        var act = () => SshTerminalLauncher.Build(new SshTerminalRequest("h", 0, "u"), Wt);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    public void Windows_arg_quoting(string input, string expected) =>
        SshTerminalLauncher.QuoteWindowsArg(input).Should().Be(expected);

    [Fact]
    public void Finds_wt_via_alias_folder_then_path()
    {
        var alias = Path.Combine(@"C:\L", "Microsoft", "WindowsApps", "wt.exe");
        SshTerminalLauncher.FindWindowsTerminal(@"C:\L", [@"C:\bin"], p => p == alias).Should().Be(alias);
        SshTerminalLauncher.FindWindowsTerminal(@"C:\L", [@"C:\bin", @"D:\tools"], p => p == @"D:\tools\wt.exe")
            .Should().Be(@"D:\tools\wt.exe");
        SshTerminalLauncher.FindWindowsTerminal(@"C:\L", [@"C:\bin"], _ => false).Should().BeNull();
    }
}
