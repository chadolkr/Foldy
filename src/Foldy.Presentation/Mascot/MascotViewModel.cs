using Foldy.Presentation.Jobs;

namespace Foldy.Presentation.Mascot;

/// <summary>마스코트가 연기할 작업 종류.</summary>
public enum JobKind
{
    Copy,
    Move,
    MoveSameDrive,
    Delete,
    DeletePermanent,
    NetworkWait,
    Analyze,
    GitPush,
    GitPull,
    Search,
    Index,
}

/// <summary>
/// 화면에 그릴 장면. 두 아이 "연주"(여)와 "우진"(남)의 농구 이야기.
/// 장면 정의(길이·반복)는 assets/mascot/scenes.json과 이름이 같아야 한다.
/// </summary>
public enum MascotScene
{
    /// <summary>그리지 않는다 (5초 미만 작업, 끝난 뒤 2초 경과, 꺼짐).</summary>
    Hidden,
    /// <summary>패스 위주의 연속 동작 (Git pull).</summary>
    Pass,
    /// <summary>복사·이동·삭제 진행 중: 다양한 동작을 이어 붙이며 진행률만큼 왼쪽에서 오른쪽으로 전진.</summary>
    Dribble,
    /// <summary>우진의 점프슛 반복 (Git push).</summary>
    Shoot,
    /// <summary>완료(복사): 3점 라인 밖에서 3점슛 성공 → 하이파이브.</summary>
    Score,
    /// <summary>완료(이동): 속공, 연주의 앨리웁 패스를 우진이 덩크 → 하이파이브.</summary>
    Dunk,
    /// <summary>진행 100% 근처: 마무리 위치 바깥에서 슛 없이 준비.</summary>
    Setup,
    /// <summary>완료(그 외): 둘이 하이파이브.</summary>
    HighFive,
    /// <summary>완료(삭제): 사이드라인 너머 코트 옆 휴지통에 던진다. 실패하면 빗나가 튕김.</summary>
    TossBin,
    /// <summary>완료(영구 삭제): 경기장 밖으로 멀리 던져 버린다.</summary>
    OutOfBounds,
    /// <summary>검색·네트워크 대기·끊김: 둘이 두리번거리며 공을 찾는다.</summary>
    Search,
    /// <summary>색인·디스크 분석: 우진은 드리블 연습, 연주는 기록.</summary>
    Practice,
    /// <summary>일시정지·이름 충돌: 우진이 공을 안고 서 있다.</summary>
    Hold,
    /// <summary>오류: 슛이 림에 맞고 튕겨 나온다.</summary>
    Miss,
}

/// <summary>작업이 끝날 때의 마무리 종류 (코트 모양과 100% 위치가 달라진다).</summary>
public enum MascotFinish
{
    None,
    ThreePointer,
    Dunk,
    BinToss,
    Punt,
}

/// <summary>머리 위 말풍선.</summary>
public enum MascotBubble
{
    None,
    Question,
    Exclaim,
    Zzz,
}

/// <summary>삭제 던지기 결과: 골인 = 성공, 빗나감 = 실패.</summary>
public enum TossOutcome
{
    None,
    Hit,
    Miss,
}

/// <summary>작업의 세부 단계. 복사·이동은 Pack(패스) → Carry(드리블) → Confirm(슛), Git은 Prepare/Transfer/Finish.</summary>
public enum MascotPhase
{
    /// <summary>모름 (진행률로 추정).</summary>
    None,
    Pack,
    Carry,
    Confirm,
    Sweep,
    Prepare,
    Transfer,
    Finish,
}

/// <summary>한 번의 판정에 필요한 입력. 전부 작업 쪽에서 이미 아는 값이다 (JobViewModel에서 그대로 채운다).</summary>
public readonly record struct MascotInput
{
    public JobKind Kind { get; init; }

    public JobState State { get; init; }

    /// <summary>작업 시작 후 경과. 끝난 작업은 총 소요 시간(멈춘 값).</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>끝난 뒤 지난 시간. 진행 중이면 null.</summary>
    public TimeSpan? SinceFinished { get; init; }

    /// <summary>현재 처리량(바이트/초). 드리블 속도에 연결.</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>0~1, 모르면 -1.</summary>
    public double Progress { get; init; }

    public MascotPhase Phase { get; init; }

    /// <summary>이름 충돌 등 사용자 입력을 기다리는 중 (State가 NeedsAttention이어도 같다).</summary>
    public bool HasConflict { get; init; }

    /// <summary>네트워크가 끊겼다.</summary>
    public bool NetworkLost { get; init; }

    /// <summary>삭제 중 마지막 항목이 실패했다 → 마무리에서 던진 공이 빗나간다.</summary>
    public bool ItemFailed { get; init; }

    /// <summary>작업마다 정하는 시드. 같은 시드면 동작 순서가 같다 (테스트·재현용).</summary>
    public int Seed { get; init; }
}

/// <summary>표시 정책. 설정과 Windows "애니메이션 효과 끄기"에서 온다.</summary>
public sealed record MascotOptions
{
    /// <summary>사용자가 설정에서 켰는가.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>OS가 애니메이션을 허용하는가 (SystemParameters.ClientAreaAnimation).</summary>
    public bool AnimationsAllowed { get; init; } = true;

    /// <summary>이보다 짧은 작업에는 보이지 않는다.</summary>
    public TimeSpan MinDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>완료 장면(슛 성공·하이파이브) 후 숨기기까지.</summary>
    public TimeSpan DoneHold { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>오류 장면을 유지하는 시간.</summary>
    public TimeSpan FailHold { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>네트워크 대기에서 "?"를 띄우고 [취소]를 권하는 시점.</summary>
    public TimeSpan NetworkImpatient { get; init; } = TimeSpan.FromSeconds(15);

    public static MascotOptions Default { get; } = new();
}

/// <summary>판정 결과 = 렌더러가 그릴 한 장면.</summary>
public readonly record struct MascotFrame(
    MascotScene Scene,
    double Speed,
    MascotBubble Bubble,
    TossOutcome Outcome,
    double Progress,
    MascotPhase Phase,
    bool ShowCancelHint,
    int Seed = 0,
    MascotFinish Finish = MascotFinish.None)
{
    public static MascotFrame Hidden { get; } = new(MascotScene.Hidden, 1, MascotBubble.None, TossOutcome.None, -1, MascotPhase.None, false);

    public bool Visible => Scene != MascotScene.Hidden;
}

/// <summary>
/// 작업 종류 + 상태 + 측정값 → 장면·속도·말풍선. 순수 함수라 시간은 입력으로 받는다.
/// </summary>
public static class MascotViewModel
{
    /// <summary>드리블 속도 배율 범위.</summary>
    public const double MinSpeed = 0.9;

    public const double MaxSpeed = 1.8;

    /// <summary>진행률이 이 값 이상이면 마무리 위치 바깥에서 준비한다(슛은 하지 않는다).</summary>
    public const double SetupFrom = 0.92;

    public static MascotFrame Evaluate(MascotInput input, MascotOptions? options = null)
    {
        var o = options ?? MascotOptions.Default;
        if (!o.Enabled || !o.AnimationsAllowed)
        {
            return MascotFrame.Hidden;
        }

        var state = input.State;
        if (state is JobState.Queued or JobState.Canceled)
        {
            return MascotFrame.Hidden;
        }

        // 5초 미만 작업은 아예 보이지 않는다. 끝난 작업의 Elapsed는 총 소요 시간이므로 같은 규칙이 적용된다.
        if (input.Elapsed < o.MinDuration)
        {
            return MascotFrame.Hidden;
        }

        var progress = input.Progress is >= 0 and <= 1 ? input.Progress : -1;

        if (state is JobState.Done)
        {
            if (input.SinceFinished is { } since && since >= o.DoneHold)
            {
                return MascotFrame.Hidden;
            }

            // 작업마다 다른 마무리: 복사=3점슛, 이동=속공 앨리웁 덩크, 삭제=사이드라인 밖 휴지통, 영구 삭제=경기장 밖으로 멀리 던지기. 성공하면 하이파이브.
            var finishScene = input.Kind switch
            {
                JobKind.Copy => MascotScene.Score,
                JobKind.Move or JobKind.MoveSameDrive => MascotScene.Dunk,
                JobKind.Delete => MascotScene.TossBin,
                JobKind.DeletePermanent => MascotScene.OutOfBounds,
                _ => MascotScene.HighFive,
            };
            return Make(finishScene, 1, MascotBubble.None, FinishOutcome());
        }

        if (state is JobState.Failed)
        {
            if (input.SinceFinished is { } since2 && since2 >= o.FailHold)
            {
                return MascotFrame.Hidden;
            }

            return input.Kind == JobKind.Delete
                ? Make(MascotScene.TossBin, 1, MascotBubble.Exclaim, TossOutcome.Miss)
                : Make(MascotScene.Miss, 1, MascotBubble.Exclaim);
        }

        // 이름 충돌 / 사용자 대기: 공을 안고 "?"
        if (state is JobState.NeedsAttention || input.HasConflict)
        {
            return Make(MascotScene.Hold, 1, MascotBubble.Question);
        }

        if (state is JobState.Paused)
        {
            return Make(MascotScene.Hold, 1, MascotBubble.None);
        }

        if (input.NetworkLost)
        {
            return Make(MascotScene.Search, 1, MascotBubble.Question);
        }

        var speed = SpeedFor(input.BytesPerSecond);
        switch (input.Kind)
        {
            case JobKind.Copy:
            case JobKind.Move:
            case JobKind.MoveSameDrive:
            case JobKind.Delete:
            case JobKind.DeletePermanent:
                return Make(TransferScene(), speed, MascotBubble.None, FinishOutcome());

            case JobKind.NetworkWait:
                {
                    var impatient = input.Elapsed >= o.NetworkImpatient;
                    return Make(MascotScene.Search, 1, impatient ? MascotBubble.Question : MascotBubble.None, showCancelHint: impatient);
                }

            case JobKind.Search:
                return Make(MascotScene.Search, 1, MascotBubble.None);

            case JobKind.Analyze:
            case JobKind.Index:
                return Make(MascotScene.Practice, 1, MascotBubble.None);

            case JobKind.GitPush:
                return Make(MascotScene.Shoot, 1, MascotBubble.None);

            case JobKind.GitPull:
                return Make(MascotScene.Pass, 1, MascotBubble.None);

            default:
                return MascotFrame.Hidden;
        }

        // 진행 중에는 슛하지 않는다. 다양한 동작을 이어 붙이며 진행률만큼 왼쪽에서 오른쪽으로 전진하고,
        // 100%에 가까워지면(Setup) 마무리 위치 바깥에서 준비한다. 슛·덩크·던지기는 완료(Done) 때만 나온다.
        TossOutcome FinishOutcome() => input.Kind == JobKind.Delete ? (input.ItemFailed ? TossOutcome.Miss : TossOutcome.Hit) : TossOutcome.None;

        MascotScene TransferScene() => input.Phase switch
        {
            MascotPhase.Confirm or MascotPhase.Sweep or MascotPhase.Finish => MascotScene.Setup,
            MascotPhase.Pack or MascotPhase.Prepare or MascotPhase.Carry or MascotPhase.Transfer => MascotScene.Dribble,
            _ => progress >= SetupFrom ? MascotScene.Setup : MascotScene.Dribble,
        };

        MascotFrame Make(MascotScene scene, double spd, MascotBubble bubble, TossOutcome outcome = TossOutcome.None, bool showCancelHint = false) =>
            new(scene, spd, bubble, outcome, progress, input.Phase, showCancelHint, input.Seed, FinishOf(input.Kind));
    }

    /// <summary>완료 때 보여 줄 마무리 종류. 복사·이동·삭제·영구 삭제만 진행 막대처럼 전진한다.</summary>
    public static MascotFinish FinishOf(JobKind kind) => kind switch
    {
        JobKind.Copy => MascotFinish.ThreePointer,
        JobKind.Move or JobKind.MoveSameDrive => MascotFinish.Dunk,
        JobKind.Delete => MascotFinish.BinToss,
        JobKind.DeletePermanent => MascotFinish.Punt,
        _ => MascotFinish.None,
    };

    /// <summary>처리량 → 드리블 속도 배율. 로그 스케일: 0MB/s=0.9, 10MB/s≈1.3, 100MB/s≈1.7, 1GB/s 이상=1.8. 기본 장면이 이미 빠르므로 느린 쪽도 0.9 이상.</summary>
    public static double SpeedFor(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond <= 0)
        {
            return MinSpeed;
        }

        var mbps = bytesPerSecond / (1024.0 * 1024.0);
        return Math.Clamp(MinSpeed + (0.4 * Math.Log10(1 + mbps)), MinSpeed, MaxSpeed);
    }
}

/// <summary>
/// 상태 전이 시각을 기억해 Elapsed / SinceFinished를 만든다. 뷰와 테스트가 같은 시계(TimeProvider)를 쓴다.
/// </summary>
public sealed class MascotClock
{
    private readonly TimeProvider _time;
    private long? _start;
    private long? _end;

    public MascotClock(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>작업 시작 후 경과. 끝나면 그 시점에 멈춘다.</summary>
    public TimeSpan Elapsed => _start is { } s ? _time.GetElapsedTime(s, _end ?? _time.GetTimestamp()) : TimeSpan.Zero;

    /// <summary>끝난 뒤 지난 시간. 진행 중이면 null.</summary>
    public TimeSpan? SinceFinished => _end is { } e ? _time.GetElapsedTime(e) : null;

    /// <summary>상태가 바뀔 때마다(또는 매 틱) 호출한다.</summary>
    public void Update(JobState state)
    {
        var finished = state is JobState.Done or JobState.Failed or JobState.Canceled;
        if (_start is null)
        {
            if (state == JobState.Queued)
            {
                return;
            }

            _start = _time.GetTimestamp();
        }

        if (finished)
        {
            _end ??= _time.GetTimestamp();
        }
        else
        {
            _end = null;
        }
    }

    /// <summary>같은 뷰를 다른 작업에 재사용할 때.</summary>
    public void Reset()
    {
        _start = null;
        _end = null;
    }
}
