using Foldy.Providers.Sftp;

namespace Foldy.Sftp.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-pf-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "Foldy", "connections.json");

    public ProfileStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Dpapi_roundtrip_including_unicode_and_empty_entropy_independence()
    {
        var secret = "p@ss word/한글🔑";
        var enc = SecretProtector.Protect(secret);

        enc.Should().NotContain("p@ss");
        SecretProtector.Unprotect(enc).Should().Be(secret);
        SecretProtector.Protect(secret).Should().NotBe(enc, "DPAPI는 매번 다른 salt를 쓴다");
    }

    [Fact]
    public void Unprotect_of_garbage_throws()
    {
        var act = () => SecretProtector.Unprotect(Convert.ToBase64String([1, 2, 3, 4]));
        act.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    [Fact]
    public void Save_and_load_roundtrip_keeps_secrets_encrypted_on_disk()
    {
        var store = new ConnectionProfileStore(FilePath);
        var p = new SftpProfile { Name = "NAS", Host = "nas.local", Port = 2222, User = "me", InitialPath = "/srv" }
            .WithPassword("hunter2");
        var k = new SftpProfile { Name = "Key", Host = "k", User = "u", Auth = SftpAuthKind.KeyFile, KeyFilePath = @"C:\k\id_ed25519" }
            .WithPassphrase("xyzzy-secret");
        store.Save([p, k]);

        var text = File.ReadAllText(FilePath);
        text.Should().NotContain("hunter2").And.NotContain("xyzzy-secret");
        text.Should().Contain("\"keyFile\"");

        var loaded = store.Load();
        loaded.Should().HaveCount(2);
        loaded[0].Id.Should().Be(p.Id);
        loaded[0].ToAuth().Password.Should().Be("hunter2");
        loaded[0].ToLocation().ToString().Should().Be("sftp://me@nas.local:2222/srv");
        loaded[1].ToAuth().Should().BeEquivalentTo(new SftpAuth(SftpAuthKind.KeyFile, null, @"C:\k\id_ed25519", "xyzzy-secret"));
    }

    [Fact]
    public void Save_is_atomic_and_leaves_no_temp_file()
    {
        var store = new ConnectionProfileStore(FilePath);
        store.Save([new SftpProfile { Name = "a", Host = "h", User = "u" }]);
        store.Save([new SftpProfile { Name = "b", Host = "h", User = "u" }]);

        Directory.GetFiles(Path.GetDirectoryName(FilePath)!).Select(Path.GetFileName)
            .Should().BeEquivalentTo(["connections.json"]);
        store.Load().Single().Name.Should().Be("b");
    }

    [Fact]
    public void Upsert_and_remove()
    {
        var store = new ConnectionProfileStore(FilePath);
        var p = new SftpProfile { Name = "a", Host = "h", User = "u" };
        store.Upsert(p);
        store.Upsert(p with { Name = "renamed" });
        store.Load().Single().Name.Should().Be("renamed");
        store.Remove(p.Id);
        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_and_load_returns_empty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");
        var store = new ConnectionProfileStore(FilePath);

        store.Load().Should().BeEmpty();
        File.Exists(FilePath + ".corrupt").Should().BeTrue();
    }

    [Fact]
    public void Missing_file_loads_empty()
    {
        new ConnectionProfileStore(FilePath).Load().Should().BeEmpty();
    }
}
