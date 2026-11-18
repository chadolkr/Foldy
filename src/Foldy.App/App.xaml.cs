using System.IO;
using System.Windows;
using System.Windows.Threading;
using Foldy.App.Diagnostics;
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
            .CreateLogger();
        Log.Information("Foldy {Version} 시작, 인자: {Args}", typeof(App).Assembly.GetName().Version, e.Args);

        // 전역 예외: 로그를 남기고 앱은 살린다 (탭 단위 복구는 M1).
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log.Error(ev.Exception, "관찰되지 않은 작업 예외");
            ev.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            Log.Fatal(ev.ExceptionObject as Exception, "처리되지 않은 예외 (종료됨: {Terminating})", ev.IsTerminating);

        _stallDetector = new UiStallDetector(Dispatcher, TimeSpan.FromMilliseconds(200));
        var settings = Foldy.Presentation.Persistence.AppSettings.Migrate(Foldy.Presentation.Persistence.JsonFileStore.Load(Path.Combine(DataDir, "settings.json"), () => new Foldy.Presentation.Persistence.AppSettings()));
        var theme = Options.ThemeGiven ? Options.Theme : Enum.TryParse<Foldy.UI.Theming.FoldyTheme>(settings.Theme, ignoreCase: true, out var t) ? t : Foldy.UI.Theming.FoldyTheme.System;
        Foldy.UI.Theming.ThemeManager.Initialize(this, theme);

        base.OnStartup(e);
        if (Options.IsBench || Options.Synthetic || Options.ListWpf)
        {
            new BenchWindow().Show();
        }
        else
        {
            new ShellWindow().Show();
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI 스레드 예외");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _stallDetector?.Dispose();
        Log.Information("Foldy 종료 ({Code})", e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
