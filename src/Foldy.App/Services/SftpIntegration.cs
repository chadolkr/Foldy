using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using Foldy.Core.FileSystem;
using Foldy.Presentation.Navigation;
using Foldy.Providers.Sftp;

namespace Foldy.App.Services;

/// <summary>경로를 보고 Provider를 고른다 (설계서 5.3 ProviderResolver): sftp:// → SftpProvider, 그 외 → 로컬.</summary>
public sealed class ProviderRouter(IFileSystemProvider local, IFileSystemProvider sftp) : IFileSystemProvider
{
    public ProviderId Id => ProviderId.Local;

    public IAsyncEnumerable<IReadOnlyList<FsEntry>> ListBatchedAsync(FsPath path, CancellationToken ct) =>
        PathUtil.IsSftp(path.Value) ? sftp.ListBatchedAsync(path with { Provider = ProviderId.Sftp }, ct) : local.ListBatchedAsync(path, ct);
}

/// <summary>SFTP 연결 관리: 저장된 프로필 + 이번 실행 동안만 기억하는 자격 증명, 호스트 키 확인 창.</summary>
public sealed class SftpSessions
{
    private readonly ConcurrentDictionary<string, SftpAuth> _session = new(StringComparer.OrdinalIgnoreCase);

    public SftpSessions()
    {
        Profiles = new ConnectionProfileStore(ConnectionProfileStore.DefaultPath);
        var knownHosts = new KnownHostsFile(KnownHostsFile.DefaultPath);
        Provider = new SftpProvider(new SshNetSftpClientFactory(knownHosts), FindCredentials, AskHostKey);
    }

    public ConnectionProfileStore Profiles { get; }

    public SftpProvider Provider { get; }

    /// <summary>연결 창에서 입력한 자격 증명을 이번 실행 동안 기억한다 (저장을 고르면 프로필에 DPAPI로 저장).</summary>
    public void Remember(SftpLocation loc, SftpAuth auth) => _session[loc.ConnectionKey] = auth;

    private SftpAuth? FindCredentials(SftpLocation loc)
    {
        if (_session.TryGetValue(loc.ConnectionKey, out var a))
        {
            return a;
        }

        var p = Profiles.Load().FirstOrDefault(p =>
            string.Equals(p.Host, loc.Host, StringComparison.OrdinalIgnoreCase) && p.Port == loc.Port &&
            string.Equals(p.User, loc.User, StringComparison.Ordinal));
        try
        {
            return p?.ToAuth();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>처음 보거나 바뀐 호스트 키: 지문을 보여주고 사용자가 고른다 (UI 스레드에서 묻는다).</summary>
    private static HostKeyDecision AskHostKey(HostKeyCheck c)
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            var changed = c.Status == HostKeyStatus.Changed;
            var msg = changed
                ? $"⚠ {c.Host}:{c.Port} 서버의 호스트 키가 저장된 것과 다릅니다.\n중간자 공격일 수 있습니다.\n\n{c.KeyType}\n{c.FingerprintSha256}\n\n새 키를 믿고 저장할까요?"
                : $"{c.Host}:{c.Port} 서버에 처음 연결합니다.\n\n{c.KeyType}\n{c.FingerprintSha256}\n\n이 키를 known_hosts에 저장하고 연결할까요?\n(아니요: 이번만 연결)";
            var r = MessageBox.Show(Application.Current.MainWindow, msg, "Foldy · 호스트 키 확인",
                changed ? MessageBoxButton.OKCancel : MessageBoxButton.YesNoCancel,
                changed ? MessageBoxImage.Warning : MessageBoxImage.Question);
            return r switch
            {
                MessageBoxResult.Yes or MessageBoxResult.OK => HostKeyDecision.AcceptAndSave,
                MessageBoxResult.No => HostKeyDecision.AcceptOnce,
                _ => HostKeyDecision.Reject,
            };
        });
    }

    /// <summary>같은 서버·계정·원격 경로로 SSH 터미널 (키 인증이면 같은 키 파일을 넘긴다).</summary>
    public void OpenTerminal(string sftpPath, bool sudo)
    {
        var loc = SftpLocation.Parse(sftpPath);
        var key = FindCredentials(loc) is { Kind: SftpAuthKind.KeyFile, KeyFilePath: { } k } ? k : null;
        var launch = SshTerminalLauncher.Build(new SshTerminalRequest(loc.Host, loc.Port, loc.User ?? Environment.UserName, loc.Path, key, sudo), SshTerminalLauncher.FindWindowsTerminal());
        using var _ = Process.Start(new ProcessStartInfo(launch.FileName, launch.Arguments) { UseShellExecute = launch.UseShellExecute });
    }
}
