using System.Net;
using System.Text.Json;
using Foldy.Reporting;

namespace Foldy.Reporting.Tests;

public class ReportScrubberTests
{
    private readonly ReportScrubber _s = new("kim", "KIM-PC", "CORP");

    [Theory]
    [InlineData(@"at Foldy.A.B() in C:\Users\kim\src\Foldy\A.cs:line 42", @"at Foldy.A.B() in <path>\A.cs:line 42")]
    [InlineData(@"Could not find file 'D:\비밀\계약서 폴더\a b.docx'.", @"Could not find file '<path>\<file>.docx'.")]
    [InlineData(@"Access to the path 'C:\Users\kim\My Docs' is denied.", @"Access to the path '<path>' is denied.")]
    [InlineData(@"\\fileserver\share\x.txt 없음", @"<path>\<file>.txt 없음")]
    [InlineData("사용자 kim의 권한 없음, CORP\\kim", "사용자 <user>의 권한 없음, <user>")]
    [InlineData("host KIM-PC down", "host <machine> down")]
    [InlineData("mail kim@example.com", "mail <email>")]
    [InlineData("kimchi는 그대로", "kimchi는 그대로")]
    [InlineData("프록시: http://proxy.example.co.kr:8080/", "프록시: <url>")]
    [InlineData("PAC https://wpad.example.com/proxy.pac 실패", "PAC <url> 실패")]
    [InlineData("A connection attempt failed 10.20.30.40:22", "A connection attempt failed <ip>")]
    [InlineData("연결 192.168.0.1 끊김", "연결 <ip> 끊김")]
    [InlineData("host fe80::1c2b:3a4d 응답 없음", "host <ip> 응답 없음")]
    [InlineData("sftp 계정 deploy@build01 거부", "sftp 계정 <account> 거부")]
    [InlineData("No such host fileserver.corp.local", "No such host <host>")]
    [InlineData("No such file /home/kim/project/secret.txt", "No such file <path>")]
    [InlineData("Foldy 0.3.18.0 시작", "Foldy 0.3.18.0 시작")]
    [InlineData("Version=10.0.0.0, Culture=neutral", "Version=10.0.0.0, Culture=neutral")]
    [InlineData("2026-11-19 10:49:42.776 +09:00 [INF]", "2026-11-19 10:49:42.776 +09:00 [INF]")]
    [InlineData("foldy_proto::decode 실패, 11/19 /qn /norestart", "foldy_proto::decode 실패, 11/19 /qn /norestart")]
    [InlineData(@"open 'C:\Users\kim\Desktop\인사평가_2026.xlsx'", @"open '<path>\<file>.xlsx'")]
    public void Scrubs(string input, string expected) => _s.Scrub(input).Should().Be(expected);
}

public class CrashReportBuilderTests
{
    private static readonly AppEnvironment Env = new("0.0.1", "Windows 10.0.26100 X64", ".NET 10.0.0");

    [Fact]
    public void Builds_scrubbed_report_with_innermost_exception()
    {
        Exception caught;
        try { Thrower.Throw(@"C:\Users\kim\secret.txt"); throw new UnreachableException(); }
        catch (Exception e) { caught = new System.Reflection.TargetInvocationException(e); }

        var r = new CrashReportBuilder(Env, new ReportScrubber("kim", "PC")).Build(caught, "log kim");

        r.ExceptionType.Should().Be("System.IO.FileNotFoundException");
        r.Message.Should().Be(@"missing <path>\<file>.txt");
        r.Frames.Should().Contain(f => f.Contains("Thrower.Throw"));
        r.Frames.Should().NotContain(f => f.Contains(":line"));
        r.Details.Should().NotContain("kim");
        r.Log.Should().Be("log <user>");
    }

    [Fact]
    public void Serializes_to_relay_schema()
    {
        var r = new CrashReport
        {
            AppVersion = "0.0.1", OsVersion = "w", Runtime = "r", ExceptionType = "System.X",
            Message = "m", Details = "d", Frames = ["Foldy.A.B"],
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(r, ReportJson.Default.CrashReport));
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        names.Should().BeEquivalentTo(["schema", "appVersion", "osVersion", "runtime", "exceptionType", "message", "details", "frames"]);
        doc.RootElement.GetProperty("schema").GetInt32().Should().Be(1);
    }

    private static class Thrower
    {
        public static void Throw(string path) => throw new FileNotFoundException($"missing {path}");
    }

    private sealed class UnreachableException : Exception;
}

public sealed class CrashStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-crash-test-" + Guid.NewGuid().ToString("N"));

    private static CrashReport Report(string type = "System.X") => new()
    {
        AppVersion = "0.0.1", OsVersion = "w", Runtime = "r", ExceptionType = type,
        Message = "m", Details = "d", Frames = ["Foldy.A.B"],
    };

    [Fact]
    public void Saves_dedupes_and_removes()
    {
        var store = new CrashStore(_dir);
        store.TrySave(Report(), fatal: true).Should().BeTrue();
        store.TrySave(Report(), fatal: false).Should().BeFalse("같은 예외는 하나만 대기");
        store.TrySave(Report("System.Y"), fatal: false).Should().BeTrue();

        var pending = store.Pending();
        pending.Should().HaveCount(2);
        pending[0].Fatal.Should().BeTrue();
        store.Remove(pending[0]);
        store.Pending().Should().ContainSingle().Which.Report.ExceptionType.Should().Be("System.Y");
    }

    [Fact]
    public void Ignores_corrupt_files()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "crash-bad.json"), "{not json");
        new CrashStore(_dir).Pending().Should().BeEmpty();
        Directory.EnumerateFiles(_dir).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

public class BugReportLinkTests
{
    private static readonly AppEnvironment Env = new("0.0.1", "Windows 10.0.26100 X64", ".NET 10.0.0");

    [Fact]
    public void Prefills_environment_and_scrubbed_log()
    {
        var url = BugReportLink.Create("https://github.com/chadolkr/Foldy", Env, @"opened C:\Users\kim\a.txt", new ReportScrubber("kim", "PC"));
        var s = url.ToString();
        s.Should().StartWith("https://github.com/chadolkr/Foldy/issues/new?labels=bug&title=");
        var body = Uri.UnescapeDataString(url.Query.Split("body=")[1]);
        body.Should().Contain("Foldy: 0.0.1").And.Contain("Windows 10.0.26100").And.Contain(@"opened <path>\<file>.txt").And.NotContain("kim");
    }

    [Fact]
    public void Drops_oldest_log_lines_to_fit_url_limit()
    {
        var log = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"line {i:D3} " + new string('가', 20)));
        var url = BugReportLink.Create("https://github.com/o/r", Env, log, new ReportScrubber("zz", "yy"));
        url.AbsoluteUri.Length.Should().BeLessThanOrEqualTo(BugReportLink.MaxUrlLength);
        var body = Uri.UnescapeDataString(url.Query.Split("body=")[1]);
        body.Should().Contain("line 499").And.NotContain("line 000").And.Contain("(일부)");
    }
}

public class CrashSenderTests
{
    private static CrashReport Report() => new()
    {
        AppVersion = "0.0.1", OsVersion = "w", Runtime = "r", ExceptionType = "System.X",
        Message = "m", Details = "d", Frames = ["Foldy.A.B"],
    };

    [Fact]
    public void Uses_system_proxy_with_default_credentials()
    {
        using var h = CrashSender.CreateSystemProxyHandler();
        h.UseProxy.Should().BeTrue();
        h.Proxy.Should().BeSameAs(HttpClient.DefaultProxy);
        h.DefaultProxyCredentials.Should().BeSameAs(CredentialCache.DefaultCredentials);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, SendOutcome.Sent)]
    [InlineData(HttpStatusCode.BadRequest, SendOutcome.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, SendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, SendOutcome.RetryLater)]
    public async Task Maps_relay_status(HttpStatusCode status, SendOutcome expected)
    {
        var handler = new StubHandler(status,
            """{"status":"created","signature":"abc","issue":7,"url":"https://github.com/o/r/issues/7","occurrences":1}""");
        using var sender = new CrashSender(new Uri("https://relay.test/v1/crash"), handler);
        var r = await sender.SendAsync(Report(), TestContext.Current.CancellationToken);
        r.Outcome.Should().Be(expected);
        if (expected == SendOutcome.Sent) r.Response!.Issue.Should().Be(7);
        handler.LastBody.Should().Contain("\"exceptionType\":\"System.X\"");
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
