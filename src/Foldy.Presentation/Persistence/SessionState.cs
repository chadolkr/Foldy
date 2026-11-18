using Foldy.Presentation.Files;
using Foldy.Presentation.Panes;
using Foldy.Presentation.Tabs;

namespace Foldy.Presentation.Persistence;

/// <summary>session.json: 탭·경로·정렬·창 위치 (설계서 7.1, 7.2). 모두 불변 레코드.</summary>
public sealed record SessionState
{
    public int SchemaVersion { get; init; } = 1;

    public PaneState Left { get; init; } = new();

    public PaneState Right { get; init; } = new();

    public PaneSide Active { get; init; } = PaneSide.Left;

    public WindowPlacement? Window { get; init; }

    /// <summary>왼쪽 패널 너비 비율 (0.2~0.8)</summary>
    public double SplitRatio { get; init; } = 0.5;
}

public sealed record PaneState
{
    public IReadOnlyList<TabState> Tabs { get; init; } = [];

    public int ActiveIndex { get; init; }
}

public sealed record TabState
{
    public TabKind Kind { get; init; } = TabKind.Folder;

    public string Path { get; init; } = "";

    public IReadOnlyList<string> Back { get; init; } = [];

    public IReadOnlyList<string> Forward { get; init; } = [];

    public SortKey SortKey { get; init; }

    public bool SortDescending { get; init; }

    public string? Focus { get; init; }

    public int Color { get; init; }
}

public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);
