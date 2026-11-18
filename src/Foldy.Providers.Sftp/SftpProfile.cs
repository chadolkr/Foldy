using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foldy.Providers.Sftp;

/// <summary>비밀번호·패스프레이즈를 현재 Windows 사용자 DPAPI로 암호화한다. 다른 사용자/PC에서는 복호화되지 않는다.</summary>
public static class SecretProtector
{
    // 같은 사용자의 다른 앱이 blob을 그대로 복호화하지 못하게 하는 앱 고유 엔트로피(비밀은 아님).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Foldy.Sftp.Profile.v1");

    public static string Protect(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        var data = Dpapi.Protect(Encoding.UTF8.GetBytes(plain), Entropy);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string protectedBase64)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedBase64);
        var data = Dpapi.Unprotect(Convert.FromBase64String(protectedBase64), Entropy);
        return Encoding.UTF8.GetString(data);
    }
}

/// <summary>저장되는 SFTP 연결 프로필. 비밀은 DPAPI로 암호화된 base64 문자열로만 들어 있다.</summary>
public sealed record SftpProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = SftpLocation.DefaultPort;
    public string User { get; init; } = "";
    public SftpAuthKind Auth { get; init; } = SftpAuthKind.Password;
    public string? KeyFilePath { get; init; }

    /// <summary>접속 후 처음 열 원격 폴더. 비면 "/".</summary>
    public string? InitialPath { get; init; }

    public string? ProtectedPassword { get; init; }
    public string? ProtectedPassphrase { get; init; }

    public SftpProfile WithPassword(string? password) =>
        this with { ProtectedPassword = string.IsNullOrEmpty(password) ? null : SecretProtector.Protect(password) };

    public SftpProfile WithPassphrase(string? passphrase) =>
        this with { ProtectedPassphrase = string.IsNullOrEmpty(passphrase) ? null : SecretProtector.Protect(passphrase) };

    public SftpLocation ToLocation() => new(User, Host, Port, SftpLocation.NormalizePath(InitialPath ?? "/"));

    /// <summary>메모리 안의 평문 자격 증명으로 복원한다(복호화 실패 시 <see cref="CryptographicException"/>).</summary>
    public SftpAuth ToAuth() => new(
        Auth,
        ProtectedPassword is null ? null : SecretProtector.Unprotect(ProtectedPassword),
        KeyFilePath,
        ProtectedPassphrase is null ? null : SecretProtector.Unprotect(ProtectedPassphrase));
}

/// <summary>
/// <c>%LOCALAPPDATA%\Foldy\connections.json</c> 저장소. 저장은 임시 파일에 쓴 뒤 교체해 중간에 끊겨도 기존 파일이 남는다.
/// 파일이 손상돼 있으면 <c>.corrupt</c>로 옮기고 빈 목록으로 시작한다.
/// </summary>
public sealed class ConnectionProfileStore(string path)
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _gate = new();

    public string Path { get; } = path;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy", "connections.json");

    public IReadOnlyList<SftpProfile> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
            {
                return [];
            }

            try
            {
                using var fs = File.OpenRead(Path);
                var doc = JsonSerializer.Deserialize<ProfileFile>(fs, Json);
                return doc?.Profiles ?? [];
            }
            catch (JsonException)
            {
                File.Move(Path, Path + ".corrupt", true);
                return [];
            }
        }
    }

    public void Save(IEnumerable<SftpProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        lock (_gate)
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = Path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, new ProfileFile { Version = CurrentVersion, Profiles = [.. profiles] }, Json);
                fs.Flush(true);
            }

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

    /// <summary>같은 Id가 있으면 교체하고 없으면 추가한다.</summary>
    public void Upsert(SftpProfile profile)
    {
        lock (_gate)
        {
            var list = Load().ToList();
            var i = list.FindIndex(p => p.Id == profile.Id);
            if (i >= 0)
            {
                list[i] = profile;
            }
            else
            {
                list.Add(profile);
            }

            Save(list);
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            Save(Load().Where(p => p.Id != id));
        }
    }

    private sealed class ProfileFile
    {
        public int Version { get; set; }
        public List<SftpProfile> Profiles { get; set; } = [];
    }
}
