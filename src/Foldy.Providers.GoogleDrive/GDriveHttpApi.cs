using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Foldy.Providers.GoogleDrive;

/// <summary>액세스 토큰 공급. forceRefresh면 캐시를 버리고 새로 받는다(401 뒤 한 번).</summary>
public interface IAccessTokenSource
{
    Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct);
}

/// <summary>
/// Drive REST v3를 HttpClient로 직접 부른다(Google SDK 없이: 의존성을 줄이고 프록시 처리를 앱과 같게 하려고).
/// <list type="bullet">
/// <item>401이면 토큰을 새로 받아 한 번 다시 보낸다. 그래도 401이면 <see cref="GDriveAuthException"/>.</item>
/// <item>429·5xx·rateLimitExceeded는 지수 백오프로 최대 4번 다시 보낸다(업로드 본문 전송은 제외).</item>
/// </list>
/// </summary>
public sealed class GDriveHttpApi : IGDriveApi
{
    public const string ApiBase = "https://www.googleapis.com/drive/v3/";
    public const string UploadBase = "https://www.googleapis.com/upload/drive/v3/";

    internal const string FileFields = "id,name,mimeType,size,modifiedTime,createdTime,webViewLink,shortcutDetails(targetId,targetMimeType)";

    private const int MaxRetries = 4;

    private readonly HttpClient _http;
    private readonly IAccessTokenSource _tokens;
    private readonly Func<int, TimeSpan> _backoff;

    public GDriveHttpApi(HttpClient http, IAccessTokenSource tokens, Func<int, TimeSpan>? backoff = null)
    {
        _http = http;
        _tokens = tokens;
        _backoff = backoff ?? (n => TimeSpan.FromMilliseconds(500 * Math.Pow(2, n) + Random.Shared.Next(250)));
    }

    public async Task<GDriveFile> GetAsync(string id, CancellationToken ct)
    {
        using var doc = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}files/{Uri.EscapeDataString(id)}?fields={Uri.EscapeDataString(FileFields)}&supportsAllDrives=true"), ct).ConfigureAwait(false);
        return ParseFile(doc.RootElement);
    }

    public async Task<GDriveFilePage> ListChildrenAsync(string folderId, string? pageToken, CancellationToken ct)
    {
        var q = $"'{EscapeQuery(folderId)}' in parents and trashed = false";
        var url = $"{ApiBase}files?q={Uri.EscapeDataString(q)}&orderBy={Uri.EscapeDataString("folder,name,createdTime")}&pageSize=1000" +
                  $"&fields={Uri.EscapeDataString("nextPageToken,files(" + FileFields + ")")}&supportsAllDrives=true&includeItemsFromAllDrives=true" +
                  (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
        using var doc = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
        return ParsePage(doc.RootElement);
    }

    public async Task<IReadOnlyList<GDriveFile>> FindChildrenByNameAsync(string folderId, string name, CancellationToken ct)
    {
        var q = $"'{EscapeQuery(folderId)}' in parents and name = '{EscapeQuery(name)}' and trashed = false";
        var url = $"{ApiBase}files?q={Uri.EscapeDataString(q)}&orderBy=createdTime&pageSize=100" +
                  $"&fields={Uri.EscapeDataString("files(" + FileFields + ")")}&supportsAllDrives=true&includeItemsFromAllDrives=true";
        using var doc = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
        return ParsePage(doc.RootElement).Files;
    }

    public Task DownloadAsync(string id, Stream destination, IProgress<long>? progress, CancellationToken ct) =>
        DownloadUrlAsync($"{ApiBase}files/{Uri.EscapeDataString(id)}?alt=media&supportsAllDrives=true", destination, progress, ct);

    public Task ExportAsync(string id, string mimeType, Stream destination, IProgress<long>? progress, CancellationToken ct) =>
        DownloadUrlAsync($"{ApiBase}files/{Uri.EscapeDataString(id)}/export?mimeType={Uri.EscapeDataString(mimeType)}", destination, progress, ct);

    public async Task<GDriveFile> UploadAsync(string parentId, string name, Stream content, long length, IProgress<long>? progress, CancellationToken ct)
    {
        // 1) 재개 가능 업로드 세션 만들기 (메타데이터만)
        var meta = new JsonObject { ["name"] = name, ["parents"] = new JsonArray(parentId) }.ToJsonString();
        Uri? session = null;
        using (var resp = await SendAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Post,
                $"{UploadBase}files?uploadType=resumable&supportsAllDrives=true&fields={Uri.EscapeDataString(FileFields)}")
            {
                Content = new StringContent(meta, Encoding.UTF8, "application/json"),
            };
            r.Headers.TryAddWithoutValidation("X-Upload-Content-Length", length.ToString(CultureInfo.InvariantCulture));
            return r;
        }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
        {
            session = resp.Headers.Location ?? throw new IOException("Google Drive가 업로드 주소를 주지 않았습니다");
        }

        // 2) 본문 한 번에 보내기 (끊기면 실패로 돌려준다: 호출자가 다시 시도)
        using var body = new ProgressStreamContent(content, length, progress);
        using var put = new HttpRequestMessage(HttpMethod.Put, session) { Content = body };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAccessTokenAsync(false, ct).ConfigureAwait(false));
        using var res = await _http.SendAsync(put, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw await ToExceptionAsync(res, ct).ConfigureAwait(false);
        }

        await using var s = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        return ParseFile(doc.RootElement);
    }

    public async Task<GDriveFile> CreateFolderAsync(string parentId, string name, CancellationToken ct)
    {
        var meta = new JsonObject { ["name"] = name, ["mimeType"] = GDriveFile.FolderMimeType, ["parents"] = new JsonArray(parentId) }.ToJsonString();
        using var doc = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Post,
            $"{ApiBase}files?supportsAllDrives=true&fields={Uri.EscapeDataString(FileFields)}")
        {
            Content = new StringContent(meta, Encoding.UTF8, "application/json"),
        }, ct).ConfigureAwait(false);
        return ParseFile(doc.RootElement);
    }

    public async Task<GDriveFile> UpdateAsync(string id, GDriveUpdate update, CancellationToken ct)
    {
        var body = new JsonObject();
        if (update.Name is not null)
        {
            body["name"] = update.Name;
        }

        if (update.Trashed is { } t)
        {
            body["trashed"] = t;
        }

        var url = new StringBuilder($"{ApiBase}files/{Uri.EscapeDataString(id)}?supportsAllDrives=true&fields={Uri.EscapeDataString(FileFields)}");
        if (update.AddParent is not null)
        {
            url.Append("&addParents=").Append(Uri.EscapeDataString(update.AddParent));
        }

        if (update.RemoveParent is not null)
        {
            url.Append("&removeParents=").Append(Uri.EscapeDataString(update.RemoveParent));
        }

        var json = body.ToJsonString();
        using var doc = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Patch, url.ToString())
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }, ct).ConfigureAwait(false);
        return ParseFile(doc.RootElement);
    }

    public async Task<GDriveFile> CopyAsync(string id, string parentId, string name, CancellationToken ct)
    {
        var meta = new JsonObject { ["name"] = name, ["parents"] = new JsonArray(parentId) }.ToJsonString();
        using var doc = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Post,
            $"{ApiBase}files/{Uri.EscapeDataString(id)}/copy?supportsAllDrives=true&fields={Uri.EscapeDataString(FileFields)}")
        {
            Content = new StringContent(meta, Encoding.UTF8, "application/json"),
        }, ct).ConfigureAwait(false);
        return ParseFile(doc.RootElement);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete,
            $"{ApiBase}files/{Uri.EscapeDataString(id)}?supportsAllDrives=true"), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
    }

    public async Task<GDriveAbout> AboutAsync(CancellationToken ct)
    {
        using var doc = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}about?fields={Uri.EscapeDataString("user(emailAddress,displayName),storageQuota(limit,usage)")}"), ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var user = root.GetProperty("user");
        long? limit = null;
        long usage = 0;
        if (root.TryGetProperty("storageQuota", out var q))
        {
            limit = ParseLong(q, "limit");
            usage = ParseLong(q, "usage") ?? 0;
        }

        return new GDriveAbout(
            user.TryGetProperty("emailAddress", out var e) ? e.GetString() ?? "" : "",
            user.TryGetProperty("displayName", out var d) ? d.GetString() : null,
            limit,
            usage);
    }

    /// <summary>q 문자열 리터럴 안의 \ 와 ' 를 이스케이프한다.</summary>
    internal static string EscapeQuery(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    internal static GDriveFilePage ParsePage(JsonElement root)
    {
        var list = new List<GDriveFile>();
        if (root.TryGetProperty("files", out var files))
        {
            foreach (var f in files.EnumerateArray())
            {
                list.Add(ParseFile(f));
            }
        }

        var next = root.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
        return new GDriveFilePage(list, string.IsNullOrEmpty(next) ? null : next);
    }

    internal static GDriveFile ParseFile(JsonElement f)
    {
        string? targetId = null, targetMime = null;
        if (f.TryGetProperty("shortcutDetails", out var sc))
        {
            targetId = sc.TryGetProperty("targetId", out var ti) ? ti.GetString() : null;
            targetMime = sc.TryGetProperty("targetMimeType", out var tm) ? tm.GetString() : null;
        }

        return new GDriveFile(
            f.GetProperty("id").GetString() ?? "",
            f.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            f.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "" : "",
            ParseLong(f, "size") ?? 0,
            ParseTime(f, "modifiedTime"),
            ParseTime(f, "createdTime"),
            f.TryGetProperty("webViewLink", out var link) ? link.GetString() : null,
            targetId,
            targetMime);
    }

    private static long? ParseLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetInt64()
            : long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null
            : null;

    private static DateTime ParseTime(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : DateTime.MinValue;

    private async Task DownloadUrlAsync(string url, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[256 * 1024];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            total += n;
            progress?.Report(total);
        }
    }

    private async Task<JsonDocument> SendJsonAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        using var resp = await SendAsync(make, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>토큰을 붙여 보내고, 401·429·5xx를 처리한다. 성공 응답만 돌려준다(호출자가 Dispose).</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, HttpCompletionOption option, CancellationToken ct)
    {
        var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            var token = await _tokens.GetAccessTokenAsync(false, ct).ConfigureAwait(false);
            using var req = make();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, option, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                await Task.Delay(_backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }

            if (resp.IsSuccessStatusCode)
            {
                return resp;
            }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
                {
                    refreshed = true;
                    _ = await _tokens.GetAccessTokenAsync(true, ct).ConfigureAwait(false);
                    attempt--;
                    continue;
                }

                var err = await ToExceptionAsync(resp, ct).ConfigureAwait(false);
                if (attempt < MaxRetries && IsTransient(resp.StatusCode, err.Message))
                {
                    await Task.Delay(_backoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }

                throw err;
            }
        }
    }

    private static bool IsTransient(HttpStatusCode code, string message) =>
        code is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
        || (code == HttpStatusCode.Forbidden && (message.Contains("rateLimitExceeded", StringComparison.Ordinal) || message.Contains("userRateLimitExceeded", StringComparison.Ordinal)));

    /// <summary>Drive 오류 JSON을 사용자에게 보여 줄 예외로.</summary>
    internal static async Task<IOException> ToExceptionAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        string text;
        try
        {
            text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            text = "";
        }

        string? message = null, reason = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                message = e.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (e.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0 &&
                    errs[0].TryGetProperty("reason", out var r))
                {
                    reason = r.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        var detail = $"{(int)resp.StatusCode} {reason ?? resp.ReasonPhrase}: {message ?? text}".Trim();
        return resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new GDriveAuthException("Google 로그인이 만료됐습니다. 다시 로그인하세요."),
            HttpStatusCode.NotFound => new FileNotFoundException($"Google Drive에서 찾을 수 없습니다 ({detail})"),
            HttpStatusCode.Forbidden when reason is "storageQuotaExceeded" or "quotaExceeded" => new IOException("Google Drive 저장 공간이 부족합니다"),
            HttpStatusCode.Forbidden when reason is "insufficientFilePermissions" or "insufficientPermissions" =>
                new IOException($"이 파일을 바꿀 권한이 없습니다 ({detail})"),
            HttpStatusCode.ProxyAuthenticationRequired => new IOException("프록시 인증에 실패했습니다 (Windows 프록시 설정 확인)"),
            _ => new IOException($"Google Drive 오류 {detail}"),
        };
    }

    /// <summary>업로드 본문: 읽은 만큼 진행률을 알린다.</summary>
    private sealed class ProgressStreamContent(Stream source, long length, IProgress<long>? progress) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new byte[256 * 1024];
            long total = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                total += n;
                progress?.Report(total);
            }
        }

        protected override bool TryComputeLength(out long len)
        {
            len = length;
            return true;
        }
    }
}
