using System.Collections.Concurrent;

namespace Foldy.Platform.SystemInfo;

/// <summary>
/// 클라우드 드라이브(Google Drive for desktop의 G: 등) 판별 (DESIGN.md 드라이브 분류 "Cloud": 라벨·파일시스템명 추정).
/// Drive for desktop의 가상 드라이브는 파일에 OFFLINE/RECALL 속성을 붙이지 않을 수 있어, 속성만 보면
/// 썸네일·아이콘·EXIF를 만들다 온라인 전용 파일을 내려받게 된다. 그래서 볼륨 자체로 막는다.
/// 드라이브 문자별로 한 번만 GetVolumeInformation을 부르고 기억한다.
/// </summary>
public static class CloudVolumes
{
    private static readonly string[] Labels = ["Google Drive", "GoogleDrive", "내 드라이브"];
    private static readonly string[] FileSystems = ["GoogleDriveFS", "DriveFS"];
    private static readonly ConcurrentDictionary<char, bool> Cache = new();

    /// <summary>볼륨 라벨·파일 시스템 이름으로 판정한다 (순수 함수).</summary>
    public static bool IsCloud(string? label, string? fileSystem)
    {
        if (!string.IsNullOrEmpty(fileSystem) && FileSystems.Any(f => fileSystem.Equals(f, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !string.IsNullOrEmpty(label) && Labels.Any(l => label.Contains(l, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>드라이브 목록을 새로 읽을 때 결과를 기억한다 (드라이브 문자가 바뀌어도 맞게).</summary>
    public static bool Remember(string root, string? label, string? fileSystem)
    {
        var cloud = IsCloud(label, fileSystem);
        if (TryLetter(root, out var c))
        {
            Cache[c] = cloud;
        }

        return cloud;
    }

    /// <summary>
    /// 경로가 클라우드 드라이브에 있는지. 처음 보는 고정 드라이브는 볼륨 정보를 한 번 읽는다(파일 내용은 안 읽음).
    /// 네트워크·이동식·CD 드라이브와 UNC는 읽지 않고 false.
    /// </summary>
    public static bool IsCloudPath(string? path)
    {
        if (path is null || !TryLetter(path, out var c))
        {
            return false;
        }

        if (Cache.TryGetValue(c, out var known))
        {
            return known;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var root = $"{c}:\\";
        if (Drives.TypeOf(root) != NativeDriveType.Fixed)
        {
            Cache[c] = false;
            return false;
        }

        var d = Drives.Details(root);
        return Remember(root, d.Label, d.FileSystem);
    }

    private static bool TryLetter(string path, out char letter)
    {
        letter = default;
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
        {
            return false;
        }

        letter = char.ToUpperInvariant(path[0]);
        return true;
    }
}
