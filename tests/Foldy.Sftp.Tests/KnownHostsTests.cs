using System.Buffers.Binary;
using System.Text;
using Foldy.Providers.Sftp;

namespace Foldy.Sftp.Tests;

public sealed class KnownHostsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-kh-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, ".ssh", "known_hosts");

    public KnownHostsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>SSH wire 형식 blob: string(type) + payload.</summary>
    internal static byte[] Blob(string type, string payload)
    {
        var t = Encoding.ASCII.GetBytes(type);
        var p = Encoding.ASCII.GetBytes(payload);
        var buf = new byte[4 + t.Length + 4 + p.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)t.Length);
        t.CopyTo(buf, 4);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4 + t.Length), (uint)p.Length);
        p.CopyTo(buf, 8 + t.Length);
        return buf;
    }

    private void WriteFile(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllLines(FilePath, lines);
    }

    private static string Line(string hosts, byte[] blob) =>
        $"{hosts} {KnownHostsFile.KeyTypeOf(blob)} {Convert.ToBase64String(blob)}";

    [Theory]
    [InlineData("ssh-ed25519")]
    [InlineData("ecdsa-sha2-nistp256")]
    [InlineData("ecdsa-sha2-nistp384")]
    [InlineData("ecdsa-sha2-nistp521")]
    [InlineData("ssh-rsa")]
    public void Matches_plain_entry_for_each_key_type(string type)
    {
        var key = Blob(type, "k1");
        WriteFile("# comment", "", Line("example.com,10.0.0.5", key));
        var kh = new KnownHostsFile(FilePath);

        kh.Check("example.com", 22, key).Should().Be(HostKeyStatus.Match);
        kh.Check("EXAMPLE.com", 22, key).Should().Be(HostKeyStatus.Match);
        kh.Check("10.0.0.5", 22, key).Should().Be(HostKeyStatus.Match);
        kh.Check("other.com", 22, key).Should().Be(HostKeyStatus.Unknown);
    }

    [Fact]
    public void Different_key_of_same_type_is_changed_but_other_type_is_unknown()
    {
        var ed = Blob("ssh-ed25519", "a");
        WriteFile(Line("h", ed));
        var kh = new KnownHostsFile(FilePath);

        kh.Check("h", 22, Blob("ssh-ed25519", "b")).Should().Be(HostKeyStatus.Changed);
        kh.Check("h", 22, Blob("ssh-rsa", "b")).Should().Be(HostKeyStatus.Unknown);
    }

    [Fact]
    public void Nonstandard_port_uses_bracket_form()
    {
        var key = Blob("ssh-ed25519", "a");
        WriteFile(Line("[h]:2222", key));
        var kh = new KnownHostsFile(FilePath);

        kh.Check("h", 2222, key).Should().Be(HostKeyStatus.Match);
        kh.Check("h", 22, key).Should().Be(HostKeyStatus.Unknown);
    }

    [Fact]
    public void Matches_hashed_entry()
    {
        var key = Blob("ssh-ed25519", "a");
        var salt = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        WriteFile(Line(KnownHostsFile.HashWithSalt("example.com", salt), key));
        var kh = new KnownHostsFile(FilePath);

        kh.Check("example.com", 22, key).Should().Be(HostKeyStatus.Match);
        kh.Check("example.org", 22, key).Should().Be(HostKeyStatus.Unknown);
    }

    [Fact]
    public void Matches_wildcards_and_respects_negation()
    {
        var key = Blob("ssh-ed25519", "a");
        WriteFile(Line("*.corp.example,!bad.corp.example", key));
        var kh = new KnownHostsFile(FilePath);

        kh.Check("a.corp.example", 22, key).Should().Be(HostKeyStatus.Match);
        kh.Check("bad.corp.example", 22, key).Should().Be(HostKeyStatus.Unknown);
    }

    [Fact]
    public void Revoked_key_is_never_trusted_and_cert_authority_is_ignored()
    {
        var key = Blob("ssh-ed25519", "a");
        WriteFile("@revoked " + Line("h", key), "@cert-authority " + Line("h", Blob("ssh-ed25519", "ca")));
        var kh = new KnownHostsFile(FilePath);

        kh.Check("h", 22, key).Should().Be(HostKeyStatus.Changed);
        kh.Check("h", 22, Blob("ssh-ed25519", "ca")).Should().Be(HostKeyStatus.Unknown);
    }

    [Fact]
    public void Missing_file_is_unknown_and_garbage_lines_are_skipped()
    {
        new KnownHostsFile(FilePath).Check("h", 22, Blob("ssh-rsa", "x")).Should().Be(HostKeyStatus.Unknown);
        WriteFile("garbage", "only two");
        new KnownHostsFile(FilePath).Check("h", 22, Blob("ssh-rsa", "x")).Should().Be(HostKeyStatus.Unknown);
    }

    [Fact]
    public void Add_creates_directory_appends_and_preserves_existing_lines()
    {
        var kh = new KnownHostsFile(FilePath);
        var k1 = Blob("ssh-ed25519", "a");
        var k2 = Blob("ecdsa-sha2-nistp256", "b");

        kh.Add("h1", 22, k1);
        File.AppendAllText(FilePath, "other.host ssh-rsa AAAA"); // 끝 개행 없는 외부 항목
        kh.Add("h2", 2200, k2);

        var lines = File.ReadAllLines(FilePath);
        lines.Should().HaveCount(3);
        lines[0].Should().Be($"h1 ssh-ed25519 {Convert.ToBase64String(k1)}");
        lines[2].Should().StartWith("[h2]:2200 ecdsa-sha2-nistp256 ");
        kh.Check("h1", 22, k1).Should().Be(HostKeyStatus.Match);
        kh.Check("h2", 2200, k2).Should().Be(HostKeyStatus.Match);
    }

    [Fact]
    public void Add_hashed_is_readable_back_and_does_not_leak_hostname()
    {
        var kh = new KnownHostsFile(FilePath);
        var key = Blob("ssh-ed25519", "a");
        kh.Add("secret.example", 22, key, hashed: true);

        File.ReadAllText(FilePath).Should().StartWith("|1|").And.NotContain("secret.example");
        kh.Check("secret.example", 22, key).Should().Be(HostKeyStatus.Match);
    }

    [Fact]
    public void Remove_deletes_only_matching_host_and_type()
    {
        var kh = new KnownHostsFile(FilePath);
        var ed = Blob("ssh-ed25519", "a");
        var rsa = Blob("ssh-rsa", "b");
        kh.Add("h", 22, ed);
        kh.Add("h", 22, rsa);
        kh.Add("other", 22, ed);

        kh.Remove("h", 22, "ssh-ed25519").Should().Be(1);

        kh.Check("h", 22, ed).Should().Be(HostKeyStatus.Unknown);
        kh.Check("h", 22, rsa).Should().Be(HostKeyStatus.Match);
        kh.Check("other", 22, ed).Should().Be(HostKeyStatus.Match);
    }

    [Fact]
    public void Fingerprint_matches_openssh_style()
    {
        var fp = KnownHostsFile.FingerprintSha256(Blob("ssh-ed25519", "a"));
        fp.Should().StartWith("SHA256:").And.NotContain("=");
        fp.Length.Should().Be(7 + 43);
    }

    [Fact]
    public void Verifier_unknown_goes_to_handler_and_AcceptAndSave_persists()
    {
        var kh = new KnownHostsFile(FilePath);
        var key = Blob("ssh-ed25519", "a");
        HostKeyCheck? seen = null;
        var v = new HostKeyVerifier(kh, c => { seen = c; return HostKeyDecision.AcceptAndSave; });

        v.Verify("h", 22, key).Should().BeTrue();

        seen!.Status.Should().Be(HostKeyStatus.Unknown);
        seen.KeyType.Should().Be("ssh-ed25519");
        kh.Check("h", 22, key).Should().Be(HostKeyStatus.Match);
        new HostKeyVerifier(kh, _ => throw new InvalidOperationException("matched key must not ask")).Verify("h", 22, key).Should().BeTrue();
    }

    [Fact]
    public void Verifier_changed_rejects_without_handler_and_replaces_when_saved()
    {
        var kh = new KnownHostsFile(FilePath);
        var oldKey = Blob("ssh-ed25519", "old");
        var newKey = Blob("ssh-ed25519", "new");
        kh.Add("h", 22, oldKey);

        new HostKeyVerifier(kh, null).Verify("h", 22, newKey).Should().BeFalse();
        new HostKeyVerifier(kh, c => { c.Status.Should().Be(HostKeyStatus.Changed); return HostKeyDecision.Reject; })
            .Verify("h", 22, newKey).Should().BeFalse();
        kh.Check("h", 22, newKey).Should().Be(HostKeyStatus.Changed);

        new HostKeyVerifier(kh, _ => HostKeyDecision.AcceptAndSave).Verify("h", 22, newKey).Should().BeTrue();
        kh.Check("h", 22, newKey).Should().Be(HostKeyStatus.Match);
        kh.Check("h", 22, oldKey).Should().Be(HostKeyStatus.Changed);
    }

    [Fact]
    public void Verifier_AcceptOnce_does_not_write()
    {
        var kh = new KnownHostsFile(FilePath);
        new HostKeyVerifier(kh, _ => HostKeyDecision.AcceptOnce).Verify("h", 22, Blob("ssh-rsa", "a")).Should().BeTrue();
        File.Exists(FilePath).Should().BeFalse();
    }
}
