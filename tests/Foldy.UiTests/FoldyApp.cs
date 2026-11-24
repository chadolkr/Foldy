using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace Foldy.UiTests;

/// <summary>
/// 시험용 Foldy 한 개: 빈 임시 데이터 폴더(FOLDY_DATA_DIR)와 샘플 폴더로 띄우고, 끝나면 닫고 지운다.
/// 데이터 폴더가 다르면 평소 쓰는 Foldy와 단일 인스턴스가 섞이지 않는다.
/// </summary>
public sealed class FoldyApp : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "foldy-ui-" + Guid.NewGuid().ToString("N"));

    private FoldyApp(bool skipTour, string? openPath, string language)
    {
        Directory.CreateDirectory(_data);
        Sample = Path.Combine(_data, "sample");
        Directory.CreateDirectory(Sample);
        File.WriteAllText(Path.Combine(Sample, "메모.txt"), "시험");
        if (skipTour)
        {
            File.WriteAllText(Path.Combine(_data, "tour-shown.txt"), "always"); // 시험: 짧은 소개를 띄우지 않는다
        }

        var psi = new ProcessStartInfo(Exe!) { UseShellExecute = false };
        psi.Environment["FOLDY_DATA_DIR"] = _data;
        psi.Environment["FOLDY_LANGUAGE"] = language; // 시험은 한국어 화면 기준 (CI는 영어 Windows). 언어별 시험만 다른 언어
        psi.ArgumentList.Add(openPath ?? Sample);
        App = Application.Launch(psi);
        Automation = new UIA3Automation();
        Main = Retry.WhileNull(() => App.GetMainWindow(Automation, TimeSpan.FromSeconds(2)), TimeSpan.FromSeconds(30), ignoreException: true).Result
               ?? throw new InvalidOperationException("Foldy 창이 뜨지 않음");
    }

    public Application App { get; }

    public UIA3Automation Automation { get; }

    public Window Main { get; }

    public string Sample { get; }

    public ConditionFactory By => Automation.ConditionFactory;

    /// <summary>빌드된 Foldy.exe (솔루션 빌드 출력). 없으면 null → 테스트를 건너뛴다.</summary>
    public static string? Exe { get; } = FindExe();

    public static FoldyApp Start(bool skipTour = true, string? openPath = null, string language = "ko")
    {
        Assert.SkipWhen(Exe is null, "Foldy.exe가 없습니다 (dotnet build Foldy.slnx -c Release 먼저)");
        return new FoldyApp(skipTour, openPath, language);
    }

    /// <summary>이름(화면 낭독기 이름)으로 요소를 찾는다. 창 전체에서, 기다리며.</summary>
    public AutomationElement Find(string name, AutomationElement? root = null, double seconds = 10) =>
        Retry.WhileNull(() => (root ?? Main).FindFirstDescendant(By.ByName(name)), TimeSpan.FromSeconds(seconds), ignoreException: true).Result
        ?? throw new InvalidOperationException($"'{name}'을(를) 찾지 못함");

    /// <summary>제목으로 Foldy가 띄운 다른 창(대화 창 등)을 찾는다.</summary>
    public Window FindWindow(string title, double seconds = 15) =>
        Retry.WhileNull(
            () => App.GetAllTopLevelWindows(Automation).FirstOrDefault(w => w.Title == title)
                  ?? Main.FindFirstDescendant(By.ByName(title).And(By.ByControlType(FlaUI.Core.Definitions.ControlType.Window)))?.AsWindow(), // 주 창이 주인인 창은 그 아래에 보인다
            TimeSpan.FromSeconds(seconds), ignoreException: true).Result
        ?? throw new InvalidOperationException($"'{title}' 창이 뜨지 않음");

    public void Dispose()
    {
        try
        {
            App.Close(killIfCloseFails: false);
            Retry.WhileFalse(() => App.HasExited, TimeSpan.FromSeconds(10), ignoreException: true);
            if (!App.HasExited)
            {
                // 진단: 닫아도 끝나지 않으면 Foldy 기록 끝부분을 시험 출력에 남긴다
                Console.WriteLine("Foldy가 닫히지 않음. 기록 끝부분:\n" + LogTail(_data));
                App.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        Automation.Dispose();
        App.Dispose();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(_data, recursive: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>데이터 폴더의 Foldy 기록(logs\foldy-*.log) 끝부분.</summary>
    public static string LogTail(string dataDir, int lines = 40)
    {
        try
        {
            var dir = Path.Combine(dataDir, "logs");
            var log = Directory.Exists(dir) ? Directory.GetFiles(dir, "foldy-*.log").OrderBy(f => f).LastOrDefault() : null;
            if (log is null)
            {
                return "(기록 없음)";
            }

            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return string.Join('\n', r.ReadToEnd().Split('\n').TakeLast(lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "(기록을 못 읽음: " + ex.Message + ")";
        }
    }

    private static string? FindExe()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Foldy.slnx")))
            {
                var exe = Path.Combine(d.FullName, "src", "Foldy.App", "bin", "x64", "Release", "net10.0-windows", "Foldy.exe");
                return File.Exists(exe) ? exe : null;
            }
        }

        return null;
    }
}
