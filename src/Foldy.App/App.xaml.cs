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
    private Foldy.Core.Diagnostics.RunMarker? _runMarker;

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

    /// <summary>
    /// 모든 화면 글을 언어 파일로 옮겼나 (다국어 작업 중, 2026-11-21~). 거짓이면 "Windows 언어 따르기"도 한국어로 시작한다.
    /// 다 옮기고 번역을 검수하면 참으로 바꾼다 (DECISIONS 다국어).
    /// </summary>
    public const bool TranslationComplete = true; // 0.9.43: 화면·설명서·템플릿·문서 그림까지 옮김 → 기본은 Windows 언어를 따른다

    /// <summary>화면 언어를 정한다 (설정 Language, auto면 Windows 언어). 창을 만들기 전에 불러야 XAML 글이 그 언어로 나온다.</summary>
    private static void ApplyLanguage()
    {
        string? setting = null;
        try
        {
            var path = Path.Combine(DataDir, "settings.json");
            if (File.Exists(path))
            {
                setting = Foldy.Presentation.Persistence.JsonFileStore.Load(path, () => new Foldy.Presentation.Persistence.AppSettings()).Language;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }

        // 시험·문서 캡처는 언어를 고정할 수 있다 (FOLDY_LANGUAGE=ko 등). 문서 그림은 한국어판 기본
        if (Environment.GetEnvironmentVariable("FOLDY_LANGUAGE") is { Length: > 0 } forced)
        {
            setting = forced;
        }
        else if (Options.DocShots is not null)
        {
            setting = "ko";
        }
        else if (!TranslationComplete && (string.IsNullOrEmpty(setting) || setting == "auto"))
        {
            setting = "ko"; // 옮기는 중에는 Windows 언어를 따르지 않는다 (반은 영어·반은 한국어 화면을 막는다). 설정에서 고르면 그 언어로
        }

        Foldy.Core.Localization.Loc.Use(setting);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--mcp", StringComparer.OrdinalIgnoreCase))
        {
            // AI 앱이 띄운 MCP 서버: 창·로그인 확인·관리자 해제 없이 stdin/stdout으로만 (docs/mcp.md)
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = Services.McpRunner.Run();
            Shutdown(code);
            return;
        }

        if (e.Args.Contains(Foldy.Core.FileSystem.FileOpWorkerProtocol.Argument, StringComparer.OrdinalIgnoreCase))
        {
            // Foldy가 띄운 휴지통·삭제 도우미: 창 없이 작업 하나만 하고 끝난다 (Services/FileOpWorker.cs)
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(Services.FileOpWorker.Serve());
            return;
        }

        Options = AppOptions.Parse(e.Args);
        ApplyLanguage();
        if (Options.DocShots is not null || Options.IsCapture)
        {
            Foldy.UI.Views.PaneView.FollowCurrentFolderInTree = false; // 문서 그림에 이 PC의 실제 폴더 이름이 찍히지 않게
        }
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

        // 데이터 폴더를 따로 쓰는 시험 실행(UI 자동 테스트 등)은 관리자 러너에서도 같은 프로세스로 둔다 (다시 띄우면 시험이 놓친다)
        if (!Options.IsBench && !Options.IsCapture && Environment.GetEnvironmentVariable("FOLDY_DATA_DIR") is not { Length: > 0 } && Diagnostics.Deelevate.RelaunchIfElevated(e.Args))
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
        Foldy.Presentation.Shell.ShellViewModel.FindLockers = paths => Foldy.Platform.SystemInfo.FileLockers.Describe(
            Foldy.Platform.SystemInfo.FileLockers.WhoUses(paths),
            names => Foldy.Core.Localization.Loc.F("lock.inUse", names),
            (names, n) => Foldy.Core.Localization.Loc.F("lock.more", names, n));
        Foldy.Platform.SystemInfo.FileLockers.ProgramLabel = pid => Foldy.Core.Localization.Loc.F("lock.program", pid);
        Foldy.Platform.Imaging.CloudFileGuard.IsSlowDrivePath = Foldy.Presentation.Services.DriveSpeedTracker.Shared.IsSlow;
        Foldy.Presentation.Services.DriveSpeedTracker.Shared.Eligible = root =>
            root.StartsWith(@"\\", StringComparison.Ordinal)
            || (root.Length >= 2 && root[1] == ':' && Foldy.Platform.SystemInfo.Drives.TypeOf(root[..2] + "\\") == Foldy.Platform.SystemInfo.NativeDriveType.Remote);

        if (normalRun)
        {
            // 지난번에 "Foldy 종료" 없이 꺼졌으면 로그에 남긴다 (네이티브 코드가 프로세스를 끝내면 예외 처리기도 못 부른다)
            _runMarker = Foldy.Core.Diagnostics.RunMarker.Start(DataDir, out var previous);
            if (previous is not null)
            {
                Log.Warning("지난 실행이 정상 종료 없이 끝났습니다. 마지막 기록: {Last}", previous);
            }
        }

        _stallDetector = new UiStallDetector(Dispatcher, TimeSpan.FromMilliseconds(200));
        var settings = Foldy.Presentation.Persistence.AppSettings.Migrate(Foldy.Presentation.Persistence.JsonFileStore.Load(Path.Combine(DataDir, "settings.json"), () => new Foldy.Presentation.Persistence.AppSettings()));
        var theme = Options.ThemeGiven ? Options.Theme : Enum.TryParse<Foldy.UI.Theming.FoldyTheme>(settings.Theme, ignoreCase: true, out var t) ? t : Foldy.UI.Theming.FoldyTheme.System;
        Foldy.UI.Theming.ThemeManager.Initialize(this, theme);
        if (!Options.IsBench && Options.Screenshot is null)
        {
            Diagnostics.IconRefresh.OnceForVersion(DataDir, typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0");
        }
        Foldy.UI.Theming.ThemeManager.SetAccent(settings.Accent);
        Services.OwnerFocus.Register(); // 대화 창·메모를 닫으면 Foldy 창이 뒤로 숨지 않게 (사용자 제보 2026-11-22)

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
        _runMarker?.Stop();
        Log.Information("Foldy 종료 ({Code})", e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
