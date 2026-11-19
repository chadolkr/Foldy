using System.Globalization;

namespace Foldy.App;

/// <summary>
/// 명령줄 옵션. 벤치 모드는 결과를 JSON으로 남기고 종료한다 (CI·야간 측정용).
/// <code>
/// Foldy.exe --bench-list [--rows 100000] [--step 120] [--out result.json]
/// Foldy.exe --bench-folder &lt;폴더&gt; [--out result.json]
/// </code>
/// </summary>
public sealed record AppOptions
{
    public bool BenchList { get; init; }

    /// <summary>진짜 ShellWindow(사이드바·트리·Git 바 포함)에서 시작 시간·폴더 열기·스크롤·메모리를 재고 종료한다 (build/perf-check.ps1).</summary>
    public bool BenchShell { get; init; }
    public string? BenchFolder { get; init; }
    public int Rows { get; init; } = 100_000;
    public int Step { get; init; } = 120;
    public string? Out { get; init; }
    public string? Font { get; init; }
    public bool FullAutomation { get; init; }
    public bool ListWpf { get; init; }
    public bool ThemeGiven { get; init; }
    public bool Synthetic { get; init; }
    public string? Screenshot { get; init; }

    /// <summary>문서용 화면 캡처 모드: 장면마다 PNG를 이 폴더에 저장하고 종료한다 (ShellWindow.DocShots.cs).</summary>
    public string? DocShots { get; init; }

    /// <summary>문서용 샘플 폴더 위치 (기본 %TEMP%\FoldyDemo). 사용자 이름이 경로에 보이지 않게 C:\FoldyDemo 같은 곳을 줄 수 있다.</summary>
    public string? DocRoot { get; init; }

    /// <summary>화면 캡처(한 장 또는 문서용 여러 장) 실행: 업데이트 확인·단일 인스턴스·재실행을 하지 않는다.</summary>
    public bool IsCapture => Screenshot is not null || DocShots is not null;

    /// <summary>명령줄로 받은 폴더(또는 .md 파일): 활성 패널에서 연다 ("Foldy.exe C:\경로").</summary>
    public string? OpenPath { get; init; }
    public Foldy.UI.Theming.FoldyTheme Theme { get; init; } = Foldy.UI.Theming.FoldyTheme.System;

    public bool IsBench => BenchList || BenchShell || BenchFolder is not null;

    public static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            o = args[i] switch
            {
                "--bench-list" => o with { BenchList = true },
                "--bench-shell" => o with { BenchShell = true },
                "--bench-folder" => o with { BenchFolder = Next() },
                "--rows" => o with { Rows = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--step" => o with { Step = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--out" => o with { Out = Next() },
                "--font" => o with { Font = Next() },
                "--full-automation" => o with { FullAutomation = true },
                "--list-wpf" => o with { ListWpf = true },
                "--synthetic" => o with { Synthetic = true },
                "--screenshot" => o with { Screenshot = Next() },
                "--doc-screenshots" => o with { DocShots = Next() },
                "--doc-root" => o with { DocRoot = Next() },
                "--theme" => o with { Theme = Enum.Parse<Foldy.UI.Theming.FoldyTheme>(Next(), ignoreCase: true), ThemeGiven = true },
                var a when !a.StartsWith("--", StringComparison.Ordinal) => o with { OpenPath = a },
                _ => o,
            };
        }

        return o;
    }
}
