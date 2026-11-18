using Foldy.Providers.Sftp;

namespace Foldy.Sftp.Tests;

public sealed class SftpLocationTests
{
    [Fact]
    public void Parses_full_uri()
    {
        var l = SftpLocation.Parse("sftp://alice@example.com:2222/home/alice/docs");
        l.User.Should().Be("alice");
        l.Host.Should().Be("example.com");
        l.Port.Should().Be(2222);
        l.Path.Should().Be("/home/alice/docs");
    }

    [Fact]
    public void Defaults_port_and_root_path()
    {
        var l = SftpLocation.Parse("sftp://bob@host");
        l.Port.Should().Be(22);
        l.Path.Should().Be("/");
        l.ToString().Should().Be("sftp://bob@host/");
    }

    [Fact]
    public void Parses_ipv6_and_roundtrips()
    {
        var l = SftpLocation.Parse("sftp://u@[::1]:2200/x");
        l.Host.Should().Be("::1");
        l.Port.Should().Be(2200);
        l.ToString().Should().Be("sftp://u@[::1]:2200/x");
    }

    [Fact]
    public void Decodes_percent_encoded_user_and_encodes_back()
    {
        var l = SftpLocation.Parse("sftp://me%40corp@host/p");
        l.User.Should().Be("me@corp");
        l.ToString().Should().Be("sftp://me%40corp@host/p");
    }

    [Fact]
    public void Keeps_path_raw_including_special_characters()
    {
        var l = SftpLocation.Parse("sftp://u@h/a b/c#d?e%20f");
        l.Path.Should().Be("/a b/c#d?e%20f");
    }

    [Theory]
    [InlineData("http://u@h/")]
    [InlineData("sftp://")]
    [InlineData("sftp://u@/p")]
    [InlineData("sftp://u@h:0/p")]
    [InlineData("sftp://u@h:99999/p")]
    [InlineData("sftp://u@h:abc/p")]
    [InlineData("sftp://@h/p")]
    [InlineData("sftp://u@-oProxyCommand=x/p")]
    [InlineData("")]
    public void Rejects_invalid(string text) => SftpLocation.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void Combine_and_parent_work()
    {
        var l = SftpLocation.Parse("sftp://u@h/a/b");
        l.Combine("c").Path.Should().Be("/a/b/c");
        l.Parent()!.Path.Should().Be("/a");
        SftpLocation.Parse("sftp://u@h/a").Parent()!.Path.Should().Be("/");
        SftpLocation.Parse("sftp://u@h/").Parent().Should().BeNull();
        SftpLocation.Parse("sftp://u@h/").Combine("x").Path.Should().Be("/x");
    }

    [Fact]
    public void Connection_key_ignores_path_and_host_case()
    {
        SftpLocation.Parse("sftp://u@HOST/a").ConnectionKey.Should().Be(SftpLocation.Parse("sftp://u@host:22/b").ConnectionKey);
    }
}
