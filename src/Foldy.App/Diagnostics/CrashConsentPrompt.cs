using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Foldy.Reporting;
using Serilog;

namespace Foldy.App.Diagnostics;

/// <summary>
/// 모인 오류 보고서(비정상 종료 + 평소 오류)를 동의에 따라 보낸다.
/// 자동 보내기에 동의하지 않았으면 실행 직후 한 번 묻고(항상 자동 / 이번만 / 보내지 않기), 동의 없이는 아무것도 나가지 않는다.
/// 자동 보내기가 켜져 있으면 시작할 때와 30분마다 조용히 보낸다. 같은 오류는 릴레이가 한 이슈로 묶는다.
/// </summary>
internal static class CrashConsentPrompt
{
    private static Func<bool> s_auto = () => false;
    private static Action<bool> s_setAuto = _ => { };
    private static DispatcherTimer? s_timer;

    public static void ScheduleOnIdle(Window owner, Func<bool> autoSend, Action<bool> setAutoSend)
    {
        s_auto = autoSend;
        s_setAuto = setAutoSend;
        owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _ = RunAsync(owner, interactive: true));
        s_timer = new DispatcherTimer(TimeSpan.FromMinutes(30), DispatcherPriority.Background, (_, _) => _ = RunAsync(owner, interactive: false), owner.Dispatcher);
        s_timer.Start();
    }

    private static async Task RunAsync(Window owner, bool interactive)
    {
        try
        {
            if (!CrashReporting.UploadEnabled) return; // 릴레이 주소가 없는 빌드: 로컬에만 보관
            var pending = CrashReporting.Pending();
            if (pending.Count == 0) return;

            if (!s_auto())
            {
                if (!interactive) return; // 동의 전에는 실행 중에 묻지 않는다 (다음 실행 때 묻는다)
                var choice = Ask(owner, pending);
                if (choice == Choice.Never)
                {
                    CrashReporting.DiscardPending();
                    Log.Information("오류 보고서 {Count}건 전송 거절", pending.Count);
                    return;
                }

                if (choice == Choice.Always)
                {
                    s_setAuto(true);
                }
            }

            var results = await Task.Run(() => CrashReporting.SendPendingAsync()).ConfigureAwait(true);
            foreach (var r in results)
                Log.Information("오류 보고서 전송 {Outcome} {Issue} {Error}", r.Outcome, r.Response?.Url, r.Error);
        }
#pragma warning disable CA1031 // 보고 기능이 앱을 넘어뜨리면 안 된다
        catch (Exception ex)
        {
            Log.Warning(ex, "오류 보고서 처리 실패");
        }
#pragma warning restore CA1031
    }

    private enum Choice { Never, Once, Always }

    private static Choice Ask(Window owner, IReadOnlyList<StoredCrash> pending)
    {
        var fatal = pending.Count(p => p.Fatal);
        var what = fatal > 0 ? Foldy.Core.Localization.Loc.T("crash.lastTime") : Foldy.Core.Localization.Loc.F("crash.count", pending.Count);
        var result = Choice.Never;
        var win = new Window
        {
            Title = Foldy.Core.Localization.Loc.T("crash.send"),
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        win.SetResourceReference(Control.BackgroundProperty, "Brush.Window");
        win.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        win.SetResourceReference(Control.FontFamilyProperty, "Font.UI");
        var text = new TextBlock
        {
            Text = what + Foldy.Core.Localization.Loc.T("crash.help") +
                   Foldy.Core.Localization.Loc.T("crash.contents") +
                   Foldy.Core.Localization.Loc.T("crash.grouped"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
        };

        Button Make(string label, Choice c, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = label, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), IsDefault = isDefault, IsCancel = isCancel };
            b.Click += (_, _) => { result = c; win.Close(); };
            return b;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(Make(Foldy.Core.Localization.Loc.T("crash.always"), Choice.Always, isDefault: true));
        buttons.Children.Add(Make(Foldy.Core.Localization.Loc.T("crash.once"), Choice.Once));
        buttons.Children.Add(Make(Foldy.Core.Localization.Loc.T("sw.diag.dontSend"), Choice.Never, isCancel: true)); // Esc = 보내지 않기 (가장 안전한 쪽)
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(text);
        panel.Children.Add(buttons);
        win.Content = panel;
        win.ShowDialog();
        return result;
    }
}
