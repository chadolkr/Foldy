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

    public string Name => Path.GetFileName(Destination);
}

public sealed class OrganizePlan
{
    public required string Root { get; init; }

    public List<PlannedMove> Moves { get; } = [];

    public List<SkippedEntry> Skipped { get; } = [];

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

public sealed class Sha256ContentHasher : IContentHasher
{
    public static readonly Sha256ContentHasher Instance = new();

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
