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
    private Foldy.Platform.SystemInfo.SingleInstance? _instance;

    /// <summary>화면 크기 배율 (설정 UiScale). 대화 창·메모 창도 같은 배율로 그린다.</summary>
    public static double UiScale { get; set; } = 1.0;

    /// <summary>창 내용에 화면 크기 배율을 적용한다 (글씨가 큰 쪽을 좋아하는 분을 위해).</summary>
    public static void ApplyScale(System.Windows.Window w)
    {
        if (w.Content is System.Windows.FrameworkElement root)
        {
            root.LayoutTransform = UiScale > 1.001 ? new System.Windows.Media.ScaleTransform(UiScale, UiScale) : null;
        }
    }

    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("FOLDY_DATA_DIR") is { Length: > 0 } over // 성능 측정이 사용자 설정·세션을 건드리지 않게 (build/perf-check.ps1)
            ? over
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy");

    public static AppOptions Options { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Options = AppOptions.Parse(e.Args);
        if (Options.DocShots is not null)
        {
            // 문서용 캡처는 항상 모던 라이트. 사용자 설정·세션을 건드리지 않도록 FOLDY_DATA_DIR(임시 폴더)가 꼭 필요하다.
            Options = Options with { Theme = Foldy.UI.Theming.FoldyTheme.Light, ThemeGiven = true };
            if (Environment.GetEnvironmentVariable("FOLDY_DATA_DIR") is not { Length: > 0 } || !DocShotsSandbox.Prepare(Options, DataDir))
            {
                Console.Error.WriteLine("--doc-screenshots 는 FOLDY_DATA_DIR 환경 변수(빈 임시 폴더)가 있어야 합니다.");
                Shutdown(2);
                return;
            }
        }

        if (!Options.IsBench && !Options.IsCapture && Diagnostics.Deelevate.RelaunchIfElevated(e.Args))
        {
            Shutdown(0);
            return;
        }

        if (Environment.GetEnvironmentVariable("FOLDY_SOFTWARE_RENDER") == "1")
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }
        Foldy.UI.Controls.FileListView.LightweightAutomation = !Options.FullAutomation;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(Diagnostics.DiagnosticMode.Level)
            .WriteTo.File(
                Path.Combine(DataDir, "logs", "foldy-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .WriteTo.Sink(new Diagnostics.ErrorReportSink())
            .CreateLogger();
        Log.Information("Foldy {Version} 시작, 인자: {Args}", typeof(App).Assembly.GetName().Version, e.Args);

        // 단일 인스턴스: 이미 Foldy가 떠 있으면 그 창을 앞으로 가져오고(경로 인자는 새 탭), 이 프로세스는 끝낸다.
        // 벤치·스크린샷 같은 시험 실행은 따로 뜬다. 앞의 Foldy가 3초 안에 답하지 않으면 그냥 새로 띄운다.
        var normalRun = !(Options.IsBench || Options.Synthetic || Options.ListWpf || Options.IsCapture);
        if (normalRun)
        {
            // 데이터 폴더를 따로 쓰는 실행(UI 자동 테스트 등)은 평소 쓰는 Foldy와 섞이지 않게 따로 하나로 센다
            var key = Environment.GetEnvironmentVariable("FOLDY_DATA_DIR") is { Length: > 0 } dd
                ? "Foldy-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dd.ToUpperInvariant())))[..12]
                : "Foldy";
            _instance = Foldy.Platform.SystemInfo.SingleInstance.Acquire(key);
            if (!_instance.IsFirst)
            {
                if (_instance.TrySendToFirst(ForwardArgs(e.Args), TimeSpan.FromSeconds(3)))
                {
                    Log.Information("이미 실행 중인 Foldy로 넘기고 끝냅니다");
                    _instance.Dispose();
                    _instance = null;
                    Shutdown(0);
                    return;
                }

                Log.Warning("실행 중인 Foldy가 응답하지 않아 새로 띄웁니다");
            }
        }

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

        // 클라우드 드라이브(G: Google Drive for desktop)는 미리보기를 누를 때만 읽는다 (온라인 전용 파일 자동 다운로드 방지).
        Foldy.Presentation.Preview.PreviewGuards.IsCloudVolumePath = Foldy.Platform.SystemInfo.CloudVolumes.IsCloudPath;
        Foldy.Platform.Imaging.CloudFileGuard.IsSlowDrivePath = Foldy.Presentation.Services.DriveSpeedTracker.Shared.IsSlow;
        Foldy.Presentation.Services.DriveSpeedTracker.Shared.Eligible = root =>
            root.StartsWith(@"\\", StringComparison.Ordinal)
            || (root.Length >= 2 && root[1] == ':' && Foldy.Platform.SystemInfo.Drives.TypeOf(root[..2] + "\\") == Foldy.Platform.SystemInfo.NativeDriveType.Remote);

        _stallDetector = new UiStallDetector(Dispatcher, TimeSpan.FromMilliseconds(200));
        var settings = Foldy.Presentation.Persistence.AppSettings.Migrate(Foldy.Presentation.Persistence.JsonFileStore.Load(Path.Combine(DataDir, "settings.json"), () => new Foldy.Presentation.Persistence.AppSettings()));
        var theme = Options.ThemeGiven ? Options.Theme : Enum.TryParse<Foldy.UI.Theming.FoldyTheme>(settings.Theme, ignoreCase: true, out var t) ? t : Foldy.UI.Theming.FoldyTheme.System;
        Foldy.UI.Theming.ThemeManager.Initialize(this, theme);
        if (!Options.IsBench && Options.Screenshot is null)
        {
            Diagnostics.IconRefresh.OnceForVersion(DataDir, typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0");
        }
        Foldy.UI.Theming.ThemeManager.SetAccent(settings.Accent);

        base.OnStartup(e);
        if (!Options.BenchShell && (Options.IsBench || Options.Synthetic || Options.ListWpf))
        {
            new BenchWindow().Show();
        }
        else
        {
            var main = new ShellWindow();
            main.Show();
            if (Options.BenchShell)
            {
                _ = main.RunBenchAsync(Options);
            }
            _instance?.StartServer(args => Dispatcher.BeginInvoke(() => main.ActivateFromOtherInstance(args)));
            CrashConsentPrompt.ScheduleOnIdle(main, () => main.AutoSendReports, v => main.AutoSendReports = v);
        }
    }

    /// <summary>다른 프로세스로 넘길 인자: 상대 경로는 이 프로세스의 현재 폴더 기준 절대 경로로 바꾼다.</summary>
    private static string[] ForwardArgs(string[] args) =>
        [.. args.Select(a =>
        {
            if (a.StartsWith("--", StringComparison.Ordinal) || a.Contains("://", StringComparison.Ordinal))
            {
                return a;
            }

            try
            {
                return Path.GetFullPath(a);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return a;
            }
        })];

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
        _instance?.Dispose();
        Log.Information("Foldy 종료 ({Code})", e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
