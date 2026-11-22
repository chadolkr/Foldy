using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Foldy.Presentation.Files;
using Foldy.Presentation.Services;

namespace Foldy.Presentation.Jobs;

public enum JobState { Queued, Running, Paused, NeedsAttention, Done, Failed, Canceled }

/// <summary>
/// 작업 패널의 한 줄 (설계서 5.5의 IJob을 화면 쪽에서 본 모습). 진행률은 절대 뒤로 가지 않고,
/// 숫자는 초당 최대 10회만 갱신한다 (DESIGN.md 진행률 규칙).
/// </summary>
public sealed partial class JobViewModel : ObservableObject, IProgress<FileOpProgress>
{
    private readonly IUiDispatcher _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastUpdateTicks;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _detail = Foldy.Core.Localization.Loc.T("job.preparing");

    [ObservableProperty]
    private double _progress; // 0~1, 모르면 -1

    [ObservableProperty]
    private JobState _state = JobState.Queued;

    /// <summary>마스코트(연주·우진) 장면을 고르는 작업 종류.</summary>
    public Foldy.Presentation.Mascot.JobKind MascotKind { get; init; } = Foldy.Presentation.Mascot.JobKind.Copy;

    /// <summary>설정의 애니메이션 켜기/끄기.</summary>
    public bool MascotEnabled { get; init; } = true;

    public JobViewModel(string title, IUiDispatcher ui)
    {
        _title = title;
        _ui = ui;
    }

    public CancellationToken Token => _cts.Token;

    public TimeSpan Elapsed => _sw.Elapsed;

    public bool IsFinished => State is JobState.Done or JobState.Failed or JobState.Canceled;

    public void Cancel()
    {
        if (!IsFinished)
        {
            _cts.Cancel();
        }
    }

    public void Report(FileOpProgress value)
    {
        var now = Stopwatch.GetTimestamp();
        if (value.Done < value.Total && Stopwatch.GetElapsedTime(_lastUpdateTicks, now) < TimeSpan.FromMilliseconds(100))
        {
            return;
        }

        _lastUpdateTicks = now;
        _ui.Post(() =>
        {
            if (IsFinished)
            {
                return;
            }

            State = JobState.Running;
            if (value.Total > 0)
            {
                Progress = Math.Max(Progress, Math.Clamp((double)value.Done / value.Total, 0, 1));
            }

            Detail = value.CurrentItem is { Length: > 0 } cur ? cur : $"{value.Done:N0} / {value.Total:N0}";
        });
    }

    public void Complete(FileOpResult r)
    {
        _sw.Stop();
        if (r.Succeeded)
        {
            State = JobState.Done;
            Progress = 1;
            Detail = Foldy.Core.Localization.Loc.F("job.done", FormatElapsed(_sw.Elapsed)) + (string.IsNullOrEmpty(r.Error) ? "" : $" · {r.Error}");
        }
        else if (r.Aborted)
        {
            State = JobState.Canceled;
            Detail = Foldy.Core.Localization.Loc.T("ops.cancelled");
        }
        else
        {
            State = JobState.Failed;
            Detail = r.Error ?? Foldy.Core.Localization.Loc.T("job.failed");
        }

        _cts.Dispose();
    }

    private static string FormatElapsed(TimeSpan t) => t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0}ms" : Foldy.Core.Localization.Loc.F("job.seconds", t.TotalSeconds);

    public static string Describe(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? $"'{System.IO.Path.GetFileName(paths[0].TrimEnd('\\'))}'" : Foldy.Core.Localization.Loc.F("pane.items", paths.Count);

    internal static string SizeOrCount(IReadOnlyList<FileItem> items) =>
        items.Count == 1 ? items[0].Name : Foldy.Core.Localization.Loc.F("pane.items", items.Count);
}
