using Foldy.Core.Templates;

namespace Foldy.Core.Organize;

public enum OrganizeMode
{
    /// <summary>확장자 그룹별.</summary>
    ByType,

    /// <summary>수정 날짜(사진·영상은 촬영 시각)별.</summary>
    ByDate,

    /// <summary>템플릿의 분류 규칙대로.</summary>
    ByTemplate,

    /// <summary>사용자가 만든 "조건 → 폴더" 목록.</summary>
    Custom,
}

/// <summary>스캔한 항목 하나 (맨 위 단계 파일·폴더, 옵션이면 하위 폴더 파일까지).</summary>
public sealed record OrganizeEntry
{
    public required string FullPath { get; init; }

    public required string Name { get; init; }

    public bool IsFolder { get; init; }

    /// <summary>.git, .sln, package.json 등이 있는 폴더 (통째로만 다룬다).</summary>
    public bool IsProjectFolder { get; init; }

    public long Size { get; init; }

    public DateTime LastWriteUtc { get; init; }

    public DateTime LastAccessUtc { get; init; }

    public FileAttributes Attributes { get; init; }

    /// <summary>클라우드 전용(내용이 로컬에 없음): 내용을 읽지 않는다 (해시·EXIF 안 함).</summary>
    public bool IsCloudOnly => (Attributes & (FileAttributes.Offline | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */ | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */)) != 0;
}

public sealed record SkippedEntry(string FullPath, string Reason);

/// <summary>스캔 결과 (계획의 입력).</summary>
public sealed class OrganizeScan
{
    public required string Root { get; init; }

    public List<OrganizeEntry> Entries { get; } = [];

    public List<SkippedEntry> Skipped { get; } = [];
}

/// <summary>직접 규칙: 조건(이름 패턴·키워드·종류) → 폴더.</summary>
public sealed class CustomRule
{
    public string Name { get; set; } = "";

    /// <summary>파일 이름 와일드카드 (예: *.pdf). 비면 모두.</summary>
    public string? Pattern { get; set; }

    /// <summary>이름에 들어 있어야 하는 말 (하나라도, 대소문자 무시).</summary>
    public IList<string> Keywords { get; set; } = [];

    public IList<string> Groups { get; set; } = [];

    /// <summary>대상 폴더 (정리할 폴더 기준 상대 경로, / 또는 \).</summary>
    public string Target { get; set; } = "";

    /// <summary>폴더에도 적용 (프로젝트 폴더는 통째로 옮김).</summary>
    public bool ApplyToFolders { get; set; }
}

public sealed class OrganizeOptions
{
    /// <summary>예전(한국어) 이름. 정리할 폴더에 이미 있으면 그 폴더를 계속 쓴다 (FolderNames.Pick).</summary>
    public const string UnclassifiedFolderKo = "_분류 필요";
    public const string DuplicatesFolderKo = "_중복 후보";

    /// <summary>지금 앱 언어로 만드는 이름.</summary>
    public static string UnclassifiedFolder => Foldy.Core.Localization.Loc.T("folder.unclassified");
    public static string DuplicatesFolder => Foldy.Core.Localization.Loc.T("folder.duplicates");

    public OrganizeMode Mode { get; init; } = OrganizeMode.ByType;

    /// <summary>날짜별 하위 폴더 형식 (/는 단계 구분).</summary>
    public string DateFormat { get; init; } = "yyyy/yyyy-MM";

    public FolderTemplate? Template { get; init; }

    public IReadOnlyList<CustomRule> Rules { get; init; } = [];

    /// <summary>이름·크기·내용 해시가 같은 파일을 _중복 후보로 모은다 (지우지 않음).</summary>
    public bool DetectDuplicates { get; init; } = true;

    /// <summary>이름 규칙 맞추기: 날짜로 시작하지 않는 파일 이름 앞에 수정 날짜(yyyy-MM-dd_)를 붙인다.</summary>
    public bool PrefixDate { get; init; }

    /// <summary>템플릿 규칙의 "N일 넘게 안 연 파일" 판단 기준 시각.</summary>
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>계획된 이동 하나. 미리보기에서 체크 해제(Included=false)하거나 대상 폴더를 바꿀 수 있다.</summary>
public sealed class PlannedMove
{
    public required string Source { get; init; }

    public required string Destination { get; set; }

    /// <summary>정리할 폴더 기준 대상 폴더 상대 경로 (예: 문서, 2026\2026-11, _분류 필요).</summary>
    public required string TargetFolder { get; set; }

    public bool IsFolder { get; init; }

    public required string Reason { get; set; }

    public bool Included { get; set; } = true;

    public long Size { get; init; }

    /// <summary>
    /// 계획을 만들 때 본 원본의 수정 시각 (파일만). [정리하기] 직전에 크기와 함께 다시 보고, 다르면 그사이 바뀐 것이라 옮기지 않는다.
    /// 모르면 null (크기만 본다).
    /// </summary>
    public DateTime? SourceLastWriteUtc { get; init; }

    public string Name => Path.GetFileName(Destination);

    /// <summary>같은 파일 묶음에 든 파일이면 그 묶음 (미리보기에서 남길 파일을 바꿀 때 쓴다).</summary>
    public DuplicateSet? DuplicateSet { get; set; }
}

/// <summary>
/// 이름·크기·내용이 같은 파일 묶음. 하나(<see cref="Keeper"/>)만 원래 규칙대로 두고 나머지는 _중복 후보로 옮긴다.
/// 미리보기에서 어느 것을 남길지 바꿀 수 있다 (<see cref="OrganizePlanner.ChooseKeeper"/>). 아무것도 지우지 않는다.
/// </summary>
public sealed class DuplicateSet
{
    /// <summary>오래된 것부터 (처음 남길 파일이 맨 앞).</summary>
    public List<DuplicateMember> Members { get; } = [];

    public required DuplicateMember Keeper { get; set; }

    /// <summary>나머지를 모으는 폴더 (정리할 폴더 기준 상대 경로, 예: _중복 후보).</summary>
    public required string TargetFolder { get; init; }

    public string Name => Path.GetFileName(Keeper.FullPath);

    public long Size => Keeper.Size;
}

/// <summary>같은 파일 묶음의 파일 하나와, 이것을 남길 때 갈 곳.</summary>
public sealed class DuplicateMember
{
    public required string FullPath { get; init; }

    public long Size { get; init; }

    public DateTime LastWriteUtc { get; init; }

    /// <summary>남길 때 갈 폴더 (정리할 폴더 기준 상대 경로, 원래 규칙대로).</summary>
    public string KeepTarget { get; set; } = "";

    public string KeepReason { get; set; } = "";

    /// <summary>옮긴 뒤 이름 (이름 앞 날짜 붙이기 반영).</summary>
    public string NewName { get; set; } = "";
}

/// <summary>남길 파일을 바꾼 결과: 새로 생긴 이동, 없어진 이동(제자리에 남게 됨), 바뀐 이동.</summary>
public sealed record KeeperChange(IReadOnlyList<PlannedMove> Added, IReadOnlyList<PlannedMove> Removed, IReadOnlyList<PlannedMove> Changed);

public sealed class OrganizePlan
{
    public required string Root { get; init; }

    public List<PlannedMove> Moves { get; } = [];

    public List<SkippedEntry> Skipped { get; } = [];

    /// <summary>같은 파일 묶음 (중복 후보가 있을 때).</summary>
    public List<DuplicateSet> DuplicateSets { get; } = [];

    /// <summary>계획 자체를 막는 이유 (시스템 폴더 등). 있으면 실행하지 않는다.</summary>
    public string? Blocked { get; set; }

    public IEnumerable<PlannedMove> IncludedMoves => Moves.Where(m => m.Included);

    /// <summary>폴더별 옮겨질 개수 (미리보기 요약).</summary>
    public IReadOnlyList<(string Folder, int Count)> CountsByFolder() =>
        IncludedMoves.GroupBy(m => m.TargetFolder, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count()))
            .OrderBy(x => x.Key, StringComparer.CurrentCulture)
            .ToList();
}

public enum OrganizePlanStage { Scanning, Duplicates, Planning }

/// <summary>계획을 만드는 동안 어느 단계에서 몇 개를 했는지 (여러 스레드가 더하고, 화면이 가끔 읽는다).</summary>
public sealed class OrganizePlanProgress
{
    private int _stage;
    private long _done;
    private long _total;

    public OrganizePlanStage Stage => (OrganizePlanStage)Volatile.Read(ref _stage);

    public long Done => Interlocked.Read(ref _done);

    public long Total => Interlocked.Read(ref _total);

    public void Start(OrganizePlanStage stage, long total)
    {
        Interlocked.Exchange(ref _total, total);
        Interlocked.Exchange(ref _done, 0);
        Volatile.Write(ref _stage, (int)stage);
    }

    public void Step(long n = 1) => Interlocked.Add(ref _done, n);
}

/// <summary>
/// "이 경로가 이미 있나?"를 폴더 목록 한 번으로 답한다 (파일마다 디스크에 묻지 않는다).
/// 정리 결과 폴더는 몇 개뿐이라 그 폴더들만 한 번씩 읽는다. 계획을 만드는 동안만 쓴다.
/// </summary>
public sealed class PathExistsCache
{
    private readonly Dictionary<string, HashSet<string>?> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public bool Exists(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var dir = Path.GetDirectoryName(path);
        if (dir is null)
        {
            return Directory.Exists(path);
        }

        HashSet<string>? names;
        lock (_gate)
        {
            if (!_dirs.TryGetValue(dir, out names))
            {
                names = Load(dir);
                _dirs[dir] = names;
            }
        }

        return names?.Contains(Path.GetFileName(path)) ?? false;
    }

    private static HashSet<string>? Load(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return null;
            }

            var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };
            return new HashSet<string>(new System.IO.Enumeration.FileSystemEnumerable<string>(dir, (ref System.IO.Enumeration.FileSystemEntry e) => e.FileName.ToString(), opts), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>사진·영상 촬영 시각(EXIF) 훅. Foldy.Platform이 WIC/셸 속성으로 구현한다.</summary>
public interface IMediaDateProvider
{
    DateTime? GetTakenDate(string path);
}

/// <summary>중복 판단용 내용 해시 (기본은 SHA-256 스트리밍).</summary>
public interface IContentHasher
{
    string? Hash(string path);
}

/// <summary>빠른 걸러 내기용: 파일 앞·뒤 조각만 읽은 해시. 다르면 내용도 다르다 (같으면 끝까지 해시해 확인).</summary>
public interface IPartialContentHasher
{
    string? HashEnds(string path);
}

public sealed class Sha256ContentHasher : IContentHasher, IPartialContentHasher
{
    public static readonly Sha256ContentHasher Instance = new();

    /// <summary>앞·뒤에서 읽는 조각 크기.</summary>
    public const int EndChunk = 64 * 1024;

    public string? HashEnds(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
            var len = fs.Length;
            if (len <= EndChunk * 2)
            {
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)); // 작은 파일은 통째로
            }

            var buf = new byte[EndChunk * 2];
            fs.ReadExactly(buf, 0, EndChunk);
            fs.Seek(-EndChunk, SeekOrigin.End);
            fs.ReadExactly(buf, EndChunk, EndChunk);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buf));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? Hash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
