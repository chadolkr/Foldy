namespace Foldy.Platform.Imaging;

/// <summary>
/// 클라우드 전용(다운로드 안 된) 파일 판정 (설계서 5.12 "느린 드라이브와 클라우드").
/// 이런 파일은 내용을 열면 OneDrive 등이 다운로드를 시작하므로, Windows가 가진 썸네일만 쓴다.
/// 속성 읽기(GetFileAttributesEx)는 다운로드를 일으키지 않는다.
/// </summary>
public static class CloudFileGuard
{
    /// <summary>FILE_ATTRIBUTE_OFFLINE</summary>
    public const int Offline = 0x0000_1000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN</summary>
    public const int RecallOnOpen = 0x0004_0000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</summary>
    public const int RecallOnDataAccess = 0x0040_0000;

    private const int Mask = Offline | RecallOnOpen | RecallOnDataAccess;

    /// <summary>이 속성의 파일은 내용을 읽으면 안 되는지.</summary>
    public static bool MustNotReadContent(FileAttributes attributes) => ((int)attributes & Mask) != 0;

    /// <summary>
    /// 경로의 속성을 읽어 판정한다. 속성을 못 읽으면(없는 파일, 권한) 내용도 못 읽으므로 true.
    /// </summary>
    public static bool MustNotReadContent(string path)
    {
        try
        {
            return MustNotReadContent(File.GetAttributes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }
}
