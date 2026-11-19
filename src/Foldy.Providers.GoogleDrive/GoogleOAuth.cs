using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Foldy.Providers.GoogleDrive;

/// <summary>
/// Google Cloud 콘솔에서 만든 "데스크톱 앱" OAuth 클라이언트.
/// 설치형 앱의 클라이언트 비밀값은 진짜 비밀이 아니지만(Google 문서), 저장소에는 넣지 않고 빌드할 때 주입한다.
/// </summary>
public sealed record GoogleOAuthClient(string ClientId, string? ClientSecret)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>빌드 때 주입한 값(AssemblyMetadata) → 환경 변수 순으로 찾는다. 없으면 null.</summary>
    public static GoogleOAuthClient? FromBuild()
    {
        var meta = typeof(GoogleOAuthClient).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        string? Find(string key) => meta.FirstOrDefault(m => m.Key == key)?.Value is { Length: > 0 } v ? v : null;
        var id = Environment.GetEnvironmentVariable("FOLDY_GOOGLE_CLIENT_ID") is { Length: > 0 } e ? e : Find("FoldyGoogleClientId");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var secret = Environment.GetEnvironmentVariable("FOLDY_GOOGLE_CLIENT_SECRET") is { Length: > 0 } s ? s : Find("FoldyGoogleClientSecret");
        return new GoogleOAuthClient(id.Trim(), secret?.Trim());
    }
}

/// <summary>토큰 응답. RefreshToken은 처음 동의할 때만 온다.</summary>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? Scope);

/// <summary>RFC 7636 PKCE (S256).</summary>
public static class Pkce
{
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// 설치형 앱 OAuth (RFC 8252): 시스템 브라우저 + loopback 리디렉트(127.0.0.1 임의 포트) + PKCE.
/// 범위는 Drive 전체(<see cref="DriveScope"/>) 하나만 요청한다. 파일 관리자는 사용자의 모든 파일을 다뤄야 하므로
/// drive.file(앱이 만든 파일만)로는 부족하다.
/// </summary>
public sealed class GoogleOAuth
{
    public const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    public const string DriveScope = "https://www.googleapis.com/auth/drive";

    /// <summary>브라우저에서 로그인을 기다리는 최대 시간.</summary>
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly GoogleOAuthClient _client;
    private readonly TimeProvider _time;

    public GoogleOAuth(HttpClient http, GoogleOAuthClient client, TimeProvider? time = null)
    {
        _http = http;
        _client = client;
        _time = time ?? TimeProvider.System;
    }

    public GoogleOAuthClient Client => _client;

    public Uri BuildAuthorizeUri(string redirectUri, string codeChallenge, string state, string? loginHint = null)
    {
        var q = new Dictionary<string, string>
        {
            ["client_id"] = _client.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = DriveScope,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline", // refresh token
            ["prompt"] = "consent select_account",
        };
        if (!string.IsNullOrEmpty(loginHint))
        {
            q["login_hint"] = loginHint;
        }

        return new Uri(AuthorizeEndpoint + "?" + string.Join('&', q.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value))));
    }

    /// <summary>
    /// 전체 로그인: loopback 수신 대기 → openBrowser(주소) → 리디렉트로 받은 code를 토큰으로 바꾼다.
    /// 사용자가 거부하면 <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<OAuthTokens> SignInAsync(Action<Uri> openBrowser, string? loginHint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        using var receiver = LoopbackReceiver.Start();
        var verifier = Pkce.CreateVerifier();
        var state = Pkce.CreateState();
        openBrowser(BuildAuthorizeUri(receiver.RedirectUri, Pkce.Challenge(verifier), state, loginHint));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        IReadOnlyDictionary<string, string> query;
        try
        {
            query = await receiver.WaitAsync(state, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("브라우저 로그인을 5분 동안 기다렸지만 끝나지 않았습니다");
        }

        if (query.TryGetValue("error", out var error))
        {
            throw error == "access_denied"
                ? new OperationCanceledException("Google 로그인을 취소했습니다")
                : new GDriveAuthException($"Google 로그인 실패: {error}");
        }

        if (!query.TryGetValue("code", out var code) || code.Length == 0)
        {
            throw new GDriveAuthException("Google 로그인 응답에 code가 없습니다");
        }

        return await ExchangeAsync(code, verifier, receiver.RedirectUri, ct).ConfigureAwait(false);
    }

    public Task<OAuthTokens> ExchangeAsync(string code, string verifier, string redirectUri, CancellationToken ct) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
        }, ct);

    /// <summary>refresh token으로 새 액세스 토큰. 취소된 토큰이면 <see cref="GDriveAuthException"/>.</summary>
    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);

    /// <summary>토큰 취소(연결 끊기). 실패해도 예외를 올리지 않는다(이미 취소됐거나 오프라인).</summary>
    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            using var _ = await _http.PostAsync(new Uri(RevokeEndpoint), content, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
        }
    }

    private async Task<OAuthTokens> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = _client.ClientId;
        if (!string.IsNullOrEmpty(_client.ClientSecret))
        {
            form["client_secret"] = _client.ClientSecret;
        }

        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http.PostAsync(new Uri(TokenEndpoint), content, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new IOException($"Google 토큰 응답을 읽을 수 없습니다 ({(int)resp.StatusCode})");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!resp.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var at))
            {
                var err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
                if (err is "invalid_grant")
                {
                    throw new GDriveAuthException("Google 로그인이 만료됐거나 취소됐습니다. 다시 로그인하세요.");
                }

                if (resp.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                {
                    throw new IOException("프록시 인증에 실패했습니다 (Windows 프록시 설정 확인)");
                }

                throw new GDriveAuthException($"Google 토큰 요청 실패: {err ?? ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture)} {desc}".Trim());
            }

            var expires = root.TryGetProperty("expires_in", out var ex) && ex.TryGetInt32(out var sec) ? sec : 3600;
            return new OAuthTokens(
                at.GetString() ?? "",
                root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                _time.GetUtcNow().AddSeconds(expires),
                root.TryGetProperty("scope", out var sc) ? sc.GetString() : null);
        }
    }
}

/// <summary>
/// 127.0.0.1 임의 포트에서 리디렉트 한 번을 받는 아주 작은 HTTP 수신기.
/// HttpListener는 URL 예약(관리자)이 필요할 수 있어 TcpListener로 직접 받는다. 루프백에만 바인딩한다.
/// </summary>
public sealed class LoopbackReceiver : IDisposable
{
    private readonly TcpListener _listener;

    private LoopbackReceiver(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string RedirectUri => $"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}/";

    public static LoopbackReceiver Start()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return new LoopbackReceiver(l);
    }

    /// <summary>state가 맞는 리디렉트(code 또는 error)가 올 때까지 기다린다. 다른 요청(favicon 등)은 404로 돌려보낸다.</summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(string expectedState, CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            await using var stream = client.GetStream();
            using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
            read.CancelAfter(TimeSpan.FromSeconds(10));
            string? requestLine;
            try
            {
                requestLine = await ReadRequestLineAsync(stream, read.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue;
            }

            var query = ParseRequestLine(requestLine);
            if (query is null || !(query.ContainsKey("code") || query.ContainsKey("error")))
            {
                await RespondAsync(stream, 404, "Not found", ct).ConfigureAwait(false);
                continue;
            }

            if (!query.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            {
                await RespondAsync(stream, 400, "로그인 요청이 맞지 않습니다. Foldy에서 다시 시도하세요.", ct).ConfigureAwait(false);
                continue;
            }

            var ok = query.ContainsKey("code");
            await RespondAsync(stream, 200, ok
                ? "Foldy에 Google Drive를 연결했습니다. 이 창을 닫고 Foldy로 돌아가세요."
                : "Google 로그인을 취소했습니다. 이 창을 닫아도 됩니다.", ct).ConfigureAwait(false);
            return query;
        }
    }

    /// <summary>"GET /?code=..&amp;state=.. HTTP/1.1" → 쿼리. 형식이 틀리면 null.</summary>
    internal static Dictionary<string, string>? ParseRequestLine(string? line)
    {
        if (line is null)
        {
            return null;
        }

        var parts = line.Split(' ');
        if (parts.Length < 2 || parts[0] != "GET")
        {
            return null;
        }

        var target = parts[1];
        var q = target.IndexOf('?', StringComparison.Ordinal);
        var path = q < 0 ? target : target[..q];
        if (path != "/")
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (q < 0)
        {
            return result;
        }

        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var k = eq < 0 ? pair : pair[..eq];
            var v = eq < 0 ? "" : pair[(eq + 1)..];
            try
            {
                result[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        return result;
    }

    private static async Task<string?> ReadRequestLineAsync(Stream stream, CancellationToken ct)
    {
        // 요청 줄과 헤더를 끝(빈 줄)까지 읽는다. 8KB를 넘으면 버린다.
        var buf = new byte[8192];
        var len = 0;
        while (len < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            len += n;
            if (buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8) >= 0)
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(buf, 0, len);
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? (text.Length > 0 ? text : null) : text[..end];
    }

    private static async Task RespondAsync(Stream stream, int status, string message, CancellationToken ct)
    {
        var html = "<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><title>Foldy</title>" +
                   "<style>body{font-family:'Segoe UI','Malgun Gothic',sans-serif;display:flex;height:90vh;align-items:center;justify-content:center;" +
                   "background:#f6f8fa;color:#1f2328}@media(prefers-color-scheme:dark){body{background:#0d1117;color:#e6edf3}}" +
                   "p{font-size:20px;max-width:32em;text-align:center;line-height:1.5}</style></head><body><p>" +
                   WebUtility.HtmlEncode(message) + "</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status.ToString(CultureInfo.InvariantCulture)} {reason}\r\nContent-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        try
        {
            await stream.WriteAsync(head, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose() => _listener.Stop();
}
