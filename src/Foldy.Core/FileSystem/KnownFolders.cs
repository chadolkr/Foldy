using System.Runtime.InteropServices;

namespace Foldy.Core.FileSystem;

/// <summary>Windows가 정해 둔 사용자 폴더 가운데 Foldy가 왼쪽에 늘 보여 주는 것.</summary>
public enum KnownFolder
{
    Desktop,
    Downloads,
    Documents,
    Pictures,
    Videos,
}

/// <summary>
/// 바탕 화면·다운로드·문서·사진·동영상의 진짜 위치.
/// Windows의 Known Folder(SHGetKnownFolderPath)로 묻는다: OneDrive가 바탕 화면·문서·사진을 옮겼거나(백업 켬)
/// 사용자가 다운로드를 D:로 옮겨도 그 자리를 안다. 사용자 폴더\Downloads처럼 이름을 이어 붙이면 틀린다.
/// 음악은 넣지 않는다(docs/DECISIONS.md 2026-11-26).
/// </summary>
public static class KnownFolders
{
    /// <summary>왼쪽에 보여 주는 순서 (Windows 11 탐색기 홈의 고정 순서와 같게).</summary>
    public static IReadOnlyList<KnownFolder> Shown { get; } =
        [KnownFolder.Desktop, KnownFolder.Downloads, KnownFolder.Documents, KnownFolder.Pictures, KnownFolder.Videos];

    /// <summary>폴더의 위치. 모르면 null.</summary>
    public static string? PathOf(KnownFolder folder)
    {
        if (OperatingSystem.IsWindows() && Ask(Id(folder)) is { Length: > 0 } p)
        {
            return p;
        }

        return Fallback(folder);
    }

    /// <summary>Known Folder를 물을 수 없을 때(아주 드묾) .NET이 아는 위치.</summary>
    internal static string? Fallback(KnownFolder folder)
    {
        var p = folder switch
        {
            KnownFolder.Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            KnownFolder.Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            KnownFolder.Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            KnownFolder.Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            KnownFolder.Downloads => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
                ? Path.Combine(home, "Downloads")
                : null,
            _ => null,
        };
        return string.IsNullOrEmpty(p) ? null : p;
    }

    internal static Guid Id(KnownFolder folder) => folder switch
    {
        KnownFolder.Desktop => new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
        KnownFolder.Downloads => new Guid("374DE290-123F-4565-9164-39C4925E467B"),
        KnownFolder.Documents => new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        KnownFolder.Pictures => new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB"),
        KnownFolder.Videos => new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
        _ => throw new ArgumentOutOfRangeException(nameof(folder)),
    };

    private static string? Ask(Guid id)
    {
        nint buf = 0;
        try
        {
            // KF_FLAG_DONT_VERIFY: 폴더가 지금 없어도(지워짐·OneDrive 아직 안 받음) 만들지 않고 위치만 받는다
            return SHGetKnownFolderPath(id, KfFlagDontVerify, 0, out buf) == 0 ? Marshal.PtrToStringUni(buf) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (buf != 0)
            {
                Marshal.FreeCoTaskMem(buf);
            }
        }
    }

    private const uint KfFlagDontVerify = 0x00004000;

    [DllImport("shell32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, nint token, out nint path);
}
