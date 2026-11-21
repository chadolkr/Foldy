using System.Runtime.InteropServices;

namespace Foldy.Platform.SystemInfo;

/// <summary>
/// Microsoft Store(MSIX) 패키지로 실행 중인지. Store 판은 같은 실행 파일을 MSIX로 감싼 것이라
/// 빌드 설정이 아니라 실행할 때 알아챈다 (docs/store.md): 자체 업데이트는 끄고 Store에 맡긴다.
/// </summary>
public static partial class AppPackage
{
    private const int ErrorInsufficientBuffer = 122;

    private static readonly Lazy<bool> s_isPackaged = new(Detect);

    /// <summary>패키지 ID가 있는 프로세스면 참 (Store·MSIX 설치본).</summary>
    public static bool IsPackaged => s_isPackaged.Value;

    private static bool Detect()
    {
        try
        {
            uint length = 0;
            // 패키지가 없으면 APPMODEL_ERROR_NO_PACKAGE(15700), 있으면 버퍼가 작다는 122를 돌려준다
            return GetCurrentPackageFullName(ref length, IntPtr.Zero) == ErrorInsufficientBuffer;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
