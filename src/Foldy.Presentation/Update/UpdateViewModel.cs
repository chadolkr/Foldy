using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Foldy.Presentation.Update;

/// <summary>
/// 설정 화면·상태바의 업데이트 표시 (설계서 10.5). 상태는 서비스가 들고 있고 여기서는 보여 주기만 한다.
/// 서비스에 연결할 수 없으면(포터블 모드, 서비스 중지) 조용히 "사용할 수 없음"으로 표시한다.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _service;
    private readonly TimeProvider _time;
    private bool _loadingSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(LastCheckText), nameof(CanInstall), nameof(CanSkip), nameof(HasUpdate))]
    private UpdateStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private UpdateMode _mode = UpdateMode.Auto;

    [ObservableProperty]
    private UpdateChannel _channel = UpdateChannel.Stable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanInstall), nameof(CanSkip))]
    private bool _serviceAvailable = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isBusy;

    public UpdateViewModel(IUpdateService service, string currentVersion, UpdateSettings? settings = null, TimeProvider? time = null)
    {
        _service = service;
        _time = time ?? TimeProvider.System;
        _status = new UpdateStatus(UpdatePhase.Idle, currentVersion);
        var s = settings ?? UpdateSettings.Default;
        _loadingSettings = true;
        Mode = s.Mode;
        Channel = s.Channel;
        _loadingSettings = false;
        CheckNowCommand = new AsyncRelayCommand(CheckNowAsync, () => !IsBusy && ServiceAvailable);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => CanInstall);
        SkipCommand = new AsyncRelayCommand(SkipAsync, () => CanSkip);
    }

    /// <summary>[지금 확인] 버튼.</summary>
    public IAsyncRelayCommand CheckNowCommand { get; }

    /// <summary>상태바의 [재시작하고 업데이트]: 작업이 끝나면 설치하도록 예약.</summary>
    public IAsyncRelayCommand InstallCommand { get; }

    /// <summary>[이 버전 건너뛰기]. 필수 업데이트에는 없다.</summary>
    public IAsyncRelayCommand SkipCommand { get; }

    public UpdateSettings Settings => new(Mode, Channel);

    public bool HasUpdate => Status.AvailableVersion is not null
        && Status.Phase is UpdatePhase.Downloading or UpdatePhase.ReadyToInstall or UpdatePhase.InstallScheduled;

    public bool CanInstall => ServiceAvailable && Status.Phase == UpdatePhase.ReadyToInstall;

    public bool CanSkip => ServiceAvailable && !Status.Mandatory && Status.AvailableVersion is not null
        && Status.Phase is UpdatePhase.Downloading or UpdatePhase.ReadyToInstall;

    public string StatusText
    {
        get
        {
            if (!ServiceAvailable)
            {
                return "업데이트 서비스를 사용할 수 없습니다";
            }

            if (IsBusy && Status.Phase is not UpdatePhase.Downloading)
            {
                return "업데이트 확인 중…";
            }

            var v = Status.AvailableVersion;
            return Status.Phase switch
            {
                _ when Mode == UpdateMode.Off && Status.Phase is UpdatePhase.Idle or UpdatePhase.Disabled or UpdatePhase.UpToDate
                    => "자동 업데이트 꺼짐",
                UpdatePhase.Idle => "아직 확인하지 않았습니다",
                UpdatePhase.Checking => "업데이트 확인 중…",
                UpdatePhase.UpToDate => $"최신 버전입니다 ({Status.CurrentVersion})",
                UpdatePhase.Downloading => $"새 버전 {v} 내려받는 중… {Math.Clamp(Status.DownloadProgress, 0, 1).ToString("P0", CultureInfo.CurrentCulture)}",
                UpdatePhase.ReadyToInstall when Status.Mandatory => $"필수 업데이트 {v} 설치 준비됨",
                UpdatePhase.ReadyToInstall => $"새 버전 {v} 설치 준비됨",
                UpdatePhase.InstallScheduled => $"작업이 끝나면 {v}을(를) 설치합니다",
                UpdatePhase.Skipped => $"{v} 버전을 건너뛰었습니다",
                UpdatePhase.Disabled => "자동 업데이트 꺼짐",
                _ => "",
            };
        }
    }

    /// <summary>설정 화면의 "마지막 확인" 줄. 네트워크 장애는 이 시간이 멈추는 것으로만 보인다.</summary>
    public string LastCheckText => Status.LastCheck is { } t
        ? "마지막 확인: " + TimeZoneInfo.ConvertTime(t, _time.LocalTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
        : "마지막 확인: 없음";

    /// <summary>화면을 열 때 서비스의 현재 상태를 읽는다.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await RunAsync(async () => Status = await _service.GetStatusAsync(ct).ConfigureAwait(true), busy: false).ConfigureAwait(true);
    }

    private Task CheckNowAsync() =>
        RunAsync(async () => Status = await _service.CheckNowAsync(CancellationToken.None).ConfigureAwait(true), busy: true);

    private Task InstallAsync() => RunAsync(async () =>
    {
        await _service.InstallWhenIdleAsync(CancellationToken.None).ConfigureAwait(true);
        Status = Status with { Phase = UpdatePhase.InstallScheduled };
    }, busy: false);

    private Task SkipAsync()
    {
        var v = Status.AvailableVersion;
        if (v is null)
        {
            return Task.CompletedTask;
        }

        return RunAsync(async () =>
        {
            await _service.SkipVersionAsync(v, CancellationToken.None).ConfigureAwait(true);
            Status = Status with { Phase = UpdatePhase.Skipped };
        }, busy: false);
    }

    partial void OnModeChanged(UpdateMode value) => SaveSettings();

    partial void OnChannelChanged(UpdateChannel value) => SaveSettings();

    partial void OnStatusChanged(UpdateStatus value) => NotifyCommands();

    partial void OnServiceAvailableChanged(bool value) => NotifyCommands();

    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    private void NotifyCommands()
    {
        // 생성자에서 명령을 만들기 전에도 불릴 수 있다
        CheckNowCommand?.NotifyCanExecuteChanged();
        InstallCommand?.NotifyCanExecuteChanged();
        SkipCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>설정 변경 마지막 저장 작업 (테스트에서 기다릴 수 있게 노출).</summary>
    public Task LastSettingsSave { get; private set; } = Task.CompletedTask;

    private void SaveSettings()
    {
        if (_loadingSettings)
        {
            return;
        }

        var settings = Settings;
        LastSettingsSave = RunAsync(() => _service.ApplySettingsAsync(settings, CancellationToken.None), busy: false);
    }

    private async Task RunAsync(Func<Task> body, bool busy)
    {
        if (busy)
        {
            IsBusy = true;
        }

        try
        {
            await body().ConfigureAwait(true);
            ServiceAvailable = true;
        }
        catch (OperationCanceledException)
        {
            // 화면을 닫음
        }
#pragma warning disable CA1031 // 서비스 연결 실패는 화면에 "사용할 수 없음"으로만 보이고 앱을 멈추지 않는다
        catch (Exception)
#pragma warning restore CA1031
        {
            ServiceAvailable = false;
        }
        finally
        {
            if (busy)
            {
                IsBusy = false;
            }
        }
    }
}
