using Foldy.Presentation.Update;

namespace Foldy.Update.Tests;

/// <summary>서비스 파이프 대신 쓰는 가짜. 호출 기록을 남긴다.</summary>
internal sealed class FakeUpdateService : IUpdateService
{
    public UpdateStatus Status { get; set; } = new(UpdatePhase.Idle, "1.0.0");

    public UpdateStatus? AfterCheck { get; set; }

    public bool Broken { get; set; }

    public List<string> Calls { get; } = [];

    public List<UpdateSettings> SavedSettings { get; } = [];

    public Task<UpdateStatus> GetStatusAsync(CancellationToken ct)
    {
        Calls.Add("status");
        ThrowIfBroken();
        return Task.FromResult(Status);
    }

    public Task<UpdateStatus> CheckNowAsync(CancellationToken ct)
    {
        Calls.Add("check");
        ThrowIfBroken();
        Status = AfterCheck ?? Status;
        return Task.FromResult(Status);
    }

    public Task InstallWhenIdleAsync(CancellationToken ct)
    {
        Calls.Add("install");
        ThrowIfBroken();
        return Task.CompletedTask;
    }

    public Task SkipVersionAsync(string version, CancellationToken ct)
    {
        Calls.Add("skip " + version);
        ThrowIfBroken();
        return Task.CompletedTask;
    }

    public Task ApplySettingsAsync(UpdateSettings settings, CancellationToken ct)
    {
        Calls.Add("settings");
        ThrowIfBroken();
        SavedSettings.Add(settings);
        return Task.CompletedTask;
    }

    private void ThrowIfBroken()
    {
        if (Broken)
        {
            throw new IOException("파이프 없음");
        }
    }
}

/// <summary>고정 시각·UTC 시간대.</summary>
internal sealed class FixedTime : TimeProvider
{
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
}

public class UpdateViewModelTests
{
    private static readonly DateTimeOffset Checked = new(2026, 11, 18, 8, 30, 0, TimeSpan.Zero);

    private static UpdateViewModel Create(FakeUpdateService svc, UpdateSettings? settings = null) =>
        new(svc, "1.0.0", settings, new FixedTime());

    [Fact]
    public void Initial_state_shows_not_checked_and_no_last_check()
    {
        var vm = Create(new FakeUpdateService());
        vm.StatusText.Should().Be("아직 확인하지 않았어요");
        vm.LastCheckText.Should().Be("마지막 확인: 없음");
        vm.Mode.Should().Be(UpdateMode.Auto);
        vm.Channel.Should().Be(UpdateChannel.Stable);
        vm.CanInstall.Should().BeFalse();
    }

    [Fact]
    public void Constructor_settings_are_not_saved_back()
    {
        var svc = new FakeUpdateService();
        var vm = Create(svc, new UpdateSettings(UpdateMode.NotifyOnly, UpdateChannel.Beta));
        vm.Mode.Should().Be(UpdateMode.NotifyOnly);
        vm.Channel.Should().Be(UpdateChannel.Beta);
        svc.SavedSettings.Should().BeEmpty();
    }

    [Fact]
    public async Task Check_now_updates_status_and_last_check()
    {
        var svc = new FakeUpdateService { AfterCheck = new UpdateStatus(UpdatePhase.UpToDate, "1.0.0", LastCheck: Checked) };
        var vm = Create(svc);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.CheckNowCommand.ExecuteAsync(null);

        svc.Calls.Should().Equal("check");
        vm.StatusText.Should().Be("최신 버전이에요 (1.0.0)");
        vm.LastCheckText.Should().Be("마지막 확인: 2026-11-18 08:30");
        vm.IsBusy.Should().BeFalse();
        changed.Should().Contain([nameof(UpdateViewModel.StatusText), nameof(UpdateViewModel.LastCheckText)]);
    }

    [Fact]
    public async Task Ready_update_can_be_installed_when_idle()
    {
        var svc = new FakeUpdateService { Status = new UpdateStatus(UpdatePhase.ReadyToInstall, "1.0.0", "1.2.3", Checked) };
        var vm = Create(svc);
        await vm.RefreshAsync();

        vm.StatusText.Should().Be("새 버전 1.2.3 설치 준비됨");
        vm.HasUpdate.Should().BeTrue();
        vm.CanInstall.Should().BeTrue();
        vm.InstallCommand.CanExecute(null).Should().BeTrue();

        await vm.InstallCommand.ExecuteAsync(null);
        svc.Calls.Should().Equal("status", "install");
        vm.Status.Phase.Should().Be(UpdatePhase.InstallScheduled);
        vm.StatusText.Should().Be("작업이 끝나면 1.2.3을(를) 설치해요");
        vm.InstallCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Skip_sends_version_but_mandatory_cannot_be_skipped()
    {
        var svc = new FakeUpdateService { Status = new UpdateStatus(UpdatePhase.ReadyToInstall, "1.0.0", "1.2.3") };
        var vm = Create(svc);
        await vm.RefreshAsync();
        vm.CanSkip.Should().BeTrue();
        await vm.SkipCommand.ExecuteAsync(null);
        svc.Calls.Should().Contain("skip 1.2.3");
        vm.StatusText.Should().Be("1.2.3 버전을 건너뛰었습니다");

        svc.Status = new UpdateStatus(UpdatePhase.ReadyToInstall, "1.0.0", "1.3.0", Mandatory: true);
        await vm.RefreshAsync();
        vm.CanSkip.Should().BeFalse();
        vm.SkipCommand.CanExecute(null).Should().BeFalse();
        vm.StatusText.Should().Be("필수 업데이트 1.3.0 설치 준비됨");
    }

    [Fact]
    public async Task Downloading_shows_progress()
    {
        var svc = new FakeUpdateService { Status = new UpdateStatus(UpdatePhase.Downloading, "1.0.0", "1.2.3", DownloadProgress: 0.42) };
        var vm = Create(svc);
        await vm.RefreshAsync();
        vm.StatusText.Should().StartWith("새 버전 1.2.3 내려받는 중…").And.Contain("42");
    }

    [Fact]
    public async Task Changing_mode_and_channel_saves_settings()
    {
        var svc = new FakeUpdateService();
        var vm = Create(svc);
        vm.Mode = UpdateMode.Off;
        await vm.LastSettingsSave;
        vm.Channel = UpdateChannel.Beta;
        await vm.LastSettingsSave;

        svc.SavedSettings.Should().Equal(
            new UpdateSettings(UpdateMode.Off, UpdateChannel.Stable),
            new UpdateSettings(UpdateMode.Off, UpdateChannel.Beta));
        vm.StatusText.Should().Be("자동 업데이트 꺼짐");
    }

    [Fact]
    public async Task Service_failure_is_quiet_and_recovers()
    {
        var svc = new FakeUpdateService { Broken = true };
        var vm = Create(svc);

        await vm.CheckNowCommand.ExecuteAsync(null);
        vm.ServiceAvailable.Should().BeFalse();
        vm.StatusText.Should().Be("지금은 업데이트를 확인할 수 없어요");
        vm.IsBusy.Should().BeFalse();
        vm.CheckNowCommand.CanExecute(null).Should().BeFalse();

        svc.Broken = false;
        svc.Status = new UpdateStatus(UpdatePhase.UpToDate, "1.0.0", LastCheck: Checked);
        await vm.RefreshAsync();
        vm.ServiceAvailable.Should().BeTrue();
        vm.CheckNowCommand.CanExecute(null).Should().BeTrue();
        vm.StatusText.Should().Be("최신 버전이에요 (1.0.0)");
    }

    [Fact]
    public async Task Network_failure_only_shows_as_stale_last_check()
    {
        // 서비스는 GitHub 장애를 오류로 돌려주지 않고 이전 상태와 이전 확인 시간을 그대로 준다
        var svc = new FakeUpdateService { Status = new UpdateStatus(UpdatePhase.UpToDate, "1.0.0", LastCheck: Checked) };
        var vm = Create(svc);
        await vm.CheckNowCommand.ExecuteAsync(null);
        vm.ServiceAvailable.Should().BeTrue();
        vm.LastCheckText.Should().Be("마지막 확인: 2026-11-18 08:30");
    }
}
