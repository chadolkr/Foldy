using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Foldy.Presentation.Navigation;
using Foldy.Presentation.Services;
using Foldy.Providers.GoogleDrive;

namespace Foldy.App.Services;

/// <summary>
/// Google Drive 연결 관리: 계정 저장소(DPAPI), OAuth 로그인, 계정별 토큰·API, Provider·전송.
/// 모든 HTTP는 Windows 시스템 프록시(PAC/WPAD)와 로그인 사용자 자격 증명(인증 프록시)을 쓴다.
/// </summary>
public sealed class GDriveSessions : IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (GDriveTokenSource Tokens, IGDriveApi Api)> _apis = new(StringComparer.Ordinal);

    public GDriveSessions()
    {
        _http = new HttpClient(Foldy.Reporting.CrashSender.CreateSystemProxyHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // 큰 파일 전송: 취소는 작업 패널의 CancellationToken으로
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Foldy/{typeof(GDriveSessions).Assembly.GetName().Version?.ToString(3)}");
        Store = new GDriveAccountStore(GDriveAccountStore.DefaultPath, DpapiSecretProtector.Instance);
        Provider = new GoogleDriveProvider(ApiFor);
        Transfer = new GDriveTransfer(Provider);
    }

    public GDriveAccountStore Store { get; }

    public GoogleDriveProvider Provider { get; }

    public GDriveTransfer Transfer { get; }

    /// <summary>쓸 OAuth 클라이언트: 사용자가 넣은 값 → 빌드에 주입된 값. 둘 다 없으면 null(연결 창이 입력을 받는다).</summary>
    public GoogleOAuthClient? Client => Store.GetUserClient() ?? GoogleOAuthClient.FromBuild();

    public static bool HasBuiltInClient => GoogleOAuthClient.FromBuild() is not null;

    public static string RootPath(string email) => PathUtil.GDriveScheme + GDriveLocation.NormalizeAccount(email) + "/";

    /// <summary>
    /// 시스템 브라우저로 Google 로그인 → 토큰 → 계정 저장. 연결한 계정 이메일을 돌려준다.
    /// 브라우저에서 거부하면 <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<string> SignInAsync(CancellationToken ct)
    {
        var client = Client ?? throw new InvalidOperationException("Google OAuth 클라이언트 ID가 없습니다");
        var oauth = new GoogleOAuth(_http, client);
        var tokens = await oauth.SignInAsync(OpenBrowser, null, ct);
        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            throw new GDriveAuthException("Google이 갱신 토큰을 주지 않았습니다. 다시 시도하세요.");
        }

        var source = new GDriveTokenSource(oauth, tokens.RefreshToken);
        source.Seed(tokens);
        var api = new GDriveHttpApi(_http, source);
        var about = await api.AboutAsync(ct);
        var email = GDriveLocation.NormalizeAccount(about.Email);
        if (email.Length == 0)
        {
            throw new GDriveAuthException("Google 계정 이메일을 알 수 없습니다");
        }

        Store.Upsert(email, about.DisplayName, tokens.RefreshToken, client.ClientId);
        if (_apis.TryRemove(email, out var old))
        {
            old.Tokens.Dispose();
        }

        _apis[email] = (source, api);
        Provider.ForgetAccount(email);
        return email;
    }

    /// <summary>연결 끊기: Google에서 토큰을 취소하고(오프라인이면 건너뜀) 저장소·캐시에서 지운다.</summary>
    public async Task DisconnectAsync(string email, CancellationToken ct)
    {
        email = GDriveLocation.NormalizeAccount(email);
        var account = Store.Find(email);
        if (account is not null && Store.GetRefreshToken(account) is { } rt && Client is { } client)
        {
            await new GoogleOAuth(_http, client).RevokeAsync(rt, ct);
        }

        Store.Remove(email);
        if (_apis.TryRemove(email, out var old))
        {
            old.Tokens.Dispose();
        }

        Provider.ForgetAccount(email);
    }

    public Task<GDriveAbout> AboutAsync(string email, CancellationToken ct) => Provider.Api(email).AboutAsync(ct);

    /// <summary>열기·미리보기용 로컬 캐시(%LOCALAPPDATA%\Foldy\gdrive-cache)를 비운다.</summary>
    public void ClearCache()
    {
        var dir = Provider.CacheDirectory;
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 열려 있는 파일은 다음에 지운다.
            }
        }
    }

    private IGDriveApi? ApiFor(string account)
    {
        if (_apis.TryGetValue(account, out var e))
        {
            return e.Api;
        }

        var a = Store.Find(account);
        if (a is null || Store.GetRefreshToken(a) is not { } rt)
        {
            return null;
        }

        // 토큰을 받은 클라이언트와 지금 클라이언트가 다르면 refresh가 실패하므로 그 계정의 원래 클라이언트를 고른다.
        var client = Client;
        if (client is null || (a.ClientId.Length > 0 && a.ClientId != client.ClientId))
        {
            client = GoogleOAuthClient.FromBuild() is { } b && b.ClientId == a.ClientId ? b
                : Store.GetUserClient() is { } u && u.ClientId == a.ClientId ? u
                : client;
        }

        if (client is null)
        {
            return null;
        }

        var tokens = new GDriveTokenSource(new GoogleOAuth(_http, client), rt);
        var api = new GDriveHttpApi(_http, tokens);
        return _apis.GetOrAdd(account, (tokens, api)).Api;
    }

    private static void OpenBrowser(Uri uri)
    {
        using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void Dispose()
    {
        foreach (var e in _apis.Values)
        {
            e.Tokens.Dispose();
        }

        _http.Dispose();
    }
}

/// <summary>
/// 파일 작업 앞단: gdrive:// 경로가 끼면 Google Drive가 처리하고, 아니면 원래 서비스(Rust 복사 엔진 → 셸)로 넘긴다.
/// 복사 경로는 F5/F6·붙여넣기·끌어 놓기가 모두 같다(ShellViewModel.TransferAsync).
/// </summary>
public sealed class GDriveFileOperationService(IFileOperationService inner, GDriveSessions drive) : IFileOperationService
{
    public Task<FileOpResult> CopyAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) =>
        Involves(sources, destinationFolder) ? TransferAsync(sources, destinationFolder, false, progress, ct) : inner.CopyAsync(sources, destinationFolder, progress, ct);

    public Task<FileOpResult> MoveAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) =>
        Involves(sources, destinationFolder) ? TransferAsync(sources, destinationFolder, true, progress, ct) : inner.MoveAsync(sources, destinationFolder, progress, ct);

    public async Task<FileOpResult> DeleteAsync(IReadOnlyList<string> paths, bool recycle, IProgress<FileOpProgress>? progress, CancellationToken ct)
    {
        var remote = paths.Where(PathUtil.IsGDrive).ToArray();
        if (remote.Length == 0)
        {
            return await inner.DeleteAsync(paths, recycle, progress, ct);
        }

        var local = paths.Where(p => !PathUtil.IsGDrive(p)).ToArray();
        return await Run(async () =>
        {
            for (var i = 0; i < remote.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new FileOpProgress(i, remote.Length + local.Length, PathUtil.DisplayName(remote[i])));
                await drive.Provider.DeleteAsync(remote[i], permanent: !recycle, ct);
            }

            if (local.Length > 0)
            {
                var r = await inner.DeleteAsync(local, recycle, progress, ct);
                if (!r.Succeeded)
                {
                    return r;
                }
            }

            progress?.Report(new FileOpProgress(1, 1, null));
            return FileOpResult.Ok();
        });
    }

    public Task<FileOpResult> RenameAsync(string path, string newName, CancellationToken ct) =>
        PathUtil.IsGDrive(path)
            ? Run(async () => FileOpResult.Ok(await drive.Provider.RenameAsync(path, newName, ct)))
            : inner.RenameAsync(path, newName, ct);

    public Task<FileOpResult> NewFolderAsync(string parent, string name, CancellationToken ct) =>
        PathUtil.IsGDrive(parent)
            ? Run(async () => FileOpResult.Ok(await drive.Provider.CreateFolderAsync(parent, name, ct)))
            : inner.NewFolderAsync(parent, name, ct);

    private static bool Involves(IReadOnlyList<string> sources, string dest) => PathUtil.IsGDrive(dest) || sources.Any(PathUtil.IsGDrive);

    private Task<FileOpResult> TransferAsync(IReadOnlyList<string> sources, string dest, bool move, IProgress<FileOpProgress>? progress, CancellationToken ct)
    {
        if (PathUtil.IsSftp(dest) || sources.Any(PathUtil.IsSftp))
        {
            return Task.FromResult(FileOpResult.Fail("SFTP와 Google Drive 사이 복사는 아직 지원하지 않습니다. 먼저 내 PC로 복사하세요."));
        }

        return Run(async () =>
        {
            var err = await drive.Transfer.TransferAsync(sources, dest, move,
                (done, total, cur) => progress?.Report(new FileOpProgress(done, total, cur)), ct);
            return err is null ? FileOpResult.Ok() : FileOpResult.Fail(err);
        });
    }

    /// <summary>네트워크·Drive 오류를 작업 패널의 실패 문장으로 바꾼다. 취소는 Canceled.</summary>
    private static async Task<FileOpResult> Run(Func<Task<FileOpResult>> work)
    {
        try
        {
            return await Task.Run(work);
        }
        catch (OperationCanceledException)
        {
            return FileOpResult.Canceled;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return FileOpResult.Fail(Describe(ex));
        }
    }

    internal static string Describe(Exception ex) => ex switch
    {
        GDriveAuthException => ex.Message + " (사이드바 › Google Drive에서 다시 연결)",
        HttpRequestException h when h.StatusCode is null => "인터넷에 연결할 수 없습니다 (프록시·네트워크 확인)",
        HttpRequestException h when h.StatusCode == HttpStatusCode.ProxyAuthenticationRequired => "프록시 인증에 실패했습니다",
        _ => ex.Message,
    };
}

/// <summary>열기 앞단: Google Drive 파일은 캐시에 내려받아 연다. Google 문서·시트는 브라우저에서 연다.</summary>
public sealed class GDriveOpenFileService(IOpenFileService inner, GDriveSessions drive) : IOpenFileService
{
    public Task<bool> ShouldUseBuiltInMarkdownAsync(string path) => inner.ShouldUseBuiltInMarkdownAsync(path);

    public void OpenWithShell(string path) => inner.OpenWithShell(path);

    public async Task<string?> PrepareAsync(string path, IProgress<FileOpProgress>? progress, CancellationToken ct)
    {
        if (!PathUtil.IsGDrive(path))
        {
            return path;
        }

        try
        {
            var file = await drive.Provider.ResolveAsync(path, ct);
            if (file.IsGoogleApp)
            {
                var link = file.WebViewLink ?? $"https://drive.google.com/open?id={Uri.EscapeDataString(file.EffectiveId)}";
                using var _ = Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
                return null;
            }

            var size = file.Size;
            return await Task.Run(() => drive.Provider.FetchToCacheAsync(path,
                progress is null ? null : new SyncProgress(n => progress.Report(new FileOpProgress(n, Math.Max(size, n), file.Name))), ct), ct);
        }
        catch (GDriveAuthException ex)
        {
            throw new IOException(GDriveFileOperationService.Describe(ex), ex);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException(GDriveFileOperationService.Describe(ex), ex);
        }
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
