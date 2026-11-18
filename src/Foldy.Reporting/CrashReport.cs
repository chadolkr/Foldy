using System.Text.Json.Serialization;

namespace Foldy.Reporting;

/// <summary>
/// 크래시 리포트 스키마 v1. 릴레이 쪽 정의와 같아야 한다: tools/crash-relay/src/report.ts.
/// 모든 문자열은 만들 때 <see cref="ReportScrubber"/>로 이미 스크럽된 상태다.
/// </summary>
public sealed record CrashReport
{
    public int Schema { get; init; } = 1;
    public required string AppVersion { get; init; }
    public required string OsVersion { get; init; }
    public required string Runtime { get; init; }
    public required string ExceptionType { get; init; }
    public required string Message { get; init; }
    /// <summary>스크럽된 Exception.ToString() (내부 예외 포함).</summary>
    public required string Details { get; init; }
    /// <summary>"Namespace.Type.Method" 형태의 프레임. 릴레이가 중복 판정 서명을 계산한다.</summary>
    public required IReadOnlyList<string> Frames { get; init; }
    public string? Log { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CrashReport))]
[JsonSerializable(typeof(StoredCrash))]
[JsonSerializable(typeof(RelayResponse))]
internal sealed partial class ReportJson : JsonSerializerContext;

/// <summary>
/// 디스크에 저장된 크래시. 전송 본문(<see cref="CrashReport"/>)에 로컬 메타데이터를 더한다.
/// Fatal: 프로세스가 끝난 크래시인지, 살아난 UI 예외인지.
/// </summary>
public sealed record StoredCrash(CrashReport Report, bool Fatal, DateTimeOffset OccurredAt)
{
    [JsonIgnore] public string FilePath { get; init; } = "";
}

/// <summary>릴레이 응답 (tools/crash-relay/src/index.ts RelayResult).</summary>
public sealed record RelayResponse(string Status, string Signature, int Issue, string Url, int Occurrences);
