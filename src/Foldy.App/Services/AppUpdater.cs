using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Foldy.App.Services;

/// <summary>
/// GitHub Releases 자동 업데이트 (설계서 10.5, 0.1 단계 구현).
/// <list type="number">
/// <item>실행 1분 뒤 + 6시간마다 releases/latest 확인 (ETag로 304 처리, 인증 없음).</item>
/// <item>새 버전이면 update.json과 .sig를 받아 Rust foldy-update로 Ed25519 서명 검사 (공개키는 빌드에 내장).</item>
/// <item>setup zip을 받아 update.json의 크기·SHA-256과 대조, 풀어 둔다.</item>
/// <item>사용자가 [재시작하고 업데이트]를 누르면 관리자 권한(UAC 한 번)으로 설치 스크립트를 실행하고 앱을 닫는다.</item>
/// </list>
/// 네트워크는 Windows 시스템 프록시(PAC/WPAD 포함)와 로그온 사용자 자격 증명을 그대로 쓴다 (사내 프록시 대응).
/// 나중에 MSI가 생기면 설치는 색인 서비스(SYSTEM)가 맡아 UAC 없이 진행한다.
/// </summary>
public sealed class AppUpdater : IDisposable
{
    public const string Repo = "chadolkr/Foldy";
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly string _root = Path.Combine(App.DataDir, "Updates");
    private readonly string _current;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;

    public AppUpdater(string currentVersion)
    {
        _current = currentVersion;
        var handler = new SocketsHttpHandler
        {
            // 시스템 프록시 + 사내 프록시 인증(NTLM/Negotiate)을 로그온 자격 증명으로
            UseProxy = true,
            Proxy = WebRequest.GetSystemWebProxy(),
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AutomaticDecompression = DecompressionMethods.All,
        };
        handler.Proxy.Credentials = CredentialCache.DefaultCredentials;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Foldy-Updater/{currentVersion}");
    }

    /// <summary>설치 준비가 끝난 버전 (없으면 null).</summary>
    public string? ReadyVersion { get; private set; }

    public string? ReadyFolder { get; private set; }

    public DateTime? LastCheck { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>상태가 바뀌면 (아무 스레드에서) 호출된다.</summary>
    public event Action? Changed;

    public void Start(TimeSpan firstDelay) =>
        _timer = new Timer(_ => _ = CheckAsync(CancellationToken.None), null, firstDelay, Period);

    /// <summary>
    /// 확인: GitHub API(익명 IP당 시간 60회 제한 — 회사처럼 출구 IP를 공유하면 금방 403) 대신
    /// 릴리스 파일 주소 <c>releases/latest/download/update.json</c>을 직접 받는다 (한도 없음, 프록시 그대로).
    /// update.json은 Ed25519 서명을 먼저 검사하고, 그 안의 버전·크기·SHA-256으로 MSI를 검증한다.
    /// </summary>
    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            return false;
        }

        try
        {
            LastError = null;
            var check = Path.Combine(_root, "check");
            Directory.CreateDirectory(check);
            var manifest = await DownloadAsync($"https://github.com/{Repo}/releases/latest/download/update.json", Path.Combine(check, "update.json"), ct);
            var sig = await DownloadAsync($"https://github.com/{Repo}/releases/latest/download/update.json.sig", Path.Combine(check, "update.json.sig"), ct);
            LastCheck = DateTime.Now;
            await VerifySignatureAsync(manifest, sig, ct);

            var info = JsonSerializer.Deserialize<UpdateManifest>(await File.ReadAllBytesAsync(manifest, ct), Json) ?? throw new InvalidDataException("update.json");
            var version = info.Version;
            if (!IsNewer(version, _current))
            {
                Log.Information("업데이트 확인: 최신 버전 (현재 {Current}, 릴리스 {Latest})", _current, version);
                return false;
            }

            if (ReadyVersion == version)
            {
                return true;
            }

            var asset = info.Assets.FirstOrDefault(a => a.Name.EndsWith("-x64.msi", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("update.json에 MSI가 없습니다");
            var dir = Path.Combine(_root, version);
            Directory.CreateDirectory(dir);
            var msi = Path.Combine(dir, asset.Name);
            if (!(File.Exists(msi) && await MatchesAsync(msi, asset, ct)))
            {
                await DownloadAsync($"https://github.com/{Repo}/releases/download/v{version}/{asset.Name}", msi, ct);
                if (!await MatchesAsync(msi, asset, ct))
                {
                    File.Delete(msi);
                    throw new CryptographicException("내려받은 MSI가 update.json과 다릅니다 (크기 또는 SHA-256)");
                }
            }

            ReadyVersion = version;
            ReadyFolder = dir;
            ReadyMsi = msi;
            Log.Information("업데이트 {Version} 설치 준비됨: {Msi}", version, msi);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or TaskCanceledException or CryptographicException or System.ComponentModel.Win32Exception)
        {
            // 네트워크·GitHub 장애는 조용히 다음 주기에 다시 (설계서 10.5 실패 대비)
            LastError = ex is HttpRequestException { StatusCode: { } code } ? $"HTTP {(int)code} {code}: {ex.Message}" : ex.Message;
            Log.Information("업데이트 확인 실패: {Message}", LastError);
            return false;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public string? ReadyMsi { get; private set; }

    private static async Task<bool> MatchesAsync(string path, ManifestAsset asset, CancellationToken ct)
    {
        if (new FileInfo(path).Length != asset.Size)
        {
            return false;
        }

        await using var fs = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
        return string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task VerifySignatureAsync(string manifest, string sig, CancellationToken ct)
    {
        // Ed25519 서명 (앱에 내장된 공개키, Rust 검증기)
        var tool = Path.Combine(AppContext.BaseDirectory, "foldy-update.exe");
        if (!File.Exists(tool))
        {
            throw new IOException("foldy-update.exe가 없어 서명을 검사할 수 없습니다");
        }

        var psi = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("verify");
        psi.ArgumentList.Add(manifest);
        psi.ArgumentList.Add("--sig");
        psi.ArgumentList.Add(sig);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            throw new CryptographicException("update.json 서명이 맞지 않습니다: " + (await p.StandardError.ReadToEndAsync(ct)).Trim());
        }
    }

    private async Task<string> DownloadAsync(string url, string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using (var fs = File.Create(path + ".part"))
        {
            await res.Content.CopyToAsync(fs, ct);
        }

        File.Move(path + ".part", path, overwrite: true);
        return path;
    }

    /// <summary>
    /// 관리자 권한(UAC 한 번)으로 MSI를 설치하고, 끝나면 탐색기를 통해(일반 권한) Foldy를 다시 띄운다.
    /// 호출자는 바로 앱을 종료해야 한다. UAC를 취소하면 false.
    /// </summary>
    public bool LaunchInstaller()
    {
        if (ReadyMsi is null)
        {
            return false;
        }

        try
        {
            var exe = Environment.ProcessPath!;
            var log = Path.Combine(_root, $"install-{ReadyVersion}.log");
            var script = $"Start-Sleep -Seconds 2; $p = Start-Process msiexec.exe -Wait -PassThru -ArgumentList '/i \"{ReadyMsi}\" /passive /norestart /l*v \"{log}\"'; " +
                         $"Start-Process explorer.exe -ArgumentList '\"{exe}\"'";
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
            };
            using var _ = Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
    }

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(Core(candidate), out var a) && Version.TryParse(Core(current), out var b) && a > b;

    private static string Core(string v)
    {
        var s = v.Split('+', '-')[0];
        return s.Count(c => c == '.') == 1 ? s + ".0" : s;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _http.Dispose();
        _gate.Dispose();
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record UpdateManifest(string Version, IReadOnlyList<ManifestAsset> Assets);

    private sealed record ManifestAsset(string Name, long Size, string Sha256);
}
