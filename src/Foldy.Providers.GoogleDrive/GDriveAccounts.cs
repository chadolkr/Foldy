using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foldy.Providers.GoogleDrive;

/// <summary>refresh token 같은 비밀을 디스크에 쓰기 전에 감싼다. 실제 앱은 DPAPI(현재 사용자), 테스트는 가짜.</summary>
public interface ISecretProtector
{
    string Protect(string plain);

    string Unprotect(string protectedText);
}

/// <summary>현재 Windows 사용자 DPAPI. 다른 사용자·PC에서는 풀리지 않는다.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // 같은 사용자의 다른 앱이 blob을 그대로 풀지 못하게 하는 앱 고유 엔트로피(비밀은 아님).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Foldy.GoogleDrive.Token.v1");

    public static readonly DpapiSecretProtector Instance = new();

    public string Protect(string plain) => Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(plain), Entropy));

    public string Unprotect(string protectedText) => Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(protectedText), Entropy));
}

/// <summary>연결한 Google 계정 하나. RefreshToken은 <see cref="ISecretProtector"/>로 감싼 값만 저장한다.</summary>
public sealed record GDriveAccount
{
    public string Email { get; init; } = "";
    public string? DisplayName { get; init; }

    /// <summary>감싼(DPAPI) refresh token.</summary>
    public string ProtectedRefreshToken { get; init; } = "";

    /// <summary>이 토큰을 받은 OAuth 클라이언트 ID (클라이언트를 바꾸면 다시 로그인해야 한다).</summary>
    public string ClientId { get; init; } = "";

    public DateTime ConnectedUtc { get; init; }
}

/// <summary>사용자가 직접 넣은 OAuth 클라이언트(빌드에 주입된 값이 없을 때). 비밀값은 감싸서 저장한다.</summary>
public sealed record GDriveClientSetting
{
    public string ClientId { get; init; } = "";
    public string? ProtectedClientSecret { get; init; }
}

/// <summary>
/// <c>%LOCALAPPDATA%\Foldy\gdrive.json</c>: 계정 목록과 사용자 OAuth 클라이언트.
/// 저장은 임시 파일에 쓴 뒤 교체한다. 손상된 파일은 <c>.corrupt</c>로 옮기고 빈 상태로 시작한다. 스레드 안전.
/// </summary>
public sealed class GDriveAccountStore(string path, ISecretProtector protector)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy", "gdrive.json");

    public string Path { get; } = path;

    public IReadOnlyList<GDriveAccount> Accounts => Load().Accounts ?? [];

    public GDriveAccount? Find(string email) =>
        Accounts.FirstOrDefault(a => string.Equals(a.Email, GDriveLocation.NormalizeAccount(email), StringComparison.Ordinal));

    /// <summary>같은 이메일이 있으면 바꾼다.</summary>
    public GDriveAccount Upsert(string email, string? displayName, string refreshToken, string clientId)
    {
        var account = new GDriveAccount
        {
            Email = GDriveLocation.NormalizeAccount(email),
            DisplayName = displayName,
            ProtectedRefreshToken = protector.Protect(refreshToken),
            ClientId = clientId,
            ConnectedUtc = DateTime.UtcNow,
        };
        lock (_gate)
        {
            var s = Load();
            var list = (s.Accounts ?? []).Where(a => a.Email != account.Email).Append(account).OrderBy(a => a.Email, StringComparer.Ordinal).ToList();
            Save(s with { Accounts = list });
        }

        return account;
    }

    public void Remove(string email)
    {
        var e = GDriveLocation.NormalizeAccount(email);
        lock (_gate)
        {
            var s = Load();
            Save(s with { Accounts = [.. (s.Accounts ?? []).Where(a => a.Email != e)] });
        }
    }

    /// <summary>풀 수 없으면(다른 PC로 옮긴 파일 등) null.</summary>
    public string? GetRefreshToken(GDriveAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        try
        {
            return account.ProtectedRefreshToken.Length == 0 ? null : protector.Unprotect(account.ProtectedRefreshToken);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public GoogleOAuthClient? GetUserClient()
    {
        var c = Load().Client;
        if (c is null || string.IsNullOrWhiteSpace(c.ClientId))
        {
            return null;
        }

        string? secret = null;
        if (!string.IsNullOrEmpty(c.ProtectedClientSecret))
        {
            try
            {
                secret = protector.Unprotect(c.ProtectedClientSecret);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
            }
        }

        return new GoogleOAuthClient(c.ClientId, secret);
    }

    /// <summary>null이나 빈 ID면 지운다.</summary>
    public void SetUserClient(string? clientId, string? clientSecret)
    {
        lock (_gate)
        {
            var s = Load();
            var client = string.IsNullOrWhiteSpace(clientId)
                ? null
                : new GDriveClientSetting
                {
                    ClientId = clientId.Trim(),
                    ProtectedClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? null : protector.Protect(clientSecret.Trim()),
                };
            Save(s with { Client = client });
        }
    }

    private sealed record State
    {
        public int Version { get; init; } = 1;
        public List<GDriveAccount>? Accounts { get; init; }
        public GDriveClientSetting? Client { get; init; }
    }

    private State Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
            {
                return new State();
            }

            try
            {
                return JsonSerializer.Deserialize<State>(File.ReadAllText(Path), Json) ?? new State();
            }
            catch (JsonException)
            {
                File.Move(Path, Path + ".corrupt", true);
                return new State();
            }
        }
    }

    private void Save(State s)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        if (File.Exists(Path))
        {
            File.Replace(tmp, Path, null);
        }
        else
        {
            File.Move(tmp, Path);
        }
    }
}

/// <summary>
/// 계정 하나의 액세스 토큰 캐시. 만료 1분 전부터 refresh token으로 새로 받는다. 동시에 여러 요청이 와도 한 번만 새로 받는다.
/// </summary>
public sealed class GDriveTokenSource(GoogleOAuth oauth, string refreshToken, TimeProvider? time = null) : IAccessTokenSource, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private OAuthTokens? _current;

    /// <summary>처음 로그인 직후 받은 토큰을 넣어 첫 요청에서 다시 받지 않게 한다.</summary>
    public void Seed(OAuthTokens tokens) => _current = tokens;

    public async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        var cur = _current;
        if (!forceRefresh && cur is not null && cur.ExpiresAt - _time.GetUtcNow() > TimeSpan.FromMinutes(1))
        {
            return cur.AccessToken;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 기다리는 동안 다른 호출이 새로 받았으면 그것을 쓴다.
            if (_current is { } now && !ReferenceEquals(now, cur) && now.ExpiresAt - _time.GetUtcNow() > TimeSpan.FromMinutes(1))
            {
                return now.AccessToken;
            }

            var fresh = await oauth.RefreshAsync(refreshToken, ct).ConfigureAwait(false);
            _current = fresh;
            return fresh.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
