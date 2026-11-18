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
