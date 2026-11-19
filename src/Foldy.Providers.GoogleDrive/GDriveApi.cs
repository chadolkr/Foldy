namespace Foldy.Providers.GoogleDrive;

/// <summary>Drive 파일(또는 폴더) 하나. Drive REST v3 files 리소스에서 쓰는 필드만.</summary>
public sealed record GDriveFile(
    string Id,
    string Name,
    string MimeType,
    long Size,
    DateTime ModifiedUtc,
    DateTime CreatedUtc,
    string? WebViewLink = null,
    string? ShortcutTargetId = null,
    string? ShortcutTargetMimeType = null)
{
    public const string FolderMimeType = "application/vnd.google-apps.folder";
    public const string ShortcutMimeType = "application/vnd.google-apps.shortcut";
    public const string GoogleAppsPrefix = "application/vnd.google-apps.";

    /// <summary>바로가기면 대상, 아니면 자신.</summary>
    public string EffectiveId => ShortcutTargetId ?? Id;

    public string EffectiveMimeType => ShortcutTargetMimeType ?? MimeType;

    public bool IsFolder => EffectiveMimeType == FolderMimeType;

    /// <summary>Google 문서·시트·슬라이드처럼 내용을 바로 받을 수 없고 내보내기(export)해야 하는 파일.</summary>
    public bool IsGoogleApp => !IsFolder && EffectiveMimeType.StartsWith(GoogleAppsPrefix, StringComparison.Ordinal);
}

public sealed record GDriveFilePage(IReadOnlyList<GDriveFile> Files, string? NextPageToken);

public sealed record GDriveAbout(string Email, string? DisplayName, long? QuotaLimit, long QuotaUsage);

/// <summary>Drive REST v3 위의 얇은 추상화. 테스트는 가짜 구현을 쓴다. 모든 호출은 스레드 안전해야 한다.</summary>
public interface IGDriveApi
{
    /// <summary>id 대신 "root"를 넘기면 내 드라이브 루트.</summary>
    Task<GDriveFile> GetAsync(string id, CancellationToken ct);

    /// <summary>휴지통에 없는 자식 목록 한 쪽(폴더 먼저, 이름순).</summary>
    Task<GDriveFilePage> ListChildrenAsync(string folderId, string? pageToken, CancellationToken ct);

    /// <summary>이름이 정확히 같은 자식들(휴지통 제외).</summary>
    Task<IReadOnlyList<GDriveFile>> FindChildrenByNameAsync(string folderId, string name, CancellationToken ct);

    /// <summary>내용 내려받기(alt=media). progress는 누적 바이트.</summary>
    Task DownloadAsync(string id, Stream destination, IProgress<long>? progress, CancellationToken ct);

    /// <summary>Google 문서를 다른 형식으로 내보내기.</summary>
    Task ExportAsync(string id, string mimeType, Stream destination, IProgress<long>? progress, CancellationToken ct);

    /// <summary>새 파일 업로드(재개 가능 업로드). progress는 누적 바이트.</summary>
    Task<GDriveFile> UploadAsync(string parentId, string name, Stream content, long length, IProgress<long>? progress, CancellationToken ct);

    Task<GDriveFile> CreateFolderAsync(string parentId, string name, CancellationToken ct);

    /// <summary>이름 바꾸기·휴지통·부모 바꾸기(이동). null인 값은 바꾸지 않는다.</summary>
    Task<GDriveFile> UpdateAsync(string id, GDriveUpdate update, CancellationToken ct);

    /// <summary>파일 복사(폴더는 복사할 수 없다).</summary>
    Task<GDriveFile> CopyAsync(string id, string parentId, string name, CancellationToken ct);

    /// <summary>영구 삭제(휴지통을 거치지 않음).</summary>
    Task DeleteAsync(string id, CancellationToken ct);

    Task<GDriveAbout> AboutAsync(CancellationToken ct);
}

public sealed record GDriveUpdate(string? Name = null, bool? Trashed = null, string? AddParent = null, string? RemoveParent = null);

/// <summary>다시 로그인해야 한다(토큰 취소·만료, 권한 철회).</summary>
public sealed class GDriveAuthException : IOException
{
    public GDriveAuthException(string message, Exception? inner = null) : base(message, inner) { }
}
