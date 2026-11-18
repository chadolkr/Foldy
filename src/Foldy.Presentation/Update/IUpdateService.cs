namespace Foldy.Presentation.Update;

/// <summary>설정의 업데이트 방식 (설계서 10.5: 자동 설치(기본) / 내려받고 알림만 / 끔).</summary>
public enum UpdateMode
{
    Auto,
    NotifyOnly,
    Off,
}

/// <summary>업데이트 채널. 베타는 GitHub pre-release.</summary>
public enum UpdateChannel
{
    Stable,
    Beta,
}

/// <summary>foldy-index 서비스가 알려 주는 업데이트 단계.</summary>
public enum UpdatePhase
{
    /// <summary>아직 확인 전.</summary>
    Idle,
    Checking,
    UpToDate,
    Downloading,
    /// <summary>Ed25519 서명·SHA-256(·Authenticode)이 모두 맞아 설치 준비됨.</summary>
    ReadyToInstall,
    /// <summary>복사·이동 작업이 끝나거나 Foldy를 종료하면 설치.</summary>
    InstallScheduled,
    /// <summary>사용자가 건너뛴 버전.</summary>
    Skipped,
    /// <summary>설정에서 끔.</summary>
    Disabled,
}

public sealed record UpdateSettings(UpdateMode Mode, UpdateChannel Channel)
{
    public static UpdateSettings Default { get; } = new(UpdateMode.Auto, UpdateChannel.Stable);
}

/// <summary>
/// 서비스의 <c>UpdateStatus()</c> 응답. 네트워크·GitHub 장애는 여기 오류로 오지 않고
/// <see cref="LastCheck"/>가 갱신되지 않는 것으로만 드러난다 (설계서 10.5 실패 대비).
/// </summary>
public sealed record UpdateStatus(
    UpdatePhase Phase,
    string CurrentVersion,
    string? AvailableVersion = null,
    DateTimeOffset? LastCheck = null,
    double DownloadProgress = 0,
    bool Mandatory = false,
    string? ReleaseNotes = null);

/// <summary>
/// 업데이트 서비스 창구. 실제 구현은 foldy-index 파이프 프로토콜의 네 명령
/// (<c>UpdateStatus()</c>, <c>CheckNow()</c>, <c>InstallWhenIdle()</c>, <c>SkipVersion(v)</c>)과 설정 저장을 감싼다.
/// 확인·다운로드·검증·설치는 모두 서비스(Rust foldy-update)가 하므로 UI 쪽에는 HTTP 코드가 없다.
/// 나중에 UI에서 직접 HTTP를 쓰게 되면 HttpClient 기본 처리기(시스템 프록시)에
/// <c>DefaultProxyCredentials = CredentialCache.DefaultCredentials</c>를 설정할 것.
/// UI는 설치할 파일 경로를 넘기지 않는다 (권한 상승 방지).
/// </summary>
public interface IUpdateService
{
    Task<UpdateStatus> GetStatusAsync(CancellationToken ct);

    Task<UpdateStatus> CheckNowAsync(CancellationToken ct);

    Task InstallWhenIdleAsync(CancellationToken ct);

    Task SkipVersionAsync(string version, CancellationToken ct);

    Task ApplySettingsAsync(UpdateSettings settings, CancellationToken ct);
}
