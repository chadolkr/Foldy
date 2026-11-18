using System.IO;
using System.Windows;
using System.Windows.Threading;
using Foldy.App.Diagnostics;
using Foldy.Reporting;
using Serilog;

namespace Foldy.App;

public partial class App : Application
{
    private UiStallDetector? _stallDetector;

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy");

    public static AppOptions Options { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Options = AppOptions.Parse(e.Args);
        Foldy.UI.Controls.FileListView.LightweightAutomation = !Options.FullAutomation;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(DataDir, "logs", "foldy-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .WriteTo.Sink(new Diagnostics.ErrorReportSink())
            .CreateLogger();
        Log.Information("Foldy {Version} 시작, 인자: {Args}", typeof(App).Assembly.GetName().Version, e.Args);

        // 전역 예외: 로그를 남기고 앱은 살린다 (탭 단위 복구는 M1).
        // 크래시 리포트는 디스크에만 남기고, 다음 실행 때 동의를 받아 보낸다 (docs/bug-reporting.md).
        CrashReporting.Initialize(DataDir, typeof(App).Assembly);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log.ForContext(Diagnostics.ErrorReportSink.ReportedProperty, true).Error(ev.Exception, "관찰되지 않은 작업 예외");
            CrashReporting.Record(ev.Exception, fatal: false);
            ev.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            Log.ForContext(Diagnostics.ErrorReportSink.ReportedProperty, true).Fatal(ev.ExceptionObject as Exception, "처리되지 않은 예외 (종료됨: {Terminating})", ev.IsTerminating);
            CrashReporting.Record(ev.ExceptionObject as Exception, fatal: ev.IsTerminating);
        };

        _stallDetector = new UiStallDetector(Dispatcher, TimeSpan.FromMilliseconds(200));
        var settings = Foldy.Presentation.Persistence.AppSettings.Migrate(Foldy.Presentation.Persistence.JsonFileStore.Load(Path.Combine(DataDir, "settings.json"), () => new Foldy.Presentation.Persistence.AppSettings()));
        var theme = Options.ThemeGiven ? Options.Theme : Enum.TryParse<Foldy.UI.Theming.FoldyTheme>(settings.Theme, ignoreCase: true, out var t) ? t : Foldy.UI.Theming.FoldyTheme.System;
        Foldy.UI.Theming.ThemeManager.Initialize(this, theme);
        Foldy.UI.Theming.ThemeManager.SetAccent(settings.Accent);

        base.OnStartup(e);
        if (Options.IsBench || Options.Synthetic || Options.ListWpf)
        {
            new BenchWindow().Show();
        }
        else
        {
            var main = new ShellWindow();
            main.Show();
            CrashConsentPrompt.ScheduleOnIdle(main, () => main.AutoSendReports, v => main.AutoSendReports = v);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.ForContext(Diagnostics.ErrorReportSink.ReportedProperty, true).Error(e.Exception, "UI 스레드 예외");
        CrashReporting.Record(e.Exception, fatal: false);
        e.Handled = true;
    }

    /// <summary>"버그 신고" 버튼·메뉴: 앱 버전, Windows 버전, 최근 로그를 채운 GitHub 새 이슈 페이지를 연다.</summary>
    public static void OpenBugReport()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CrashReporting.BugReportUrl().AbsoluteUri)
        {
            UseShellExecute = true,
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _stallDetector?.Dispose();
        Log.Information("Foldy 종료 ({Code})", e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
