using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foldy.Core.FileSystem;

/// <summary>
/// 도우미 프로세스(Foldy.exe --file-op)에게 넘기는 파일 작업 하나.
/// <paramref name="Kind"/>는 FileOpKind 이름(Recycle·Delete …), <paramref name="Owner"/>는 Foldy 창 핸들(확인·UAC 창의 주인).
/// </summary>
public sealed record FileOpWorkerRequest(string Kind, IReadOnlyList<string> Sources, long Owner);

/// <summary>도우미가 보내는 진행 한 줄.</summary>
public sealed record FileOpWorkerProgress(long Done, long Total, string? Current);

/// <summary>도우미가 마지막에 보내는 결과 한 줄.</summary>
/// <param name="Code">실패했을 때 Windows 오류 코드 (없으면 0).</param>
public sealed record FileOpWorkerResult(bool Succeeded, bool Aborted, string? Error, int Code = 0);

/// <summary>
/// 휴지통·삭제 작업을 Foldy 창과 다른 프로세스에서 돌리기 위한 약속 (1.7.1, 사용자 제보 2026-11-23).
/// Windows 휴지통 작업(IFileOperation)은 탐색기 확장·보안 프로그램 같은 남의 코드를 Foldy 안에서 부른다.
/// 그 코드가 큰 폴더 여러 개를 한꺼번에 지우다 "Abnormal program termination"으로 프로세스를 끝내면
/// Foldy 창까지 같이 꺼졌다. 도우미 프로세스가 대신 하면 도우미만 끝나고, Foldy는 쉬운 말로 알린다.
/// 주고받기는 UTF-8 한 줄씩: Foldy → 도우미 첫 줄은 요청 JSON, 그 뒤 "cancel"이면 그만. 도우미 → Foldy는 "P {진행 JSON}", 마지막 "R {결과 JSON}".
/// </summary>
public static class FileOpWorkerProtocol
{
    /// <summary>Foldy.exe에 주는 인자.</summary>
    public const string Argument = "--file-op";

    /// <summary>Foldy가 도우미에게 그만하라고 보내는 줄.</summary>
    public const string CancelLine = "cancel";

    private const string ProgressPrefix = "P ";
    private const string ResultPrefix = "R ";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>도우미 프로세스에 맡길 작업인가: 폴더가 하나라도 있는 휴지통·삭제 (파일 몇 개는 그 자리에서 빨리).</summary>
    public static bool ShouldIsolate(bool isDeleteOrRecycle, IReadOnlyList<string> sources, Func<string, bool> isDirectory) =>
        isDeleteOrRecycle && sources.Count > 0 && sources.Any(isDirectory);

    public static string WriteRequest(FileOpWorkerRequest request) => JsonSerializer.Serialize(request, Json);

    /// <summary>요청 줄을 읽는다. 모양이 틀리면 null.</summary>
    public static FileOpWorkerRequest? ReadRequest(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var r = JsonSerializer.Deserialize<FileOpWorkerRequest>(line, Json);
            return r is { Kind.Length: > 0, Sources.Count: > 0 } && r.Sources.All(s => !string.IsNullOrEmpty(s)) ? r : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string WriteProgress(FileOpWorkerProgress p) => ProgressPrefix + JsonSerializer.Serialize(p, Json);

    public static string WriteResult(FileOpWorkerResult r) => ResultPrefix + JsonSerializer.Serialize(r, Json);

    /// <summary>도우미가 보낸 한 줄을 읽는다. 진행이면 progress, 결과면 result, 모르는 줄이면 둘 다 null.</summary>
    public static void ReadLine(string? line, out FileOpWorkerProgress? progress, out FileOpWorkerResult? result)
    {
        progress = null;
        result = null;
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        try
        {
            if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            {
                progress = JsonSerializer.Deserialize<FileOpWorkerProgress>(line.AsSpan(ProgressPrefix.Length), Json);
            }
            else if (line.StartsWith(ResultPrefix, StringComparison.Ordinal))
            {
                result = JsonSerializer.Deserialize<FileOpWorkerResult>(line.AsSpan(ResultPrefix.Length), Json);
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>
    /// 도우미가 끝났을 때 결과를 정한다. 결과 줄 없이 끝났으면(도우미가 갑자기 꺼짐) 실패로 보고
    /// <paramref name="crashedMessage"/>를 쓴다. 그만하라고 했는데 결과 없이 끝났으면 취소로 본다.
    /// </summary>
    public static FileOpWorkerResult Finish(FileOpWorkerResult? result, bool cancelRequested, string crashedMessage) =>
        result ?? (cancelRequested ? new FileOpWorkerResult(false, true, null) : new FileOpWorkerResult(false, false, crashedMessage));
}

/// <summary>
/// 진행 보고를 일정 간격으로만 내보낸다. 큰 node_modules는 파일이 수십만 개라 하나마다 알리면
/// 화면 스레드가 밀린다(사용자 로그: 휴지통 작업 중 UI 300~700ms 멈춤 여러 번). 첫 보고와 마지막 보고는 늘 보낸다.
/// </summary>
public sealed class ProgressThrottle(TimeSpan interval, Func<long>? clockMs = null)
{
    private readonly Func<long> _clock = clockMs ?? (() => Environment.TickCount64);
    private long _last = long.MinValue;

    /// <summary>지금 보내도 되면 true (그리고 시각을 기억한다). force면 늘 true.</summary>
    public bool ShouldSend(bool force = false)
    {
        var now = _clock();
        if (force || _last == long.MinValue || now - _last >= (long)interval.TotalMilliseconds)
        {
            _last = now;
            return true;
        }

        return false;
    }
}
