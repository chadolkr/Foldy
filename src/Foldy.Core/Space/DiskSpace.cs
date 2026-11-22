namespace Foldy.Core.Space;

/// <summary>작업을 시작하면 끝까지 할 수 없는 이유.</summary>
public enum SpaceProblem
{
    /// <summary>문제 없음 (또는 드라이브를 알 수 없어 막지 않음).</summary>
    None,

    /// <summary>드라이브의 빈 공간이 모자람.</summary>
    NotEnoughSpace,

    /// <summary>FAT32 드라이브라 4GB가 넘는 파일 하나를 담을 수 없음.</summary>
    Fat32TooBig,
}

/// <summary>
/// 시작 전에 본 결과. <see cref="Need"/>는 필요한 바이트(어림, <see cref="SpaceProblem.Fat32TooBig"/>면 가장 큰 파일 크기),
/// <see cref="Free"/>는 남은 바이트(모르면 -1), <see cref="Root"/>는 그 드라이브 루트(예: <c>D:\</c>, 모르면 null),
/// <see cref="Server"/>는 SFTP 서버라면 그 이름 (그때는 <see cref="Root"/>가 null이고 [공간 늘리기]를 보여 주지 않는다).
/// </summary>
public sealed record SpaceCheck(SpaceProblem Problem, long Need, long Free, string? Root, string? Server = null)
{
    public bool Ok => Problem == SpaceProblem.None;

    /// <summary>드라이브 이름 (예: <c>D:</c>). 모르면 빈 문자열.</summary>
    public string DriveName => Root is null ? "" : Root.TrimEnd('\\', '/');
}

/// <summary>
/// 공간을 쓰는 작업(복사·이동·금고·압축·꺼내기·되살리기 …)을 <b>시작하기 전에</b> 대상 드라이브를 본다
/// (사용자 원칙 2026-11-22: "한참 작업하다가 공간이 부족해요라고 하면 사용자가 화날 거다", docs/DESIGN-PRINCIPLES.md).
/// 실제 한도는 둘이다: (1) 대상 드라이브의 빈 공간, (2) FAT32 드라이브(오래된 USB 등)는 파일 하나가 4GB-1바이트까지.
/// 네트워크 위치처럼 알 수 없으면 막지 않는다.
/// </summary>
public static class DiskSpace
{
    /// <summary>FAT32 파일 하나의 최대 크기 (4GB - 1바이트).</summary>
    public const long Fat32MaxFile = 0xFFFF_FFFFL;

    /// <summary>이 크기 이상이면 시작 전에 필요한 공간을 한 줄로 알려 준다 (작은 일은 금방 끝나므로 말하지 않는다).</summary>
    public const long MentionFrom = 100L << 20;

    /// <summary>목록·폴더 기록 등 여유.</summary>
    public const long Slack = 1L << 20;

    /// <summary>파일이 실제로 차지하는 단위 (NTFS 기본 클러스터). 작은 파일이 많을 때 모자라게 어림하지 않도록 올림한다.</summary>
    public const long Cluster = 4096;

    /// <summary>드라이브 정보: 남은 바이트, 파일 시스템 이름(NTFS, exFAT, FAT32 …), 루트.</summary>
    public readonly record struct DriveRoom(long Free, string Format, string Root);

    /// <summary>시험용: 드라이브 정보를 바꾼다.</summary>
    internal static Func<string, DriveRoom?>? ProbeOverride { get; set; }

    /// <summary>디스크에서 차지하는 크기 (클러스터 단위 올림).</summary>
    public static long OnDisk(long size) => size <= 0 ? 0 : (size + Cluster - 1) / Cluster * Cluster;

    /// <summary>그 폴더가 있는 드라이브의 빈 공간과 형식. 폴더가 아직 없어도 드라이브가 있으면 안다. 알 수 없으면 null.</summary>
    public static DriveRoom? Probe(string folder)
    {
        if (ProbeOverride is { } o)
        {
            return o(folder);
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            var d = new DriveInfo(root);
            return d.IsReady ? new DriveRoom(d.AvailableFreeSpace, d.DriveFormat ?? "", d.RootDirectory.FullName) : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary><paramref name="folder"/>에 <paramref name="need"/>바이트를 쓸 수 있나. <paramref name="biggestFile"/>은 FAT32 한도를 볼 가장 큰 파일.</summary>
    public static SpaceCheck Check(string folder, long need, long biggestFile = 0)
    {
        need = Math.Max(0, need);
        if (Probe(folder) is not { } d)
        {
            return new SpaceCheck(SpaceProblem.None, need, -1, null); // 알 수 없으면 막지 않는다
        }

        if (IsFat(d.Format) && biggestFile > Fat32MaxFile)
        {
            return new SpaceCheck(SpaceProblem.Fat32TooBig, biggestFile, d.Free, d.Root);
        }

        return new SpaceCheck(need > d.Free ? SpaceProblem.NotEnoughSpace : SpaceProblem.None, need, d.Free, d.Root);
    }

    /// <summary>이 크기의 파일들을 <paramref name="folder"/>에 새로 쓸 수 있나 (꺼내기·내려받기·되살리기).</summary>
    public static SpaceCheck CheckFiles(string folder, IEnumerable<long> sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        long total = 0, biggest = 0;
        foreach (var s in sizes)
        {
            total += OnDisk(s);
            biggest = Math.Max(biggest, s);
        }

        return Check(folder, total + Slack, biggest);
    }

    /// <summary>
    /// 금고·잠긴 파일을 만들 때 필요한 공간 어림. 압축이 안 되는 내용(사진·동영상)이 최악이므로 원래 크기에
    /// 파일마다 머리글·암호화 값, 목록, 여유를 더한다. 원본은 확인이 끝날 때까지 그대로 두므로 원본 크기만큼 더 든다.
    /// </summary>
    public static long VaultEstimate(long totalBytes, int files) =>
        Math.Max(0, totalBytes) + (Math.Max(0, totalBytes) / 1000) + (Math.Max(0, files) * 1024L) + Slack;

    /// <summary>FAT 계열(FAT, FAT16, FAT32). exFAT는 큰 파일을 담을 수 있으므로 아니다.</summary>
    public static bool IsFat(string? format) =>
        format is not null && format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase);
}
