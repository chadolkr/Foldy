using Foldy.Core.FileSystem;

namespace Foldy.Presentation.Services;

/// <summary>UI 스레드로 값을 돌려보내는 유일한 길 (설계서 5.1).</summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);
}

/// <summary>테스트·헤드리스용: 호출한 스레드에서 바로 실행.</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();
}

/// <summary>탐색기와 같은 파일 작업 (MVP는 IFileOperation, 나중에 복사·이동만 병렬 엔진으로 교체).</summary>
public interface IFileOperationService
{
    Task<FileOpResult> CopyAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct);

    Task<FileOpResult> MoveAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct);

    Task<FileOpResult> DeleteAsync(IReadOnlyList<string> paths, bool recycle, IProgress<FileOpProgress>? progress, CancellationToken ct);

    Task<FileOpResult> RenameAsync(string path, string newName, CancellationToken ct);

    Task<FileOpResult> NewFolderAsync(string parent, string name, CancellationToken ct);
}

public sealed record FileOpProgress(long Done, long Total, string? CurrentItem);

public sealed record FileOpResult(bool Succeeded, bool Aborted, string? Error, string? CreatedPath = null)
{
    public static FileOpResult Ok(string? created = null) => new(true, false, null, created);

    public static FileOpResult Fail(string error) => new(false, false, error);

    public static readonly FileOpResult Canceled = new(false, true, null);
}

/// <summary>파일 열기: Markdown은 연결 앱이 없으면 내장 뷰어 (설계서 5.9).</summary>
public interface IOpenFileService
{
    /// <summary>true면 내장 Markdown 뷰어로 열어야 한다.</summary>
    Task<bool> ShouldUseBuiltInMarkdownAsync(string path);

    void OpenWithShell(string path);
}

/// <summary>드라이브 목록 (설계서: GetLogicalDrives·GetDriveType로 즉시, 이름·용량은 나중에).</summary>
public interface IDriveService
{
    IReadOnlyList<DriveItem> GetDrivesFast();

    Task<DriveItem> FillDetailsAsync(DriveItem drive, CancellationToken ct);
}

public enum DriveKind : byte { Fixed, Removable, Network, CdRom, Ram, Unknown }

public sealed record DriveItem(string Root, DriveKind Kind, string? Label = null, long? FreeBytes = null, long? TotalBytes = null, bool Offline = false, bool Cloud = false)
{
    public string Letter => Root.TrimEnd('\\');

    public string Display => string.IsNullOrEmpty(Label) ? Letter : $"{Label} ({Letter})";

    /// <summary>사용한 비율 0~1. 용량을 모르면 0 (사이드바 막대).</summary>
    public double UsedFraction => TotalBytes is > 0 && FreeBytes is { } f ? Math.Clamp(1 - (double)f / TotalBytes.Value, 0, 1) : 0;

    /// <summary>90% 넘게 찼다 (막대를 경고색으로).</summary>
    public bool IsAlmostFull => UsedFraction >= 0.9;

    public bool HasSpace => TotalBytes is > 0 && FreeBytes is not null;

    /// <summary>"120 GB 남음 / 476 GB". 용량을 모르면 빈 문자열.</summary>
    public string SpaceText => HasSpace ? Foldy.Core.Localization.Loc.F("drive.space", Files.FileItem.FormatBytes(FreeBytes!.Value), Files.FileItem.FormatBytes(TotalBytes!.Value)) : Offline ? Foldy.Core.Localization.Loc.T("drive.offline") : "";

    /// <summary>이 드라이브의 빠른 찾기(색인) 상태 한 줄 ("빠른 찾기 ✓", "빠른 찾기 준비 중 62%", "빠른 찾기 없음 · 폴더 안에서만"). 모르면 null.</summary>
    public string? IndexText { get; init; }

    public bool HasIndexText => !string.IsNullOrEmpty(IndexText);

    /// <summary>화면 낭독기가 읽는 이름 (사이드바 드라이브 항목): 레코드 기본 ToString은 속성을 모두 읽어 버린다.</summary>
    public override string ToString() => string.Join(" · ", new[] { Display, SpaceText, IndexText }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>터미널 열기 (설계서: 현재 패널 경로에서 시작, 관리자는 UAC 경유).</summary>
public interface ITerminalService
{
    void Open(string folder, TerminalKind kind, bool elevated);
}

public enum TerminalKind { Default, Cmd, PowerShell }

/// <summary>목록 표시 문자열(종류 이름 등)을 셸에서 가져오는 훅.</summary>
public interface IFileTypeNames
{
    string GetTypeName(FsEntry entry);
}

/// <summary>파일이 많은 곳을 지우기 전에 사용자에게 묻는 내용.</summary>
public sealed record BigDeleteQuestion(IReadOnlyList<string> Paths, Foldy.Core.FileSystem.DeleteCount Count, bool CanFast);

/// <summary>휴지통으로(되살릴 수 있음, 기본) · 빨리 지우기(되살릴 수 없음) · 그만두기.</summary>
public enum BigDeleteChoice { Recycle, Fast, Cancel }
