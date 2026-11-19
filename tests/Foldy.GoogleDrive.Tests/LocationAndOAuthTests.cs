using System.Net;
using System.Net.Sockets;
using System.Text;
using Foldy.Providers.GoogleDrive;

namespace Foldy.GoogleDrive.Tests;

public class GDriveLocationTests
{
    [Fact]
    public void Parse_normalizes_account_and_trims_slashes()
    {
        var l = GDriveLocation.Parse("gdrive://Me@Gmail.com//a/b/");
        l.Account.Should().Be("me@gmail.com");
        l.Segments.Should().Equal("a", "b");
        l.ToString().Should().Be("gdrive://me@gmail.com/a/b");
        l.Parent!.ToString().Should().Be("gdrive://me@gmail.com/a");
        l.Parent!.Parent!.IsRoot.Should().BeTrue();
        l.Parent!.Parent!.ToString().Should().Be("gdrive://me@gmail.com/");
        l.Parent!.Parent!.Parent.Should().BeNull();
    }

    [Theory]
    [InlineData("gdrive://")]
    [InlineData("gdrive:///a")]
    [InlineData("gdrive://me/a/../b")]
    [InlineData("sftp://me/a")]
    [InlineData("C:\\a")]
    public void Parse_rejects_bad_paths(string text) =>
        GDriveLocation.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void Slash_in_drive_names_is_substituted() =>
        GDriveLocation.EscapeName("a/b").Should().Be("a\u2215b");
}

public class PkceTests
{
    [Fact]
    public void Challenge_matches_rfc7636_appendix_b() =>
        Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk").Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");

    [Fact]
    public void Verifier_is_url_safe_and_long_enough()
    {
        var v = Pkce.CreateVerifier();
        v.Length.Should().BeInRange(43, 128);
        v.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }
}

public class OAuthTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return await f(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public void Authorize_uri_uses_pkce_offline_and_only_drive_scope()
    {
        var o = new GoogleOAuth(new HttpClient(), new GoogleOAuthClient("cid.apps.googleusercontent.com", null));
        var u = o.BuildAuthorizeUri("http://127.0.0.1:5000/", "chal", "st8", "me@gmail.com").ToString();
        u.Should().StartWith(GoogleOAuth.AuthorizeEndpoint + "?");
        u.Should().Contain("client_id=cid.apps.googleusercontent.com")
            .And.Contain("redirect_uri=http%3A%2F%2F127.0.0.1%3A5000%2F")
            .And.Contain("code_challenge=chal").And.Contain("code_challenge_method=S256")
            .And.Contain("access_type=offline").And.Contain("state=st8")
            .And.Contain("scope=" + Uri.EscapeDataString(GoogleOAuth.DriveScope))
            .And.Contain("login_hint=me%40gmail.com");
    }

    [Fact]
    public async Task Exchange_sends_verifier_and_secret_and_parses_tokens()
    {
        var h = new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"at","refresh_token":"rt","expires_in":3599,"scope":"s"}""")));
        var o = new GoogleOAuth(new HttpClient(h), new GoogleOAuthClient("cid", "sec"));
        var t = await o.ExchangeAsync("c0de", "ver", "http://127.0.0.1:1/", CancellationToken.None);
        t.AccessToken.Should().Be("at");
        t.RefreshToken.Should().Be("rt");
        h.Bodies[0].Should().Contain("code_verifier=ver").And.Contain("client_secret=sec").And.Contain("grant_type=authorization_code");
    }

    [Fact]
    public async Task Refresh_invalid_grant_means_sign_in_again()
    {
        var h = new Handler(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")));
        var o = new GoogleOAuth(new HttpClient(h), new GoogleOAuthClient("cid", null));
        var act = () => o.RefreshAsync("rt", CancellationToken.None);
        await act.Should().ThrowAsync<GDriveAuthException>();
        h.Bodies[0].Should().NotContain("client_secret");
    }

    [Fact]
    public async Task Token_source_refreshes_once_for_concurrent_callers()
    {
        var calls = 0;
        var h = new Handler(async _ =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(50);
            return Json(HttpStatusCode.OK, """{"access_token":"fresh","expires_in":3600}""");
        });
        using var src = new GDriveTokenSource(new GoogleOAuth(new HttpClient(h), new GoogleOAuthClient("cid", null)), "rt");
        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => src.GetAccessTokenAsync(false, CancellationToken.None)));
        tokens.Should().AllBe("fresh");
        calls.Should().Be(1);
        (await src.GetAccessTokenAsync(true, CancellationToken.None)).Should().Be("fresh");
        calls.Should().Be(2);
    }

    [Theory]
    [InlineData("GET /?code=4%2F0Ab&state=xyz&scope=a+b HTTP/1.1", "4/0Ab")]
    [InlineData("GET /?state=xyz&code=c HTTP/1.1", "c")]
    public void Request_line_is_parsed(string line, string code)
    {
        var q = LoopbackReceiver.ParseRequestLine(line)!;
        q["code"].Should().Be(code);
        q["state"].Should().Be("xyz");
    }

    [Theory]
    [InlineData("GET /favicon.ico HTTP/1.1")]
    [InlineData("POST /?code=a HTTP/1.1")]
    [InlineData("garbage")]
    public void Other_requests_are_ignored(string line) =>
        LoopbackReceiver.ParseRequestLine(line).Should().BeNull();

    [Fact]
    public async Task Loopback_ignores_wrong_state_and_returns_matching_redirect()
    {
        using var r = LoopbackReceiver.Start();
        r.RedirectUri.Should().StartWith("http://127.0.0.1:");
        var wait = r.WaitAsync("good", new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

        (await Get(r.Port, "/favicon.ico")).Should().Contain("404");
        (await Get(r.Port, "/?code=x&state=bad")).Should().Contain("400");
        (await Get(r.Port, "/?code=abc&state=good")).Should().Contain("200");

        var q = await wait;
        q["code"].Should().Be("abc");
    }

    private static async Task<string> Get(int port, string target)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var s = tcp.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
        using var reader = new StreamReader(s);
        return await reader.ReadLineAsync() ?? "";
    }
}

public class AccountStoreTests
{
    private sealed class Rot : ISecretProtector
    {
        public string Protect(string plain) => "P:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));

        public string Unprotect(string protectedText) =>
            protectedText.StartsWith("P:", StringComparison.Ordinal)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(protectedText[2..]))
                : throw new System.Security.Cryptography.CryptographicException("bad");
    }

    [Fact]
    public void Tokens_are_stored_protected_and_round_trip()
    {
        var dir = Directory.CreateTempSubdirectory("foldy-gd");
        try
        {
            var path = Path.Combine(dir.FullName, "gdrive.json");
            var store = new GDriveAccountStore(path, new Rot());
            store.Upsert("Me@Gmail.com", "Me", "refresh-secret", "cid");
            store.Upsert("me@gmail.com", "Me2", "refresh-2", "cid");
            File.ReadAllText(path).Should().NotContain("refresh-secret").And.NotContain("refresh-2");

            var again = new GDriveAccountStore(path, new Rot());
            again.Accounts.Should().ContainSingle();
            var a = again.Find("ME@gmail.com")!;
            a.DisplayName.Should().Be("Me2");
            again.GetRefreshToken(a).Should().Be("refresh-2");

            again.SetUserClient(" my.apps.googleusercontent.com ", "shh");
            File.ReadAllText(path).Should().NotContain("shh");
            new GDriveAccountStore(path, new Rot()).GetUserClient().Should().Be(new GoogleOAuthClient("my.apps.googleusercontent.com", "shh"));

            again.Remove("me@gmail.com");
            again.Accounts.Should().BeEmpty();
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Corrupt_file_is_moved_aside()
    {
        var dir = Directory.CreateTempSubdirectory("foldy-gd");
        try
        {
            var path = Path.Combine(dir.FullName, "gdrive.json");
            File.WriteAllText(path, "{not json");
            new GDriveAccountStore(path, new Rot()).Accounts.Should().BeEmpty();
            File.Exists(path + ".corrupt").Should().BeTrue();
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Unreadable_token_returns_null()
    {
        var store = new GDriveAccountStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), new Rot());
        store.GetRefreshToken(new GDriveAccount { Email = "a", ProtectedRefreshToken = "garbage" }).Should().BeNull();
    }
}
