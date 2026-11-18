using System.Diagnostics;
using System.IO;
using Foldy.Indexer.Client;
using Serilog;

namespace Foldy.App.Services;

/// <summary>ShellWindow가 쓰는 업데이트 표면. <see cref="ServiceUpdater"/>(UAC 없음)와 <see cref="AppUpdater"/>(UAC) 공통.</summary>
public interface IUpdateSource : IDisposable
{
    /// <summary>설치 준비가 끝난 버전 (없으면 null).</summary>
    string? ReadyVersion { get; }

    string? LastError { get; }

    /// <summary>상태가 바뀌면 (아무 스레드에서) 호출된다.</summary>
    event Action? Changed;

    void Start(TimeSpan firstDelay);

    Task<bool> CheckAsync(CancellationToken ct);

    /// <summary>설치를 시작(또는 예약)한다. 참이면 호출자는 바로 앱을 닫는다.</summary>
    bool LaunchInstaller();
}

/// <summary>기존 <see cref="AppUpdater"/>(관리자 권한 설치 스크립트)를 <see cref="IUpdateSource"/>로 감싼다.</summary>
public sealed class LegacyUpdateSource(AppUpdater inner) : IUpdateSource
{
    public string? ReadyVersion => inner.ReadyVersion;

    public string? LastError => inner.LastError;

    public event Action? Changed
    {
        add => inner.Changed += value;
        remove => inner.Changed -= value;
    }

    public void Start(TimeSpan firstDelay) => inner.Start(firstDelay);

    public Task<bool> CheckAsync(CancellationToken ct) => inner.CheckAsync(ct);

    public bool LaunchInstaller() => inner.LaunchInstaller();

    public void Dispose() => inner.Dispose();
}

/// <summary>
/// 색인 서비스(SYSTEM)가 맡는 자동 업데이트 (설계서 10.5). 확인·다운로드·검증·설치는 모두 서비스가 하고,
/// 앱은 상태를 보여 주고 설치 시점만 알린다 — UAC 창이 뜨지 않는다.
/// <list type="bullet">
/// <item>[재시작하고 업데이트]: InstallWhenIdle(다시 띄우기) 후 앱을 닫는다. 서비스는 Foldy가 끝나면 MSI를 설치하고 같은 세션에 다시 띄운다.</item>
/// <item>앱 종료: 준비된 업데이트가 있고 진행 중인 작업이 없으면 InstallWhenIdle(다시 띄우지 않음).</item>
/// <item>창이 뜨면 AppStarted로 정상 시작을 알린다 (두 번 연속 실패하면 서비스가 직전 버전으로 되돌린다).</item>
/// </list>
/// 검색 연결과 따로 연결한다: 업데이트를 모르는 이전 서비스는 모르는 메시지에 연결을 끊기 때문이다.
/// </summary>
public sealed class ServiceUpdater : IUpdateSource
{
    private static readonly TimeSpan PollPeriod = TimeSpan.FromMinutes(1);
    private readonly string _pipeName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IndexerClient? _client;
    private Timer? _timer;
    private UpdateStatusReply? _status;
    private bool _disposed;

    private ServiceUpdater(string pipeName, IndexerClient client, UpdateStatusReply status)
    {
        _pipeName = pipeName;
        _client = client;
        _status = status;
    }

    public event Action? Changed;

    public UpdateStatusReply? Status => _status;

    public string? ReadyVersion => _status is { State: UpdateState.ReadyToInstall or UpdateState.Installing, AvailableVersion.Length: > 0 } s
        ? s.AvailableVersion
        : null;

    public string? LastError { get; private set; }

    /// <summary>서비스가 연결돼 있고 업데이트 메시지를 지원하면 만든다. 아니면 null (호출자는 AppUpdater로 폴백).</summary>
    public static async Task<ServiceUpdater?> TryCreateAsync(
        string pipeName = Protocol.DefaultPipeName,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var client = await IndexerClient.TryConnectAsync(pipeName, timeout ?? TimeSpan.FromMilliseconds(500), ct);
        if (client is null)
        {
            return null;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(2));
            var status = await client.UpdateStatusAsync(cts.Token);
            return new ServiceUpdater(pipeName, client, status);
        }
        catch (Exception ex) when (ex is ProtocolException or IOException or OperationCanceledException)
        {
            // 이전 서비스(메시지 모름) 또는 업데이트가 꺼진 서비스(콘솔 실행 등)
            Log.Information("서비스 업데이트를 쓸 수 없어 기존 방식으로: {Message}", ex.Message);
            await client.DisposeAsync();
            return null;
        }
    }

    /// <summary>서비스가 스스로 확인하므로(시작 1분 뒤 + 6시간마다) 앱은 상태만 주기적으로 읽는다.</summary>
    public void Start(TimeSpan firstDelay) =>
        _timer = new Timer(_ => _ = RefreshAsync(CancellationToken.None), null, firstDelay < PollPeriod ? firstDelay : PollPeriod, PollPeriod);

    public Task<UpdateStatusReply?> RefreshAsync(CancellationToken ct) => CallAsync(c => c.UpdateStatusAsync(ct), ct);

    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        if (await CallAsync(c => c.CheckForUpdateAsync(ct), ct) is null)
        {
            return false;
        }

        // 확인이 시작되고 끝날 때까지 상태를 읽는다 (다운로드 포함, 최대 10분)
        var sw = Stopwatch.StartNew();
        var started = false;
        while (sw.Elapsed < TimeSpan.FromMinutes(10))
        {
            await Task.Delay(500, ct);
            var s = await RefreshAsync(ct);
            if (s is null)
            {
                return false;
            }

            var busy = s.State is UpdateState.Checking or UpdateState.Downloading;
            started |= busy;
            if (!busy && (started || sw.Elapsed > TimeSpan.FromSeconds(3)))
            {
                break;
            }
        }

        return ReadyVersion is not null;
    }

    /// <summary>[재시작하고 업데이트]: 서비스에 설치를 맡기고 끝나면 다시 띄우게 한다. 참이면 호출자는 앱을 닫는다.</summary>
    public bool LaunchInstaller() => RequestInstall(relaunch: true);

    /// <summary>앱 종료 시 설치 (다시 띄우지 않음). 진행 중인 작업이 없을 때만 부른다.</summary>
    public bool InstallAtExit() => ReadyVersion is not null && RequestInstall(relaunch: false);

    /// <summary>창이 뜬 뒤 정상 시작을 알린다.</summary>
    public Task ReportStartedAsync(string version, CancellationToken ct = default) => CallAsync(c => c.ReportAppStartedAsync(version, ct), ct);

    private bool RequestInstall(bool relaunch)
    {
        uint session;
        using (var me = Process.GetCurrentProcess())
        {
            session = (uint)me.SessionId;
        }

        // UI 스레드에서 불리므로 스레드 풀에서 짧게 기다린다 (서비스는 바로 답한다)
        var task = Task.Run(() => CallAsync(c => c.InstallUpdateWhenIdleAsync(relaunch, session), CancellationToken.None));
        if (!task.Wait(TimeSpan.FromSeconds(3)))
        {
            LastError = "색인 서비스가 답하지 않습니다";
            return false;
        }

        var s = task.Result;
        return s is { State: UpdateState.Installing } or { InstallPending: true };
    }

    private async Task<UpdateStatusReply?> CallAsync(Func<IndexerClient, Task<UpdateStatusReply>> call, CancellationToken ct)
    {
        if (_disposed || !await _gate.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            return null;
        }

        try
        {
            if (_client is null || _client.IsFaulted)
            {
                // 서비스가 다시 시작했으면 (MSI 업그레이드 등) 새로 연결
                if (_client is not null)
                {
                    await _client.DisposeAsync();
                }

                _client = await IndexerClient.TryConnectAsync(_pipeName, TimeSpan.FromMilliseconds(500), ct);
                if (_client is null)
                {
                    LastError = "색인 서비스에 연결할 수 없습니다";
                    return null;
                }
            }

            var s = await call(_client);
            LastError = s.LastError.Length > 0 ? s.LastError : null;
            var changed = s != _status;
            _status = s;
            if (changed)
            {
                Changed?.Invoke();
            }

            return s;
        }
        catch (Exception ex) when (ex is ProtocolException or IOException or OperationCanceledException)
        {
            LastError = ex.Message;
            Log.Information("서비스 업데이트 요청 실패: {Message}", ex.Message);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _client?.DisposeAsync().AsTask().Wait(200);
        _gate.Dispose();
    }
}
