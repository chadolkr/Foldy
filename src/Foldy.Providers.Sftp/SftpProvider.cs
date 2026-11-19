using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Foldy.Core.FileSystem;
using Foldy.Core.Threading;

namespace Foldy.Providers.Sftp;

/// <summary>
/// SFTP Provider (설계서 5.3). 경로 문자열은 <c>sftp://user@host:port/path</c>.
/// <list type="bullet">
/// <item>연결(user@host:port)마다 세션 1개와 전용 <see cref="DriveWorkQueue"/> 1개. 한 서버가 멈춰도 다른 연결·UI는 영향이 없다.</item>
/// <item>연결/재연결(호스트 키 확인 UI 대기 포함)은 큐 밖에서 하고, 큐 스레드는 목록·열기 같은 짧은 호출만 실행한다(워치독 오탐 방지).</item>
/// <item>목록은 <see cref="BatchSize"/>개 또는 <see cref="BatchInterval"/> 단위로 스트리밍한다. 연결이 끊기면 아직 아무것도 내보내지 않았을 때 한 번 재연결 후 재시도한다.</item>
/// <item>대용량 전송(다운로드/업로드)은 큐를 점유하지 않고 별도 작업으로 돌려 진행률을 보고한다.</item>
/// </list>
/// 스레드 소유권: 공개 멤버는 아무 스레드에서나 호출 가능.
/// </summary>
public sealed class SftpProvider : IFileSystemProvider, IAsyncDisposable
{
    public const int BatchSize = 200;
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>느린 서버의 큰 폴더 열거를 허용하기 위해 로컬(5초)보다 넉넉하게 둔다.</summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(30);

    private readonly ISftpClientFactory _factory;
    private readonly Func<SftpLocation, SftpAuth?> _credentials;
    private readonly Func<HostKeyCheck, HostKeyDecision>? _hostKeyHandler;
    private readonly TimeSpan _stallTimeout;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <param name="credentials">위치에 맞는 자격 증명을 돌려준다(프로필 저장소 조회 또는 UI 프롬프트). null이면 연결 실패.</param>
    /// <param name="hostKeyHandler">알 수 없거나 바뀐 호스트 키를 UI가 판단한다. null이면 거부.</param>
    public SftpProvider(
        ISftpClientFactory factory,
        Func<SftpLocation, SftpAuth?> credentials,
        Func<HostKeyCheck, HostKeyDecision>? hostKeyHandler = null,
        TimeSpan? stallTimeout = null)
    {
        _factory = factory;
        _credentials = credentials;
        _hostKeyHandler = hostKeyHandler;
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
    }

    public ProviderId Id => ProviderId.Sftp;

    /// <summary>연결을 미리 맺는다(연결 관리자의 "연결" 버튼, 호스트 키 확인 흐름 시작).</summary>
    public Task ConnectAsync(SftpLocation location, CancellationToken ct) =>
        GetSession(location).EnsureConnectedAsync(location, ct);

    public async IAsyncEnumerable<IReadOnlyList<FsEntry>> ListBatchedAsync(
        FsPath path, [EnumeratorCancellation] CancellationToken ct)
    {
        var loc = ParsePath(path);
        for (var attempt = 0; ; attempt++)
        {
            var session = GetSession(loc);
            var client = await session.EnsureConnectedAsync(loc, ct).ConfigureAwait(false);

            var channel = Channel.CreateUnbounded<IReadOnlyList<FsEntry>>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var work = session.Queue.Enqueue(c => Enumerate(client, loc, channel.Writer, c), WorkPriority.ActiveTab, ct);
            // 채널은 항상 정상 종료하고, 실패는 work 작업에서 따로 받는다(yield 사이에서 예외를 잡아 재시도하기 위함).
            _ = work.ContinueWith(
                static (_, s) => ((ChannelWriter<IReadOnlyList<FsEntry>>)s!).TryComplete(),
                channel.Writer,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            var emitted = false;
            var reader = channel.Reader;
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var batch))
                {
                    emitted = true;
                    yield return batch;
                }
            }

            Exception? failure = null;
            try
            {
                await work.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure is null)
            {
                yield break;
            }

            if (attempt == 0 && !emitted && IsConnectionLoss(failure))
            {
                session.Invalidate(client); // 끊긴 연결 버리고 한 번 재연결
                continue;
            }

            throw failure;
        }
    }

    /// <summary>원격 파일을 읽기 스트림으로 연다. 스트림 읽기는 호출자 스레드에서 일어난다(큐는 열기만 담당).</summary>
    public Task<Stream> OpenReadAsync(FsPath path, CancellationToken ct)
    {
        var loc = ParsePath(path);
        return RunOnQueueAsync(loc, (client, _) => client.OpenRead(loc.Path), ct);
    }

    /// <summary>원격 파일을 대상 스트림으로 내려받는다. progress는 누적 바이트.</summary>
    public async Task DownloadAsync(FsPath source, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        var loc = ParsePath(source);
        var client = await GetSession(loc).EnsureConnectedAsync(loc, ct).ConfigureAwait(false);
        await Task.Run(() => client.Download(loc.Path, destination, n => progress?.Report(n), ct), ct).ConfigureAwait(false);
    }

    /// <summary>스트림을 원격 경로에 업로드한다(덮어쓰기). progress는 누적 바이트.</summary>
    public async Task UploadAsync(Stream source, FsPath destination, IProgress<long>? progress, CancellationToken ct)
    {
        var loc = ParsePath(destination);
        var client = await GetSession(loc).EnsureConnectedAsync(loc, ct).ConfigureAwait(false);
        await Task.Run(() => client.Upload(source, loc.Path, n => progress?.Report(n), ct), ct).ConfigureAwait(false);
    }

    /// <summary>경로의 정보(없으면 null). 큐에서 실행한다.</summary>
    public Task<SftpEntryInfo?> StatAsync(FsPath path, CancellationToken ct)
    {
        var loc = ParsePath(path);
        return RunOnQueueAsync(loc, (client, _) => client.Stat(loc.Path), ct);
    }

    public async Task<bool> ExistsAsync(FsPath path, CancellationToken ct) => await StatAsync(path, ct).ConfigureAwait(false) is not null;

    /// <summary>폴더를 만든다(부모는 있어야 한다).</summary>
    public Task CreateDirectoryAsync(FsPath path, CancellationToken ct)
    {
        var loc = ParsePath(path);
        return RunOnQueueAsync(loc, (client, _) => { client.CreateDirectory(loc.Path); return 0; }, ct);
    }

    public Task DeleteFileAsync(FsPath path, CancellationToken ct)
    {
        var loc = ParsePath(path);
        return RunOnQueueAsync(loc, (client, _) => { client.DeleteFile(loc.Path); return 0; }, ct);
    }

    /// <summary>빈 폴더를 지운다.</summary>
    public Task DeleteDirectoryAsync(FsPath path, CancellationToken ct)
    {
        var loc = ParsePath(path);
        return RunOnQueueAsync(loc, (client, _) => { client.DeleteDirectory(loc.Path); return 0; }, ct);
    }

    /// <summary>같은 연결 안에서 이동·이름 바꾸기. 연결이 다르면 <see cref="ArgumentException"/>.</summary>
    public Task RenameAsync(FsPath from, FsPath to, CancellationToken ct)
    {
        var a = ParsePath(from);
        var b = ParsePath(to);
        if (a.ConnectionKey != b.ConnectionKey)
        {
            throw new ArgumentException("서로 다른 SFTP 연결 사이에서는 이름을 바꿀 수 없습니다.");
        }

        return RunOnQueueAsync(a, (client, _) => { client.Rename(a.Path, b.Path); return 0; }, ct);
    }

    /// <summary>임시 파일을 목적지로 바꿔 넣는다(목적지가 있으면 교체). 같은 연결이어야 한다.</summary>
    public Task ReplaceFileAsync(FsPath temp, FsPath target, CancellationToken ct)
    {
        var a = ParsePath(temp);
        var b = ParsePath(target);
        if (a.ConnectionKey != b.ConnectionKey)
        {
            throw new ArgumentException("서로 다른 SFTP 연결 사이에서는 바꿔 넣을 수 없습니다.");
        }

        return RunOnQueueAsync(a, (client, _) => { client.Replace(a.Path, b.Path); return 0; }, ct);
    }

    /// <summary>폴더 내용을 모두 모아 돌려준다("."/".." 제외).</summary>
    public async Task<IReadOnlyList<FsEntry>> ListAsync(FsPath path, CancellationToken ct)
    {
        var all = new List<FsEntry>();
        await foreach (var b in ListBatchedAsync(path, ct).ConfigureAwait(false))
        {
            all.AddRange(b);
        }

        return all;
    }

    /// <summary>연결을 닫는다. 다음 호출 때 다시 맺는다.</summary>
    public void Disconnect(SftpLocation location)
    {
        if (_sessions.TryGetValue(location.ConnectionKey, out var s))
        {
            s.Invalidate(null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values)
        {
            s.Dispose();
            await s.Queue.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }

    internal static bool IsConnectionLoss(Exception ex) =>
        ex is SftpConnectionLostException || SshNetSftpClient.IsConnectionFailure(ex);

    private static SftpLocation ParsePath(FsPath path)
    {
        if (path.Provider != ProviderId.Sftp)
        {
            throw new ArgumentException($"SFTP 경로가 아닙니다: {path}", nameof(path));
        }

        return SftpLocation.Parse(path.Value);
    }

    private async Task<T> RunOnQueueAsync<T>(SftpLocation loc, Func<ISftpClient, CancellationToken, T> work, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var session = GetSession(loc);
            var client = await session.EnsureConnectedAsync(loc, ct).ConfigureAwait(false);
            try
            {
                return await session.Queue.Enqueue(c => work(client, c), WorkPriority.ActiveTab, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 0 && IsConnectionLoss(ex))
            {
                session.Invalidate(client);
            }
        }
    }

    /// <summary>큐 스레드에서 실행된다.</summary>
    private static bool Enumerate(ISftpClient client, SftpLocation dir, ChannelWriter<IReadOnlyList<FsEntry>> writer, CancellationToken ct)
    {
        var batch = new List<FsEntry>(BatchSize);
        var sw = Stopwatch.StartNew();
        foreach (var info in client.ListDirectory(dir.Path, ct))
        {
            if (info.Name is "." or "..")
            {
                continue;
            }

            batch.Add(ToEntry(dir, info));
            if (batch.Count >= BatchSize || sw.Elapsed >= BatchInterval)
            {
                ct.ThrowIfCancellationRequested();
                writer.TryWrite(batch);
                batch = new List<FsEntry>(BatchSize);
                sw.Restart();
            }
        }

        if (batch.Count > 0)
        {
            writer.TryWrite(batch);
        }

        return true;
    }

    private static FsEntry ToEntry(SftpLocation dir, SftpEntryInfo info)
    {
        var kind = info.IsSymbolicLink ? FsKind.Link : info.IsDirectory ? FsKind.Folder : FsKind.File;
        var attrs = info.IsSymbolicLink ? FileAttributes.ReparsePoint
            : info.IsDirectory ? FileAttributes.Directory
            : FileAttributes.Normal;
        return new FsEntry(
            info.Name,
            new FsPath(ProviderId.Sftp, dir.Combine(info.Name).ToString()),
            kind,
            info.IsDirectory ? 0 : info.Size,
            null,
            info.ModifiedUtc,
            info.ModifiedUtc, // SFTP에는 생성 시각이 없다.
            attrs,
            null);
    }

    private Session GetSession(SftpLocation loc) =>
        _sessions.GetOrAdd(loc.ConnectionKey, _ => new Session(this, loc));

    /// <summary>연결 하나: 클라이언트 + 전용 큐. 연결 시도는 직렬화한다.</summary>
    private sealed class Session : IDisposable
    {
        private readonly SftpProvider _owner;
        private readonly SemaphoreSlim _connectGate = new(1, 1);
        private readonly object _lock = new();
        private ISftpClient? _client;

        public Session(SftpProvider owner, SftpLocation loc)
        {
            _owner = owner;
            // 큐 키는 DriveKey 규칙(\\서버\공유)에 맞춰 만든다. 경로 구분자가 섞이지 않게 치환.
            var safe = loc.ConnectionKey.Replace('\\', '_').Replace('/', '_');
            Queue = new DriveWorkQueue(DriveKey.FromPath($@"\\{safe}\SFTP"), owner._stallTimeout);
        }

        public DriveWorkQueue Queue { get; }

        public async Task<ISftpClient> EnsureConnectedAsync(SftpLocation loc, CancellationToken ct)
        {
            await _connectGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ISftpClient? existing;
                lock (_lock)
                {
                    existing = _client;
                }

                if (existing is { IsConnected: true })
                {
                    return existing;
                }

                Invalidate(null);
                var auth = _owner._credentials(loc)
                    ?? throw new InvalidOperationException($"SFTP 자격 증명이 없습니다: {loc.ConnectionKey}");
                var client = _owner._factory.Create(new SftpConnectionRequest(loc, auth, _owner._hostKeyHandler));
                try
                {
                    // 호스트 키 확인 UI를 기다릴 수 있으므로 큐 스레드가 아닌 별도 작업에서 연결한다.
                    await Task.Run(() => client.Connect(ct), ct).ConfigureAwait(false);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }

                lock (_lock)
                {
                    _client = client;
                }

                return client;
            }
            finally
            {
                _connectGate.Release();
            }
        }

        /// <summary>끊긴 클라이언트를 버린다. 이미 다른 클라이언트로 교체됐으면 건드리지 않는다.</summary>
        public void Invalidate(ISftpClient? client)
        {
            lock (_lock)
            {
                if (client is null || ReferenceEquals(_client, client))
                {
                    DisposeClient();
                }
            }
        }

        public void Dispose()
        {
            Invalidate(null);
            _connectGate.Dispose();
        }

        private void DisposeClient()
        {
            var c = _client;
            _client = null;
            c?.Dispose();
        }
    }
}
