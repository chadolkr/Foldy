using Foldy.Providers.Sftp;

namespace Foldy.Sftp.Tests;

public class SshConfigFileTests
{
    [Fact]
    public void Parses_hosts_and_skips_wildcards()
    {
        string[] lines =
        [
            "# 주석",
            "Host *",
            "  ServerAliveInterval 30",
            "Host web prod-web",
            "  HostName 10.0.0.5",
            "  User deploy",
            "  Port 2222",
            "  IdentityFile ~/.ssh/deploy_ed25519",
            "Host nas",
            "  HostName=nas.local",
            "Match host foo",
            "  User ignored",
        ];
        var hosts = SshConfigFile.Parse(lines);
        hosts.Select(h => h.Alias).Should().Equal("web", "prod-web", "nas");
        var web = hosts[0];
        web.HostName.Should().Be("10.0.0.5");
        web.User.Should().Be("deploy");
        web.Port.Should().Be(2222);
        web.IdentityFile.Should().EndWith(Path.Combine(".ssh", "deploy_ed25519"));
        hosts[2].HostName.Should().Be("nas.local");
        hosts[2].Port.Should().Be(22);
        hosts[2].User.Should().BeNull();
    }
}
