using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Foldy.Analysis;

namespace Foldy.Presentation.Analysis;

/// <summary>트리맵 칸. ColorIndex: 0~7 = 폴더 (테마 토큰 Brush.Tab0~7), -1 = 파일, -2 = 나머지(기타).</summary>
public sealed record TreemapNode(long Id, bool IsDir, string Name, long Alloc, int ColorIndex, string? Tag, bool IsRemainder = false)
{
    public string SizeText => AnalysisText.FormatSize(Alloc);

    public string ToolTip => Name + "\n" + SizeText + (Tag is null ? "" : $"  [{Tag}]") + (IsDir ? Foldy.Core.Localization.Loc.T("an.clickToOpen") : "");
}

public sealed record BreadcrumbItem(long Id, string Name);

public sealed record FolderListItem(FolderRow Row, long SortValue)
{
    public string Path => Row.Path;

    public string SizeText => AnalysisText.FormatSize(Row.Alloc);

    public string OwnSizeText => AnalysisText.FormatSize(Row.OwnAlloc);

    public string FilesText => Foldy.Core.Localization.Loc.F("an.nFiles", Row.TotalFiles);

    public string OwnFilesText => Foldy.Core.Localization.Loc.F("an.nFiles", Row.OwnFiles);

    public string? Tag => Row.Tag;
}

public sealed record FileListItem(FileRow Row)
{
    public string Path => Row.Path;

    public string Name => Row.Name;

    public string SizeText => AnalysisText.FormatSize(Row.Alloc);

    public string ModifiedText => Row.Mtime > 0 ? AnalysisText.FromFileTime(Row.Mtime).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) : "";
}

/// <param name="Fraction">전체 대비 비율 (막대 길이, 0~1).</param>
public sealed record ExtensionListItem(string Label, long Files, long Alloc, double Fraction, string Detail)
{
    public string SizeText => AnalysisText.FormatSize(Alloc);

    public string FilesText => Foldy.Core.Localization.Loc.F("an.nFiles", Files);

    public double BarWidth => Math.Max(2, Fraction * 160);
}

/// <summary>정리 제안 하나. 휴지통으로 보내는 일은 호스트가 한다 (<see cref="AnalysisTabViewModel.RecycleRequested"/>).</summary>
public sealed partial class CandidateItem(CandidateRow row, string ruleTitle) : ObservableObject
{
    public CandidateRow Row { get; } = row;

    public string Path => Row.Path;

    public string Title { get; } = ruleTitle;

    public string Reason => Row.Reason;

    public bool IsFolder => Row.Kind == "dir";

    public string SizeText => AnalysisText.FormatSize(Row.Reclaim);

    /// <summary>휴지통으로 보내라고 호스트에 요청했고 아직 결과가 오지 않음.</summary>
    [ObservableProperty]
    private bool _isPending;
}

public sealed record GrowthItem(GrowthRow Row)
{
    public string Path => Row.Path;

    public string DeltaText => (Row.Delta >= 0 ? "+" : "") + AnalysisText.FormatSize(Row.Delta);

    public string SizeText => AnalysisText.FormatSize(Row.PreviousAlloc) + " → " + AnalysisText.FormatSize(Row.Alloc);

    public bool IsNew => Row.IsNew;

    public bool Grew => Row.Delta > 0;
}

/// <summary>휴지통으로 보내 달라는 요청. 호스트가 확인 창을 띄우고 휴지통으로 옮긴 뒤 <see cref="AnalysisTabViewModel.MarkRecycled"/>를 부른다.</summary>
public sealed class RecycleRequestEventArgs(IReadOnlyList<CandidateItem> items) : EventArgs
{
    public IReadOnlyList<CandidateItem> Items { get; } = items;

    public IReadOnlyList<string> Paths { get; } = [.. items.Select(i => i.Path)];

    public long TotalBytes { get; } = items.Sum(i => i.Row.Reclaim);
}

public sealed class OpenPathEventArgs(string path, bool isFolder) : EventArgs
{
    public string Path { get; } = path;

    public bool IsFolder { get; } = isFolder;
}
