using System.Net;
using System.Text;
using Foldy.Providers.GoogleDrive;

namespace Foldy.GoogleDrive.Tests;

public class HttpApiTests
{
    private sealed class Tokens : IAccessTokenSource
    {
        public int Refreshes;

        public Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
        {
            if (forceRefresh)
            {
                Refreshes++;
            }

            return Task.FromResult(Refreshes == 0 ? "old" : "new");
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.Parameter, body));
            return f(request, Requests.Count);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static GDriveHttpApi Api(Handler h, Tokens? t = null) => new(new HttpClient(h), t ?? new Tokens(), _ => TimeSpan.Zero);

    [Fact]
    public async Task List_builds_query_and_parses_files()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, """
            {"nextPageToken":"p2","files":[
              {"id":"1","name":"a.txt","mimeType":"text/plain","size":"12","modifiedTime":"2026-11-19T01:02:03.000Z","createdTime":"2026-11-18T00:00:00Z"},
              {"id":"2","name":"L","mimeType":"application/vnd.google-apps.shortcut","shortcutDetails":{"targetId":"9","targetMimeType":"application/vnd.google-apps.folder"}}]}
            """));
        var page = await Api(h).ListChildrenAsync("it's", null, CancellationToken.None);

        page.NextPageToken.Should().Be("p2");
        page.Files[0].Size.Should().Be(12);
        page.Files[0].ModifiedUtc.Should().Be(new DateTime(2026, 11, 19, 1, 2, 3, DateTimeKind.Utc));
        page.Files[1].IsFolder.Should().BeTrue();
        page.Files[1].EffectiveId.Should().Be("9");
        Uri.UnescapeDataString(h.Requests[0].Url).Should().Contain("'it\\'s' in parents and trashed = false");
        h.Requests[0].Auth.Should().Be("old");
    }

    [Fact]
    public async Task Unauthorized_refreshes_token_once_and_retries()
    {
        var tokens = new Tokens();
        var h = new Handler((r, n) => n == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json(HttpStatusCode.OK, """{"id":"root-id","name":"My Drive","mimeType":"application/vnd.google-apps.folder"}"""));
        var f = await Api(h, tokens).GetAsync("root", CancellationToken.None);
        f.IsFolder.Should().BeTrue();
        tokens.Refreshes.Should().Be(1);
        h.Requests[1].Auth.Should().Be("new");
    }

    [Fact]
    public async Task Rate_limit_is_retried_and_not_found_maps_to_file_not_found()
    {
        var h = new Handler((_, n) => n < 3
            ? Json(HttpStatusCode.Forbidden, """{"error":{"message":"slow down","errors":[{"reason":"userRateLimitExceeded"}]}}""")
            : Json(HttpStatusCode.NotFound, """{"error":{"message":"File not found: x","errors":[{"reason":"notFound"}]}}"""));
        var act = () => Api(h).GetAsync("x", CancellationToken.None);
        await act.Should().ThrowAsync<FileNotFoundException>();
        h.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Quota_error_is_readable()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.Forbidden, """{"error":{"message":"quota","errors":[{"reason":"storageQuotaExceeded"}]}}"""));
        var act = () => Api(h).CreateFolderAsync("root", "n", CancellationToken.None);
        (await act.Should().ThrowAsync<IOException>()).Which.Message.Should().Contain("저장 공간");
    }

    [Fact]
    public async Task Upload_opens_resumable_session_then_puts_body()
    {
        var h = new Handler((r, n) =>
        {
            if (n == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                resp.Headers.Location = new Uri("https://www.googleapis.com/upload/drive/v3/files?upload_id=abc");
                return resp;
            }

            return Json(HttpStatusCode.OK, """{"id":"new","name":"a.bin","mimeType":"application/octet-stream","size":"3"}""");
        });
        long reported = 0;
        var f = await Api(h).UploadAsync("p1", "a.bin", new MemoryStream([1, 2, 3]), 3, new SyncProgress(v => reported = v), CancellationToken.None);

        f.Id.Should().Be("new");
        reported.Should().Be(3);
        h.Requests[0].Method.Should().Be(HttpMethod.Post);
        h.Requests[0].Url.Should().Contain("uploadType=resumable");
        h.Requests[0].Body.Should().Contain("\"parents\":[\"p1\"]").And.Contain("\"name\":\"a.bin\"");
        h.Requests[1].Method.Should().Be(HttpMethod.Put);
        h.Requests[1].Url.Should().Contain("upload_id=abc");
    }

    [Fact]
    public async Task Update_sends_parents_in_query_and_name_in_body()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, """{"id":"1","name":"n","mimeType":"text/plain"}"""));
        await Api(h).UpdateAsync("1", new GDriveUpdate(Name: "n", AddParent: "b", RemoveParent: "a"), CancellationToken.None);
        h.Requests[0].Method.Should().Be(HttpMethod.Patch);
        h.Requests[0].Url.Should().Contain("addParents=b").And.Contain("removeParents=a");
        h.Requests[0].Body.Should().Be("""{"name":"n"}""");
    }

    private sealed class SyncProgress(Action<long> a) : IProgress<long>
    {
        public void Report(long value) => a(value);
    }
}
