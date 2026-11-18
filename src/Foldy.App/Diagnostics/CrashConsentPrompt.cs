using System.Windows;
using System.Windows.Threading;
using Foldy.Reporting;
using Serilog;

namespace Foldy.App.Diagnostics;

/// <summary>
/// 지난 실행에서 남은 크래시 리포트가 있으면, 창이 뜬 뒤 한 번 동의를 묻고 보낸다.
/// 동의 없이는 아무것도 나가지 않는다. 거절하면 리포트를 지운다.
/// </summary>
internal static class CrashConsentPrompt
{
    public static void ScheduleOnIdle(Window owner) =>
        owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _ = RunAsync(owner));

    private static async Task RunAsync(Window owner)
    {
        try
        {
            if (!CrashReporting.UploadEnabled) return; // 릴레이 주소가 없는 빌드: 로컬에만 보관
            var pending = CrashReporting.Pending();
            if (pending.Count == 0) return;

            var fatal = pending.Count(p => p.Fatal);
            var what = fatal > 0 ? "지난번에 Foldy가 비정상 종료되었습니다." : "Foldy에서 오류가 발생했습니다.";
            var answer = MessageBox.Show(owner,
                $"{what}\n\n오류 보고서 {pending.Count}건을 개발자에게 보낼까요?\n\n" +
                "보고서에는 앱·Windows 버전, 오류 내용, 최근 로그가 들어가며 파일 경로와 사용자 이름은 지웁니다. " +
                "보낸 내용은 GitHub 공개 이슈로 올라갑니다.",
                "오류 보고서 보내기", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes)
            {
                CrashReporting.DiscardPending();
                Log.Information("크래시 리포트 {Count}건 전송 거절", pending.Count);
                return;
            }

            var results = await Task.Run(() => CrashReporting.SendPendingAsync()).ConfigureAwait(true);
            foreach (var r in results)
                Log.Information("크래시 리포트 전송 {Outcome} {Issue} {Error}", r.Outcome, r.Response?.Url, r.Error);
        }
#pragma warning disable CA1031 // 보고 기능이 앱을 넘어뜨리면 안 된다
        catch (Exception ex)
        {
            Log.Warning(ex, "크래시 리포트 처리 실패");
        }
#pragma warning restore CA1031
    }
}
