using Foldy.Presentation.Update;

namespace Foldy.Presentation.Persistence;

/// <summary>settings.json (설계서 7.2). 기본값과 다른 것만 의미가 있고, schemaVersion으로 마이그레이션한다.</summary>
public sealed record AppSettings
{
    public const int CurrentSchema = 2;

    public int SchemaVersion { get; init; } = CurrentSchema;

    /// <summary>system / light / dark / retro</summary>
    public string Theme { get; init; } = "system";

    /// <summary>compact(24) / normal(28) / comfortable(32)</summary>
    public string Density { get; init; } = "normal";

    public bool ShowHidden { get; init; }

    /// <summary>alwaysFoldy(Foldy 뷰어, 기본) / preferAssociated(등록된 앱 우선)</summary>
    public string MarkdownOpen { get; init; } = "alwaysFoldy";

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

    /// <summary>로그·다운로드가 많이 쌓이면 알려 준다 (색인으로 몰래 확인, 주 1회까지, 기본 켬).</summary>
    public bool CleanupNotices { get; init; } = true;

    /// <summary>덮어쓰기 전에 이전 버전을 7일 동안 보관한다 (기본 켬).</summary>
    public bool KeepPreviousVersions { get; init; } = true;

    /// <summary>빠른 검색 단축키를 다른 프로그램을 쓰는 중에도 받는다 (기본 켬). 끄면 Foldy 창 안에서만.</summary>
    public bool QuickSearchGlobal { get; init; } = true;

    /// <summary>AI 연결(MCP): AI 앱이 Foldy로 파일을 찾고 폴더를 볼 수 있게 한다 (기본 끔, "AI에 연결"을 누르면 켜진다). 읽기 전용.</summary>
    public bool McpEnabled { get; init; }

    /// <summary>AI 앱이 있는데 아직 연결 안 했으면 창 위 한 줄로 제안 (기본 켬, [다시 묻지 않기]로 끔).</summary>
    public bool AiSuggest { get; init; } = true;

    /// <summary>바탕화면 파일이 많으면 시작할 때 한 줄로 정리를 제안 (기본 켬, [다시 묻지 않기]로 끔).</summary>
    public bool DesktopSuggest { get; init; } = true;

    /// <summary>[나중에]를 누르면 이때까지 다시 묻지 않는다 (7일 뒤).</summary>
    public DateTime? DesktopSuggestAfterUtc { get; init; }

    /// <summary>다운로드 폴더 제안의 [나중에] (7일 뒤). 켜고 끄기는 <see cref="DesktopSuggest"/>를 함께 쓴다.</summary>
    public DateTime? DownloadsSuggestAfterUtc { get; init; }

    /// <summary>"지워도 되는 폴더 찾기"에 더할 폴더 이름 (쉼표로, 예: "Temp, .cache"). 기본 목록(node_modules 등)에 더해진다.</summary>
    public string RebuildableExtra { get; init; } = "";

    /// <summary>색인이 없는 드라이브(네트워크·회사 드라이브·Google Drive 등)도 천천히 모아 빠른 찾기에 넣는다 (기본 켬).</summary>
    public bool SlowCrawl { get; init; } = true;

    /// <summary>
    /// 화면 전체 크기 (글씨·버튼). 0 = Windows 설정 따르기(기본: 설정 → 접근성 → 텍스트 크기),
    /// 그 밖에는 직접 고른 배율 (1.0, 1.15, 1.3, 1.5). 디스플레이 배율(DPI)은 이와 따로 늘 따라간다.
    /// </summary>
    public double UiScale { get; init; }

    public static AppSettings Migrate(AppSettings s) =>
        s.SchemaVersion switch
        {
            CurrentSchema => s,
            // 2: Markdown은 메모장 대신 Foldy 뷰어로 여는 것이 기본 (사용자 결정 2026-11-19)
            1 => s with { SchemaVersion = CurrentSchema, MarkdownOpen = "alwaysFoldy" },
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
