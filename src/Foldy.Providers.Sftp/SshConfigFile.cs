namespace Foldy.Providers.Sftp;

/// <summary>~/.ssh/config 의 Host 항목 하나 (외부 ssh와 같은 이름으로 접속할 수 있게).</summary>
public sealed record SshConfigHost(string Alias, string HostName, string? User, int Port, string? IdentityFile);

/// <summary>
/// OpenSSH 설정 파일(~/.ssh/config)의 단순 해석: Host(와일드카드 제외), HostName, User, Port, IdentityFile.
/// Match·Include·ProxyJump 등은 무시한다 (외부 ssh 터미널은 원래 설정을 그대로 쓴다).
/// </summary>
public static class SshConfigFile
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    /// <summary>기본 개인 키 후보 (OpenSSH 기본 순서).</summary>
    public static IEnumerable<string> DefaultIdentityFiles()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p))
            {
                yield return p;
            }
        }
    }

    public static IReadOnlyList<SshConfigHost> Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllLines(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<SshConfigHost> Parse(IEnumerable<string> lines)
    {
        var result = new List<SshConfigHost>();
        List<string>? aliases = null;
        string? hostName = null, user = null, identity = null;
        var port = SftpLocation.DefaultPort;

        void Flush()
        {
            if (aliases is null)
            {
                return;
            }

            foreach (var a in aliases.Where(a => a.IndexOfAny(['*', '?', '!']) < 0))
            {
                result.Add(new SshConfigHost(a, hostName ?? a, user, port, identity is null ? null : ExpandHome(identity)));
            }
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var sep = line.IndexOfAny([' ', '\t', '=']);
            if (sep < 0)
            {
                continue;
            }

            var key = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim().TrimStart('=').Trim().Trim('"');
            switch (key.ToLowerInvariant())
            {
                case "host":
                    Flush();
                    aliases = [.. value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)];
                    hostName = user = identity = null;
                    port = SftpLocation.DefaultPort;
                    break;
                case "match":
                    Flush();
                    aliases = null;
                    break;
                case "hostname" when aliases is not null:
                    hostName = value;
                    break;
                case "user" when aliases is not null:
                    user = value;
                    break;
                case "port" when aliases is not null && int.TryParse(value, out var p) && p is > 0 and < 65536:
                    port = p;
                    break;
                case "identityfile" when aliases is not null && identity is null:
                    identity = value;
                    break;
            }
        }

        Flush();
        return result;
    }

    private static string ExpandHome(string p) =>
        (p.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[1..].TrimStart('/', '\\')) : p).Replace('/', '\\');
}
