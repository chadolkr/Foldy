using Foldy.Reporting;
using Serilog.Core;
using Serilog.Events;

namespace Foldy.App.Diagnostics;

/// <summary>
/// 평소에 잡혀서 넘어간 오류(로그 Error 이상 + 예외)도 오류 보고서로 모아 둔다.
/// 같은 오류(종류·메시지·첫 프레임)는 한 실행에 한 번만, 한 실행에 최대 20건.
/// 보내는 것은 <see cref="CrashConsentPrompt"/>가 동의에 따라 한다.
/// 예외 처리기가 직접 기록한 것은 "Reported" 속성으로 건너뛴다.
/// </summary>
internal sealed class ErrorReportSink : ILogEventSink
{
    public const string ReportedProperty = "Reported";
    private const int MaxPerRun = 20;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Error || logEvent.Exception is not { } ex || logEvent.Properties.ContainsKey(ReportedProperty))
        {
            return;
        }

        var frame = ex.StackTrace?.Split('\n', 2)[0].Trim() ?? "";
        var key = $"{ex.GetType().FullName}|{(ex.Message.Length > 120 ? ex.Message[..120] : ex.Message)}|{frame}";
        lock (_gate)
        {
            if (_seen.Count >= MaxPerRun || !_seen.Add(key))
            {
                return;
            }
        }

        CrashReporting.Record(ex, fatal: false);
    }
}
