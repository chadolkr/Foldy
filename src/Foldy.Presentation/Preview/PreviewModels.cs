using Foldy.Core.FileSystem;
using Foldy.Presentation.Files;

namespace Foldy.Presentation.Preview;

/// <summary>미리보기 창이 보여 줄 내용의 종류 (설계서 9장: 텍스트, 이미지, Windows 미리보기 처리기).</summary>
public enum PreviewKind
{
    None,
    Text,
    Markdown,
    Image,
    Shell,
}

/// <summary>미리보기 창의 상태. 상태 문구는 <see cref="PreviewMessages"/>.</summary>
public enum PreviewStatus
{
    /// <summary>선택한 파일 없음.</summary>
    Empty,
    Loading,
    Ready,
    NotPreviewable,
    /// <summary>클라우드 전용 파일. 내용을 읽으면 다운로드가 시작되므로 읽지 않는다.</summary>
    CloudOnly,
    /// <summary>네트워크 드라이브·원격. 사용자가 [미리보기]를 누를 때만 읽는다.</summary>
    NetworkDrive,
    TooLarge,
    Failed,
}

/// <summary>상태 줄에 쓰는 문구 (DESIGN.md 느린 드라이브 규칙).</summary>
public static class PreviewMessages
{
    public const string NotPreviewable = "미리 볼 수 없음";
    public const string CloudOnly = "클라우드 파일 — 내려받지 않음";
    public const string NetworkDrive = "네트워크 드라이브 — [미리보기]";
    public const string Loading = "불러오는 중…";
    public const string Failed = "미리 볼 수 없음 (처리기 오류)";
    public const string Folder = "폴더";

    public static string TooLarge(long bytes, long limit) =>
        $"파일이 커서 미리 볼 수 없음 ({FileItem.FormatBytes(bytes)} > {FileItem.FormatBytes(limit)})";
}

/// <summary>
/// 미리보기 대상 파일 하나. 호스트가 이미 가진 목록 항목(속성 포함)에서 만들므로
/// 선택이 바뀔 때 디스크를 다시 읽지 않는다.
/// </summary>
/// <param name="IsRemote">네트워크 드라이브(UNC·매핑), SFTP 등 원격 여부.</param>
/// <param name="UserForced">사용자가 [미리보기]를 눌러 느린 드라이브 보호를 풀었다.</param>
public sealed record PreviewRequest(
    string Path,
    string Name,
    long Length,
    DateTime ModifiedUtc,
    FileAttributes Attributes,
    bool IsRemote = false,
    bool UserForced = false)
{
    public string Extension => System.IO.Path.GetExtension(Name);

    public bool IsFolder => (Attributes & FileAttributes.Directory) != 0;

    /// <summary>목록 항목에서. 네트워크 판정은 경로로 한다 (<see cref="PreviewGuards.IsRemotePath"/>).</summary>
    public static PreviewRequest From(FileItem item, Func<string, bool>? isRemote = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var e = item.Entry;
        return new PreviewRequest(
            e.Path.Value,
            e.Name,
            e.Size,
            e.Modified,
            e.Attributes | (item.IsFolder ? FileAttributes.Directory : 0),
            e.Path.Provider != ProviderId.Local || (isRemote ?? PreviewGuards.IsRemotePath)(e.Path.Value));
    }
}

/// <summary>공급자가 준비한 내용. 종류별 하위 형식을 UI가 골라 그린다.</summary>
public abstract record PreviewContent(PreviewKind Kind);

/// <summary>텍스트: 앞부분(기본 1MB)만 읽은 문자열.</summary>
public sealed record TextPreviewContent(string Text, string EncodingName, bool Truncated, long TotalBytes, string? Language)
    : PreviewContent(PreviewKind.Text);

/// <summary>Markdown·이미지·셸 처리기: 뷰가 경로로 직접 읽는다.</summary>
/// <param name="AllowContentRead">false면 내용을 읽지 않고 셸 캐시(썸네일)만 쓴다 (클라우드 전용 파일).</param>
public sealed record FilePreviewContent(PreviewKind Kind, PreviewRequest Request, bool AllowContentRead)
    : PreviewContent(Kind);

/// <summary>공급자가 이 파일은 미리 볼 수 없다고 판단 (바이너리, 처리기 없음 등).</summary>
public sealed record UnavailableContent(string Reason) : PreviewContent(PreviewKind.None);
