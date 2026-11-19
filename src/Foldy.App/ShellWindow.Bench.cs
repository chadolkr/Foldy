using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Threading;
using Foldy.Core.Diagnostics;
using Foldy.Presentation.Tabs;
using Serilog;

namespace Foldy.App;

/// <summary>
/// 진짜 화면(ShellWindow: 사이드바·폴더 트리·Git 바 포함)의 성능 측정:
/// <c>Foldy.exe --bench-shell --bench-folder &lt;폴더&gt; --step 120 --out r.json</c>.
/// 시작(프로세스 시작 → 첫 그리기), 폴더 열기(첫 묶음·전체), 메모리, 스크롤 프레임 간격을 JSON으로 남기고 종료한다.
/// 사용자 설정을 건드리지 않으려면 환경 변수 FOLDY_DATA_DIR를 임시 폴더로 준다 (build/perf-check.ps1이 한다).
/// 화면이 잠겨 있거나 접근성 도구가 UIA를 듣고 있으면 프레임 간격이 크게 늘어난다.
/// 그래서 스크롤 없이 잰 "유휴 프레임"과 UIA 듣는 쪽 유무도 같이 기록해, 환경 탓인지 코드 탓인지 가린다.
/// </summary>
public partial class ShellWindow
{
    private static readonly JsonSerializerOptions BenchJson = new() { WriteIndented = true };

    /// <summary>스크롤 측정에 쓰는 최대 시간. 환경이 느려 900프레임이 몇 분씩 걸릴 때 끊는다 (결과에 truncated로 표시).</summary>
    private static readonly TimeSpan ScrollBudget = TimeSpan.FromSeconds(45);

    internal async Task RunBenchAsync(AppOptions o)
    {
        var exitCode = 1;
        try
        {
            var proc = Process.GetCurrentProcess();
            var rendered = new TaskCompletionSource();
            ContentRendered += (_, _) => rendered.TrySetResult();
            await rendered.Task;
            var firstPaintMs = (DateTime.Now - proc.StartTime).TotalMilliseconds;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var interactiveMs = (DateTime.Now - proc.StartTime).TotalMilliseconds;
            await Task.Delay(1500);
            proc.Refresh();
            var idleWorkingSet = proc.WorkingSet64;

            object? folder = null;
            object? scroll = null;
            if (o.BenchFolder is { Length: > 0 } dir && Shell.ActivePane.ActiveFolder is { } tab)
            {
                var before = tab.LastLoadTime;
                var t0 = Stopwatch.StartNew();
                _ = tab.NavigateAsync(dir);
                while ((tab.State != LoadState.Idle || !tab.IsLoaded || tab.LastLoadTime == before) && t0.Elapsed < TimeSpan.FromMinutes(2))
                {
                    await Task.Delay(10);
                }

                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(1500);
                var count = tab.Items.Count;
                proc.Refresh();
                var ws = proc.WorkingSet64;
                var priv = proc.PrivateMemorySize64;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                var managed = GC.GetTotalMemory(forceFullCollection: true);
                folder = new
                {
                    dir,
                    entries = count,
                    firstBatchMs = Math.Round(tab.FirstBatchTime.TotalMilliseconds, 1),
                    totalMs = Math.Round(tab.LastLoadTime.TotalMilliseconds, 1),
                    workingSetMB = Math.Round(ws / 1048576.0, 1),
                    privateMB = Math.Round(priv / 1048576.0, 1),
                    managedMB = Math.Round(managed / 1048576.0, 1),
                };

                if (count >= 1000)
                {
                    var idle = await IdleFramesAsync();
                    var (list, scroller) = ActiveView.BenchHandles;
                    var (stats, truncated) = await ScrollAsync(list, scroller, o.Step);
                    scroll = new
                    {
                        rows = count,
                        step = o.Step,
                        stats,
                        truncated,
                        idleFrames = idle,
                        uiaPropertyListeners = AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged),
                        uiaStructureListeners = AutomationPeer.ListenerExists(AutomationEvents.StructureChanged),
                    };
                }
            }

            var result = new
            {
                kind = "shell",
                version = AppVersion,
                renderTier = RenderCapability.Tier >> 16,
                startup = new
                {
                    firstPaintMs = Math.Round(firstPaintMs, 1),
                    interactiveMs = Math.Round(interactiveMs, 1),
                    idleWorkingSetMB = Math.Round(idleWorkingSet / 1048576.0, 1),
                },
                folder,
                scroll,
            };
            var json = JsonSerializer.Serialize(result, BenchJson);
            if (o.Out is { } path)
            {
                File.WriteAllText(path, json);
            }

            Log.Information("셸 벤치 결과 {Json}", json);
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "셸 벤치 실패");
            if (o.Out is { } path)
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new { kind = "shell", error = ex.ToString() }, BenchJson));
            }
        }
        finally
        {
            Application.Current.Shutdown(exitCode);
        }
    }

    /// <summary>스크롤하지 않고 60프레임의 간격을 잰다: 측정 환경(갱신 주기, 잠금 화면, 접근성 도구 간섭)이 정상인지 보는 기준값.</summary>
    private static Task<FrameStats> IdleFramesAsync()
    {
        var tcs = new TaskCompletionSource<FrameStats>();
        var deltas = new List<double>(64);
        TimeSpan last = default;
        var n = 0;

        void Tick(object? s, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == last)
            {
                return;
            }

            if (last != default && n++ > 5)
            {
                deltas.Add((now - last).TotalMilliseconds);
            }

            last = now;
            if (deltas.Count >= 60)
            {
                CompositionTarget.Rendering -= Tick;
                tcs.TrySetResult(FrameStats.From(deltas));
            }
        }

        CompositionTarget.Rendering += Tick;
        return tcs.Task;
    }

    /// <summary>매 프레임 stepRows행씩 끝까지 스크롤하며 CompositionTarget.Rendering 간격을 잰다 (처음 30프레임은 버림).</summary>
    private static Task<(FrameStats Stats, bool Truncated)> ScrollAsync(Foldy.UI.Controls.FastFileList list, System.Windows.Controls.ScrollViewer sv, int stepRows)
    {
        sv.ScrollToTop();
        var tcs = new TaskCompletionSource<(FrameStats, bool)>();
        var deltas = new List<double>(1024);
        TimeSpan last = default;
        var frame = 0;
        var clock = Stopwatch.StartNew();

        void Tick(object? s, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == last)
            {
                return;
            }

            if (frame > 30 && last != default)
            {
                deltas.Add((now - last).TotalMilliseconds);
            }

            last = now;
            frame++;
            var atEnd = sv.VerticalOffset >= sv.ScrollableHeight - 0.5 && frame > 30;
            var timedOut = clock.Elapsed > ScrollBudget;
            if (atEnd || deltas.Count >= 900 || timedOut)
            {
                CompositionTarget.Rendering -= Tick;
                tcs.TrySetResult((FrameStats.From(deltas), timedOut && !atEnd));
                return;
            }

            if (frame > 30)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset + (stepRows * list.RowHeight));
                list.UpdateLayout();
            }
        }

        CompositionTarget.Rendering += Tick;
        return tcs.Task;
    }
}
