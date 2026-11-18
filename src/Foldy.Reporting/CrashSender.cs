using System.Net;
using System.Net.Http.Json;

namespace Foldy.Reporting;

/// <summary>
/// 스크럽된 리포트를 크래시 릴레이(Cloudflare Worker)로 보낸다.
/// 회사망을 위해 Windows 시스템 프록시를 그대로 쓴다:
/// - <see cref="HttpClient.DefaultProxy"/>는 Windows에서 인터넷 옵션(WinINet)의 수동 프록시,
///   자동 구성 스크립트(PAC), 자동 검색(WPAD)과 예외 목록을 따른다.
/// - 인증 프록시(NTLM/Kerberos)는 로그인한 사용자 자격 증명(<see cref="CredentialCache.DefaultCredentials"/>)으로 통과한다.
/// </summary>
public sealed class CrashSender : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    public CrashSender(Uri endpoint, HttpMessageHandler? handler = null)
    {
        _endpoint = endpoint;
        _http = new HttpClient(handler ?? CreateSystemProxyHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Foldy-CrashReporter/1");
    }

    /// <summary>시스템 프록시 + 기본 자격 증명. 테스트에서 설정을 확인할 수 있게 공개한다.</summary>
    public static SocketsHttpHandler CreateSystemProxyHandler() => new()
    {
        UseProxy = true,
        Proxy = HttpClient.DefaultProxy,          // 시스템 설정 (PAC/WPAD 포함)
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.All,
    };

    public async Task<SendResult> SendAsync(CrashReport report, CancellationToken ct = default)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync(_endpoint, report, ReportJson.Default.CrashReport, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadFromJsonAsync(ReportJson.Default.RelayResponse, ct).ConfigureAwait(false);
                return new SendResult(SendOutcome.Sent, body, null);
            }
            return res.StatusCode switch
            {
                // 형식 오류는 다시 보내도 같다 → 버린다
                HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge
                    => new SendResult(SendOutcome.Rejected, null, $"HTTP {(int)res.StatusCode}"),
                HttpStatusCode.ProxyAuthenticationRequired
                    => new SendResult(SendOutcome.RetryLater, null, "프록시 인증 실패 (407)"),
                _ => new SendResult(SendOutcome.RetryLater, null, $"HTTP {(int)res.StatusCode}"),
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new SendResult(SendOutcome.RetryLater, null, e.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}

public enum SendOutcome { Sent, Rejected, RetryLater }

public sealed record SendResult(SendOutcome Outcome, RelayResponse? Response, string? Error);
