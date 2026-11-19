using Foldy.Presentation.Update;

namespace Foldy.Presentation.Persistence;

/// <summary>settings.json (설계서 7.2). 기본값과 다른 것만 의미가 있고, schemaVersion으로 마이그레이션한다.</summary>
public sealed record AppSettings
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; init; } = CurrentSchema;

    /// <summary>system / light / dark / retro</summary>
    public string Theme { get; init; } = "system";

    /// <summary>compact(24) / normal(28) / comfortable(32)</summary>
    public string Density { get; init; } = "normal";

    public bool ShowHidden { get; init; }

    /// <summary>preferAssociated(등록된 앱 우선) / alwaysFoldy</summary>
    public string MarkdownOpen { get; init; } = "preferAssociated";

    public TerminalSettings Terminal { get; init; } = new();

    public bool Animations { get; init; } = true;

    /// <summary>foldy(Foldy 오렌지, 기본) / windows(Windows 강조색을 따라감). 이전 settings.json에는 없다.</summary>
    public string Accent { get; init; } = "foldy";

    /// <summary>복사 스레드 수. 0이면 자동(드라이브 조합별 튜닝), 1 이상이면 수동 고정.</summary>
    public int CopyThreads { get; init; }

    /// <summary>업데이트 방식(자동 설치/알림만/끔)과 채널(stable/beta).</summary>
    public UpdateSettings Update { get; init; } = UpdateSettings.Default;

    /// <summary>오류 보고서 보내기 동의. 기본은 보내지 않음.</summary>
    public bool SendCrashReports { get; init; }

    /// <summary>진단 모드가 켜진 동안의 끝 시각(UTC). null이면 꺼짐. 켜면 24시간 뒤 저절로 꺼진다.</summary>
    public DateTime? DiagnosticUntil { get; init; }

    /// <summary>Git 저장소 폴더에서 파일 목록 위에 Git 바를 보인다 (기본 켬).</summary>
    public bool ShowGitBar { get; init; } = true;

    /// <summary>Git 저장소 폴더에 들어가면 Git 화면 탭을 옆에 자동으로 연다 (저장소마다 세션당 한 번, 기본 끔).</summary>
    public bool AutoOpenGitView { get; init; }

    public static AppSettings Migrate(AppSettings s) =>
        s.SchemaVersion switch
        {
            CurrentSchema => s,
            _ => s with { SchemaVersion = CurrentSchema },
        };
}

public sealed record TerminalSettings
{
    /// <summary>auto(Windows Terminal이 있으면 사용) / windowsTerminal / console</summary>
    public string Host { get; init; } = "auto";

    /// <summary>pwsh / powershell / cmd</summary>
    public string Shell { get; init; } = "pwsh";

    public string? WtProfile { get; init; }

    public string? WtAdminProfile { get; init; }
}
