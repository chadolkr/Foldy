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
    public string? BenchFolder { get; init; }
    public int Rows { get; init; } = 100_000;
    public int Step { get; init; } = 120;
    public string? Out { get; init; }
    public string? Font { get; init; }
    public bool FullAutomation { get; init; }
    public bool ListWpf { get; init; }
    public bool Synthetic { get; init; }
    public string? Screenshot { get; init; }
    public Foldy.UI.Theming.FoldyTheme Theme { get; init; } = Foldy.UI.Theming.FoldyTheme.System;

    public bool IsBench => BenchList || BenchFolder is not null;

    public static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            o = args[i] switch
            {
                "--bench-list" => o with { BenchList = true },
                "--bench-folder" => o with { BenchFolder = Next() },
                "--rows" => o with { Rows = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--step" => o with { Step = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--out" => o with { Out = Next() },
                "--font" => o with { Font = Next() },
                "--full-automation" => o with { FullAutomation = true },
                "--list-wpf" => o with { ListWpf = true },
                "--synthetic" => o with { Synthetic = true },
                "--screenshot" => o with { Screenshot = Next() },
                "--theme" => o with { Theme = Enum.Parse<Foldy.UI.Theming.FoldyTheme>(Next(), ignoreCase: true) },
                _ => o,
            };
        }

        return o;
    }
}
