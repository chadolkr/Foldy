namespace Foldy.Core.FileSystem;

/// <summary>Windows 파일 작업 오류를 사람이 알아들을 묶음으로 나눈 것.</summary>
public enum FileOpErrorKind { Other, Cancelled, AccessDenied, InUse, PathTooLong }

/// <summary>
/// IFileOperation 오류 코드(HRESULT) 나누기 (1.7.2, 사용자 제보 2026-11-23: 브라우저 캐시를 휴지통으로 보내다
/// Windows "폴더 액세스가 거부됨" 창이 떴다). Foldy는 Windows 오류 창을 띄우지 않고(FOF_NOERRORUI) 이 묶음으로 쉬운 말을 고른다.
/// </summary>
public static class FileOpErrors
{
    public static FileOpErrorKind Classify(int hr) => unchecked((uint)hr) switch
    {
        0x800704C7 or 0x80270000 or 0x80270001 => FileOpErrorKind.Cancelled, // ERROR_CANCELLED, COPYENGINE_E_USER_CANCELLED, COPYENGINE_E_CANCELLED
        0x80070005 or 0x80270021 or 0x80270022 or 0x80270002 => FileOpErrorKind.AccessDenied, // E_ACCESSDENIED, COPYENGINE_E_ACCESS_DENIED_SRC/DEST, REQUIRES_ELEVATION
        0x80070020 or 0x80070021 or 0x80270027 or 0x80270028 => FileOpErrorKind.InUse, // SHARING/LOCK_VIOLATION, COPYENGINE_E_SHARING_VIOLATION_SRC/DEST
        0x800700CE or 0x8007006F or 0x80270023 or 0x80270024 or 0x80270034 => FileOpErrorKind.PathTooLong, // FILENAME_EXCED_RANGE, BUFFER_OVERFLOW, PATH_TOO_DEEP_SRC/DEST, RECYCLE_PATH_TOO_LONG
        _ => FileOpErrorKind.Other,
    };
}

/// <summary>정리 제안의 브라우저 캐시 폴더가 어느 브라우저 것인지 (켜져 있으면 캐시 파일을 쓰고 있다).</summary>
public static class BrowserCache
{
    /// <summary>Chrome·Edge 사용자 데이터 안이면 그 이름, 아니면 null.</summary>
    public static string? BrowserName(string path) =>
        path.Contains(@"\Google\Chrome\User Data\", StringComparison.OrdinalIgnoreCase) ? "Chrome"
        : path.Contains(@"\Microsoft\Edge\User Data\", StringComparison.OrdinalIgnoreCase) ? "Edge"
        : null;
}
