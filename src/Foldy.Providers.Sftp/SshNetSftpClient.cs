using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Foldy.Providers.Sftp;

/// <summary>SSH.NET <see cref="SftpClient"/>를 <see cref="ISftpClient"/>로 감싼다. 연결 끊김 계열 예외는 <see cref="SftpConnectionLostException"/>으로 바꾼다.</summary>
internal sealed class SshNetSftpClient : ISftpClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private readonly SftpConnectionRequest _request;
    private readonly HostKeyVerifier _verifier;
    private SftpClient? _client;

    public SshNetSftpClient(SftpConnectionRequest request, KnownHostsFile knownHosts)
    {
        _request = request;
        _verifier = new HostKeyVerifier(knownHosts, request.HostKeyHandler);
    }

    public bool IsConnected => _client?.IsConnected == true;

    public void Connect(CancellationToken ct)
    {
        _client?.Dispose();
        var loc = _request.Location;
        var user = loc.User ?? throw new InvalidOperationException("SFTP 사용자 이름이 필요합니다.");

        var client = new SftpClient(BuildConnectionInfo(loc, user, _request.Auth))
        {
            KeepAliveInterval = KeepAlive,
        };
        client.HostKeyReceived += (_, e) => e.CanTrust = _verifier.Verify(loc.Host, loc.Port, e.HostKey);
        _client = client;
        Guard(() => client.ConnectAsync(ct).GetAwaiter().GetResult());
    }

    public IEnumerable<SftpEntryInfo> ListDirectory(string path, CancellationToken ct)
    {
        var client = Connected();
        // 서버가 응답하는 대로 흘려 보내도록 비동기 열거를 큐 스레드에서 동기로 소비한다.
        var e = client.ListDirectoryAsync(path, ct).GetAsyncEnumerator(ct);
        try
        {
            while (Guard(() => e.MoveNextAsync().AsTask().GetAwaiter().GetResult()))
            {
                var f = e.Current;
                yield return new SftpEntryInfo(f.Name, f.IsDirectory, f.IsSymbolicLink, f.Length, f.LastWriteTimeUtc);
            }
        }
        finally
        {
            Guard(() => e.DisposeAsync().AsTask().GetAwaiter().GetResult());
        }
    }

    public Stream OpenRead(string path) => Guard(() => Connected().OpenRead(path));

    public void Download(string path, Stream destination, Action<long> progress, CancellationToken ct) =>
        Guard(() => Connected()
            .DownloadFileAsync(path, destination, new Reporter<DownloadFileProgressReport>(r => progress(unchecked((long)r.TotalBytesDownloaded))), ct)
            .GetAwaiter().GetResult());

    public void Upload(Stream source, string path, Action<long> progress, CancellationToken ct) =>
        Guard(() => Connected()
            .UploadFileAsync(source, path, true, new Reporter<UploadFileProgressReport>(r => progress(unchecked((long)r.TotalBytesUploaded))), ct)
            .GetAwaiter().GetResult());

    public SftpEntryInfo? Stat(string path)
    {
        try
        {
            var f = Guard(() => Connected().Get(path));
            return new SftpEntryInfo(f.Name, f.IsDirectory, f.IsSymbolicLink, f.Length, f.LastWriteTimeUtc);
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            return null;
        }
    }

    public long? FreeBytes(string path)
    {
        try
        {
            // statvfs@openssh.com: 사용자가 쓸 수 있는 블록 × 블록 크기. 값이 비면 모르는 것으로 본다.
            var st = Guard(() => Connected().GetStatus(path));
            if (st.TotalBlocks == 0 || st.BlockSize == 0)
            {
                return null;
            }

            var free = (decimal)st.AvailableBlocks * st.BlockSize;
            return free >= long.MaxValue ? long.MaxValue : (long)free;
        }
        catch (Exception ex) when (ex is SshException or NotSupportedException or InvalidOperationException)
        {
            return null; // 서버가 지원하지 않음: 막지 않는다
        }
    }

    public void CreateDirectory(string path) => Guard(() => Connected().CreateDirectory(path));

    public void DeleteFile(string path) => Guard(() => Connected().DeleteFile(path));

    public void DeleteDirectory(string path) => Guard(() => Connected().DeleteDirectory(path));

    public void Rename(string source, string target) => Guard(() => Connected().RenameFile(source, target));

    public void Replace(string source, string target) => Guard(() =>
    {
        var c = Connected();
        if (!_posixUnsupported)
        {
            try
            {
                c.RenameFile(source, target, isPosix: true);
                return;
            }
            catch (NotSupportedException)
            {
                _posixUnsupported = true; // posix-rename@openssh.com 미지원 서버
            }
        }

        // 임시 파일이 온전히 있을 때만 옛 파일을 지운다 (이 경로에서만 짧게 대상이 없는 순간이 생긴다).
        if (!c.Exists(source))
        {
            throw new FileNotFoundException("임시 파일이 없습니다: " + source);
        }

        if (c.Exists(target))
        {
            c.DeleteFile(target);
        }

        c.RenameFile(source, target);
    });

    private bool _posixUnsupported;

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }

    private SftpClient Connected() =>
        _client is { IsConnected: true } c ? c : throw new SftpConnectionLostException("SFTP 연결이 끊어졌습니다.");

    private static ConnectionInfo BuildConnectionInfo(SftpLocation loc, string user, SftpAuth auth)
    {
        var methods = new List<AuthenticationMethod>();
        switch (auth.Kind)
        {
            case SftpAuthKind.Password:
                var pw = auth.Password ?? "";
                methods.Add(new PasswordAuthenticationMethod(user, pw));
                var ki = new KeyboardInteractiveAuthenticationMethod(user);
                ki.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var p in e.Prompts)
                    {
                        p.Response = pw;
                    }
                };
                methods.Add(ki);
                break;
            case SftpAuthKind.KeyFile:
                var keyPath = auth.KeyFilePath ?? throw new InvalidOperationException("키 파일 경로가 필요합니다.");
                var key = string.IsNullOrEmpty(auth.Passphrase) ? new PrivateKeyFile(keyPath) : new PrivateKeyFile(keyPath, auth.Passphrase);
                methods.Add(new PrivateKeyAuthenticationMethod(user, key));
                break;
            default:
                throw new NotSupportedException($"지원하지 않는 인증 방식: {auth.Kind}");
        }

        return new ConnectionInfo(loc.Host, loc.Port, user, [.. methods]) { Timeout = ConnectTimeout };
    }

    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            throw new SftpConnectionLostException(ex.Message, ex);
        }
    }

    private static void Guard(Action action) => Guard(() => { action(); return 0; });

    internal static bool IsConnectionFailure(Exception ex) =>
        ex is SshConnectionException or SocketException or SshOperationTimeoutException or ObjectDisposedException;

    private sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>실제 SSH.NET 클라이언트를 만드는 기본 팩터리. 호스트 키는 known_hosts로 검증한다.</summary>
public sealed class SshNetSftpClientFactory(KnownHostsFile knownHosts) : ISftpClientFactory
{
    public ISftpClient Create(SftpConnectionRequest request) => new SshNetSftpClient(request, knownHosts);
}
