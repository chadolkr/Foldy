namespace Foldy.Core.Templates;

/// <summary>
/// 폴더 템플릿 (설계서 5.10, <c>*.foldy-template.json</c>).
/// 트리의 name에는 <c>/</c>로 여러 단계를 한 번에 적을 수 있고({{고객}}/{{date:yyyy-MM-dd}}_{{프로젝트}}),
/// 변수는 <c>{{key}}</c>와 <c>{{date:형식}}</c>을 쓴다.
/// </summary>
public sealed class FolderTemplate
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>파일 이름·배지·머리말 template 항목에 쓰는 짧은 영문 id (예: para, client-project).</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>경로바 배지에 보이는 짧은 이름 (없으면 Name).</summary>
    public string? Badge { get; set; }

    /// <summary>
    /// 목록 묶음 이름 (개인·업무, 개발, 미디어·창작, 기록·보관 ...). 선택 항목이라 schemaVersion은 그대로 1이고,
    /// 없으면 목록 맨 끝의 "내 템플릿"(또는 "기타")에 들어간다.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>위저드 추천용 용도 태그 (personal, work, dev, media ...).</summary>
    public IList<string> Purposes { get; set; } = [];

    public IList<TemplateVariable> Variables { get; set; } = [];

    public IList<TemplateNode> Tree { get; set; } = [];

    /// <summary>사용법 안내문 (Markdown). 내장 템플릿은 &lt;id&gt;.guide.md에서 채운다.</summary>
    public string? Guide { get; set; }

    /// <summary>파일명 규칙 (강제하지 않고 제안만).</summary>
    public IList<NamingRule> Naming { get; set; } = [];

    /// <summary>위저드 완료 화면의 "이렇게 시작하세요" 첫 할 일.</summary>
    public IList<string> FirstSteps { get; set; } = [];

    /// <summary>폴더 정리(5.11) "스타일대로" 방식의 분류 규칙.</summary>
    public IList<TemplateOrganizeRule> Organize { get; set; } = [];

    /// <summary>내장 템플릿 여부 (파일에는 쓰지 않는다).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBuiltIn { get; set; }

    /// <summary>불러온 파일 경로 (내 템플릿만).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SourcePath { get; set; }

    /// <summary>목록에 보일 묶음 이름 (비어 있으면 내장은 "기타", 내 템플릿은 "내 템플릿").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayCategory => string.IsNullOrWhiteSpace(Category) ? (IsBuiltIn ? "기타" : TemplateCategories.User) : Category!.Trim();

    public string DisplayBadge => string.IsNullOrWhiteSpace(Badge) ? Name : Badge!;
}

public sealed class TemplateVariable
{
    public string Key { get; set; } = "";

    public string? Label { get; set; }

    public bool Required { get; set; }

    public string? Default { get; set; }

    /// <summary>선택지가 있으면 드롭다운으로 보여준다 (예: 개발 언어).</summary>
    public IList<TemplateChoice> Choices { get; set; } = [];
}

public sealed class TemplateChoice
{
    public string Value { get; set; } = "";

    public string? Label { get; set; }
}

public sealed class TemplateNode
{
    /// <summary>이름 (변수 포함 가능, <c>/</c>로 여러 단계).</summary>
    public string Name { get; set; } = "";

    /// <summary>폴더 이름 영문판 (위저드 "폴더 이름 한글/영문"). 없으면 Name.</summary>
    public string? NameEn { get; set; }

    /// <summary>파일 내용. 값이 있으면 파일, 없으면 폴더 (빈 파일은 빈 문자열).</summary>
    public string? File { get; set; }

    /// <summary>조건 (예: <c>language=dotnet</c>, 여러 값은 <c>language=node|python</c>). 맞지 않으면 만들지 않는다.</summary>
    public string? When { get; set; }

    /// <summary>미리보기에서 처음부터 체크 해제해 둘지.</summary>
    public bool Optional { get; set; }

    public IList<TemplateNode> Children { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFile => File is not null;
}

public sealed class NamingRule
{
    /// <summary>적용할 폴더(템플릿 기준 상대 경로, 변수 포함 가능). 없으면 전체.</summary>
    public string? Folder { get; set; }

    public string Pattern { get; set; } = "";

    public IList<string> Examples { get; set; } = [];

    /// <summary>파일 이름 검사용 정규식 (선택).</summary>
    public string? Regex { get; set; }
}

/// <summary>"스타일대로" 정리 규칙: 조건이 맞으면 Target(템플릿 기준 상대 경로)으로.</summary>
public sealed class TemplateOrganizeRule
{
    public string Target { get; set; } = "";

    public string? Reason { get; set; }

    /// <summary>파일명에 들어 있으면 맞음 (대소문자 무시, 하나라도).</summary>
    public IList<string> Keywords { get; set; } = [];

    /// <summary>확장자 (점 포함, 예: .pdf).</summary>
    public IList<string> Extensions { get; set; } = [];

    /// <summary>종류 그룹 (문서, 이미지 ...).</summary>
    public IList<string> Groups { get; set; } = [];

    /// <summary>마지막으로 연(접근) 날짜가 이 일수보다 오래됐으면 맞음.</summary>
    public int? NotOpenedForDays { get; set; }
}

/// <summary>템플릿 묶음의 표시 순서.</summary>
public static class TemplateCategories
{
    public const string User = "내 템플릿";

    public static readonly IReadOnlyList<string> Order = ["개인·업무", "개발", "미디어·창작", "기록·보관"];

    /// <summary>정해진 묶음은 Order 순서, 그 밖의 묶음은 그 뒤(이름순), "내 템플릿"은 맨 끝.</summary>
    public static int OrderOf(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        for (var i = 0; i < Order.Count; i++)
        {
            if (string.Equals(Order[i], category, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return string.Equals(category, User, StringComparison.OrdinalIgnoreCase) ? 1000 : 100;
    }
}
