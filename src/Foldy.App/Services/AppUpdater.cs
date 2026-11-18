using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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
    private string? _etag;

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
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
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

    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            return false;
        }

        try
        {
            LastError = null;
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            if (_etag is not null)
            {
                req.Headers.IfNoneMatch.ParseAdd(_etag);
            }

            using var res = await _http.SendAsync(req, ct);
            LastCheck = DateTime.Now;
            if (res.StatusCode == HttpStatusCode.NotModified || res.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            res.EnsureSuccessStatusCode();
            _etag = res.Headers.ETag?.ToString();
            var rel = await res.Content.ReadFromJsonAsync<Release>(Json, ct) ?? throw new InvalidDataException("빈 응답");
            var version = rel.TagName.TrimStart('v');
            if (rel.Draft || rel.Prerelease || !IsNewer(version, _current) || ReadyVersion == version)
            {
                return false;
            }

            await PrepareAsync(rel, version, ct);
            return ReadyVersion == version;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or TaskCanceledException or CryptographicException or System.ComponentModel.Win32Exception)
        {
            // 네트워크·GitHub 장애는 조용히 다음 주기에 다시 (설계서 10.5 실패 대비)
            LastError = ex.Message;
            Log.Information("업데이트 확인 실패: {Message}", ex.Message);
            return false;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    private async Task PrepareAsync(Release rel, string version, CancellationToken ct)
    {
        var dir = Path.Combine(_root, version);
        Directory.CreateDirectory(dir);
        var manifest = await DownloadAsset(rel, "update.json", dir, ct);
        var sig = await DownloadAsset(rel, "update.json.sig", dir, ct);

        // 1) Ed25519 서명 (앱에 내장된 공개키, Rust 검증기)
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
        using (var p = Process.Start(psi)!)
        {
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
            {
                throw new CryptographicException("update.json 서명이 맞지 않습니다: " + (await p.StandardError.ReadToEndAsync(ct)).Trim());
            }
        }

        var info = JsonSerializer.Deserialize<UpdateManifest>(await File.ReadAllBytesAsync(manifest, ct), Json) ?? throw new InvalidDataException("update.json");
        if (info.Version != version)
        {
            throw new InvalidDataException($"버전 불일치: 태그 {version}, update.json {info.Version}");
        }

        var asset = info.Assets.FirstOrDefault(a => a.Name.EndsWith("-x64-setup.zip", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("setup zip이 없습니다");

        // 2) 크기·SHA-256
        var zip = await DownloadAsset(rel, asset.Name, dir, ct);
        var len = new FileInfo(zip).Length;
        string hash;
        await using (var fs = File.OpenRead(zip))
        {
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
        }

        if (len != asset.Size || !string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            throw new CryptographicException("내려받은 파일이 update.json과 다릅니다 (크기 또는 SHA-256)");
        }

        // 3) 풀어 두기 (경로 탈출 방지: ZipFile은 상대 경로 밖을 막는다)
        var setup = Path.Combine(dir, "setup");
        if (Directory.Exists(setup))
        {
            Directory.Delete(setup, true);
        }

        ZipFile.ExtractToDirectory(zip, setup);
        if (!File.Exists(Path.Combine(setup, "Foldy-Setup.ps1")))
        {
            throw new InvalidDataException("설치 스크립트가 없습니다");
        }

        ReadyVersion = version;
        ReadyFolder = setup;
        Log.Information("업데이트 {Version} 설치 준비됨: {Folder}", version, setup);
    }

    private async Task<string> DownloadAsset(Release rel, string name, string dir, CancellationToken ct)
    {
        var a = rel.Assets.FirstOrDefault(x => x.Name == name) ?? throw new InvalidDataException($"릴리스에 {name}이 없습니다");
        var path = Path.Combine(dir, name);
        if (File.Exists(path) && new FileInfo(path).Length == a.Size)
        {
            return path;
        }

        using var res = await _http.GetAsync(a.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using var fs = File.Create(path + ".part");
        await res.Content.CopyToAsync(fs, ct);
        await fs.DisposeAsync();
        File.Move(path + ".part", path, overwrite: true);
        return path;
    }

    /// <summary>관리자 권한으로 설치 스크립트를 실행한다. 호출자는 바로 앱을 종료해야 한다. UAC를 취소하면 false.</summary>
    public bool LaunchInstaller()
    {
        if (ReadyFolder is null)
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = ReadyFolder,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(ReadyFolder, "Foldy-Setup.ps1")}\"";
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

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string TagName,
        bool Draft,
        bool Prerelease,
        IReadOnlyList<ReleaseAsset> Assets);

    private sealed record ReleaseAsset(
        string Name,
        long Size,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

    private sealed record UpdateManifest(string Version, IReadOnlyList<ManifestAsset> Assets);

    private sealed record ManifestAsset(string Name, long Size, string Sha256);
}
