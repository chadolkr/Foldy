namespace Foldy.Presentation.Preview;

/// <summary>
/// 느린 드라이브·클라우드 보호 판정 (DESIGN.md). 속성과 경로 문자열만 보고, 파일 내용이나 네트워크는 건드리지 않는다.
/// </summary>
public static class PreviewGuards
{
    // Foldy.Platform의 CloudFileGuard와 같은 값 (Presentation은 Platform을 참조하지 않아 상수를 따로 둔다).
    private const int Offline = 0x0000_1000;
    private const int RecallOnOpen = 0x0004_0000;
    private const int RecallOnDataAccess = 0x0040_0000;
    private const int CloudMask = Offline | RecallOnOpen | RecallOnDataAccess;

    /// <summary>내용을 읽으면 다운로드가 시작되는 파일인지 (OFFLINE / RECALL_ON_*).</summary>
    public static bool IsCloudOnly(FileAttributes attributes) => ((int)attributes & CloudMask) != 0;

    /// <summary>클라우드 드라이브 판정 (앱 시작 때 Foldy.Platform의 CloudVolumes.IsCloudPath를 넣는다).</summary>
    public static Func<string, bool>? IsCloudVolumePath { get; set; }

    /// <summary>
    /// UNC(\\서버\...), URI 형태(sftp://), 매핑된 네트워크 드라이브. 드라이브 종류 조회는 디스크·네트워크 I/O 없이
    /// GetDriveType만 부르므로 끊긴 공유에서도 멈추지 않는다.
    /// </summary>
    public static bool IsRemotePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // 클라우드 드라이브(G: Google Drive for desktop)도 네트워크처럼 사용자가 누를 때만 읽는다. 앱이 판정 함수를 넣는다.
        if (IsCloudVolumePath?.Invoke(path) == true)
        {
            return true;
        }

        // 느리다고 학습한 드라이브(회사 보안 드라이브 등)도 네트워크처럼 다룬다.
        if (Foldy.Presentation.Services.DriveSpeedTracker.Shared.IsSlow(path))
        {
            return true;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return true;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || path.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
        {
            try
            {
                return new DriveInfo(path[..1]).DriveType == DriveType.Network;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return false;
    }
}
