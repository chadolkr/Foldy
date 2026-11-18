using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

// known_hosts 해시 형식(|1|)이 HMAC-SHA1로 고정이라 외부 ssh와 호환하려면 SHA1이 필요하다. 보안 경계가 아닌 호스트 이름 난독화 용도.
#pragma warning disable CA5350

namespace Foldy.Providers.Sftp;

/// <summary>
/// OpenSSH known_hosts 파일 읽기/쓰기. 외부 ssh.exe와 같은 파일을 공유해 호스트 키 판단을 일치시킨다 (설계서 SFTP/SSH 절).
/// 평문 항목(호스트 목록, [host]:port, 와일드카드 *?, !부정)과 해시 항목(|1|salt|hash)을 모두 읽는다.
/// 키 종류는 ssh-ed25519, ecdsa-sha2-nistp256/384/521, ssh-rsa 등 키 blob 안의 이름을 그대로 사용한다.
/// 스레드 안전: 쓰기는 내부 잠금으로 직렬화한다. 다른 프로세스(ssh)와의 동시 쓰기는 append이므로 안전하다.
/// </summary>
public sealed class KnownHostsFile(string path)
{
    private readonly object _gate = new();

    public string Path { get; } = path;

    /// <summary>%USERPROFILE%\.ssh\known_hosts</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    /// <summary>호스트 키를 판정한다. 같은 호스트에 같은 종류의 다른 키가 있으면 Changed, 아무 항목도 없으면 Unknown.</summary>
    public HostKeyStatus Check(string host, int port, byte[] keyBlob)
    {
        var name = HostName(host, port);
        var type = KeyTypeOf(keyBlob);
        var b64 = Convert.ToBase64String(keyBlob);
        var sawDifferent = false;

        foreach (var e in ReadEntries())
        {
            if (e.Marker == KnownHostMarker.CertAuthority || !e.Matches(name))
            {
                continue;
            }

            var sameKey = string.Equals(e.KeyBase64, b64, StringComparison.Ordinal);
            if (e.Marker == KnownHostMarker.Revoked)
            {
                if (sameKey)
                {
                    return HostKeyStatus.Changed; // 폐기된 키는 절대 신뢰하지 않는다.
                }

                continue;
            }

            if (!string.Equals(e.KeyType, type, StringComparison.Ordinal))
            {
                continue;
            }

            if (sameKey)
            {
                return HostKeyStatus.Match;
            }

            sawDifferent = true;
        }

        return sawDifferent ? HostKeyStatus.Changed : HostKeyStatus.Unknown;
    }

    /// <summary>항목을 끝에 덧붙인다(디렉터리가 없으면 만든다). 기존 줄은 건드리지 않는다.</summary>
    public void Add(string host, int port, byte[] keyBlob, bool hashed = false)
    {
        var name = HostName(host, port);
        var field = hashed ? Hash(name) : name;
        var line = $"{field} {KeyTypeOf(keyBlob)} {Convert.ToBase64String(keyBlob)}";

        lock (_gate)
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var needNewline = false;
            if (File.Exists(Path))
            {
                using var read = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (read.Length > 0)
                {
                    read.Seek(-1, SeekOrigin.End);
                    needNewline = read.ReadByte() != '\n';
                }
            }

            using var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = Encoding.UTF8.GetBytes((needNewline ? "\n" : "") + line + "\n");
            fs.Write(bytes);
        }
    }

    /// <summary>해당 호스트의 항목(keyType 지정 시 그 종류만)을 지운다(키가 바뀌어 사용자가 새 키를 신뢰하기로 했을 때). 해시 항목도 비교해 지운다.</summary>
    public int Remove(string host, int port, string? keyType = null)
    {
        var name = HostName(host, port);
        lock (_gate)
        {
            if (!File.Exists(Path))
            {
                return 0;
            }

            var lines = File.ReadAllLines(Path);
            var kept = new List<string>(lines.Length);
            var removed = 0;
            foreach (var l in lines)
            {
                if (ParseLine(l) is { } e && e.Marker == KnownHostMarker.None && e.Matches(name)
                    && (keyType is null || e.KeyType == keyType))
                {
                    removed++;
                    continue;
                }

                kept.Add(l);
            }

            if (removed > 0)
            {
                var tmp = Path + ".foldy-tmp";
                File.WriteAllLines(tmp, kept);
                File.Replace(tmp, Path, null);
            }

            return removed;
        }
    }

    /// <summary>known_hosts의 호스트 표기: 22번이면 host, 아니면 [host]:port (소문자).</summary>
    public static string HostName(string host, int port) =>
        port == SftpLocation.DefaultPort ? host.ToLowerInvariant() : $"[{host.ToLowerInvariant()}]:{port}";

    /// <summary>SSH wire 형식 키 blob의 맨 앞 문자열(키 종류)을 읽는다.</summary>
    public static string KeyTypeOf(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < 4)
        {
            throw new FormatException("호스트 키 blob이 너무 짧습니다.");
        }

        var len = BinaryPrimitives.ReadUInt32BigEndian(blob);
        if (len == 0 || len > blob.Length - 4)
        {
            throw new FormatException("호스트 키 blob 형식이 올바르지 않습니다.");
        }

        return Encoding.ASCII.GetString(blob, 4, (int)len);
    }

    /// <summary>ssh가 보여 주는 형태의 지문: SHA256:base64(패딩 없음).</summary>
    public static string FingerprintSha256(byte[] blob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    internal static string Hash(string hostName)
    {
        var salt = RandomNumberGenerator.GetBytes(20);
        return HashWithSalt(hostName, salt);
    }

    internal static string HashWithSalt(string hostName, byte[] salt)
    {
        var mac = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(hostName));
        return $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(mac)}";
    }

    internal IEnumerable<KnownHostEntry> ReadEntries()
    {
        string[] lines;
        lock (_gate)
        {
            if (!File.Exists(Path))
            {
                return [];
            }

            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            lines = sr.ReadToEnd().Split('\n');
        }

        return lines.Select(ParseLine).OfType<KnownHostEntry>().ToList();
    }

    internal static KnownHostEntry? ParseLine(string line)
    {
        var s = line.Trim();
        if (s.Length == 0 || s[0] == '#')
        {
            return null;
        }

        var parts = s.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var i = 0;
        var marker = KnownHostMarker.None;
        if (parts[0].StartsWith('@'))
        {
            marker = parts[0] switch
            {
                "@revoked" => KnownHostMarker.Revoked,
                "@cert-authority" => KnownHostMarker.CertAuthority,
                _ => KnownHostMarker.CertAuthority, // 모르는 마커는 무시(사용하지 않음)
            };
            i = 1;
        }

        if (parts.Length < i + 3)
        {
            return null;
        }

        return new KnownHostEntry(marker, parts[i], parts[i + 1], parts[i + 2]);
    }
}

internal enum KnownHostMarker { None, Revoked, CertAuthority }

internal sealed record KnownHostEntry(KnownHostMarker Marker, string HostField, string KeyType, string KeyBase64)
{
    public bool Matches(string hostName)
    {
        if (HostField.StartsWith("|1|", StringComparison.Ordinal))
        {
            return MatchesHashed(hostName);
        }

        var positive = false;
        foreach (var raw in HostField.Split(','))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            var negated = raw[0] == '!';
            var pattern = negated ? raw[1..] : raw;
            if (!Glob(pattern, hostName))
            {
                continue;
            }

            if (negated)
            {
                return false;
            }

            positive = true;
        }

        return positive;
    }

    private bool MatchesHashed(string hostName)
    {
        var f = HostField.Split('|'); // "", "1", salt, hash
        if (f.Length != 4)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(f[2]);
            var expected = Convert.FromBase64String(f[3]);
            var actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(hostName));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>* ? 와일드카드, 대소문자 무시.</summary>
    private static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])) && pattern[p] != '*')
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
