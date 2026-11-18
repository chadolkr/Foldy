using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Foldy.Reporting;

/// <summary>예외 → 스크럽된 <see cref="CrashReport"/>.</summary>
public sealed class CrashReportBuilder(AppEnvironment env, ReportScrubber scrubber)
{
    public const int MaxFrames = 40;
    public const int MaxDetails = 28_000;

    public CrashReport Build(Exception ex, string? logTail = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var root = Innermost(ex);
        return new CrashReport
        {
            AppVersion = env.AppVersion,
            OsVersion = env.OsVersion,
            Runtime = env.Runtime,
            // 래퍼(TargetInvocationException 등)보다 실제 원인이 중복 판정에 맞다
            ExceptionType = root.GetType().FullName ?? root.GetType().Name,
            Message = Truncate(scrubber.Scrub(root.Message), 2000),
            Details = Truncate(scrubber.Scrub(ex.ToString()), MaxDetails),
            Frames = Frames(root).Select(scrubber.Scrub).ToArray(),
            Log = string.IsNullOrEmpty(logTail) ? null : scrubber.Scrub(logTail),
        };
    }

    /// <summary>TargetInvocationException·AggregateException(단일) 껍질을 벗긴다.</summary>
    internal static Exception Innermost(Exception ex)
    {
        while (true)
        {
            switch (ex)
            {
                case TargetInvocationException { InnerException: { } inner }:
                    ex = inner; continue;
                case AggregateException agg when agg.InnerExceptions.Count == 1:
                    ex = agg.InnerExceptions[0]; continue;
                case TypeInitializationException { InnerException: { } inner }:
                    ex = inner; continue;
                default:
                    return ex;
            }
        }
    }

    /// <summary>"Namespace.Type.Method" 프레임. 줄 번호·오프셋은 넣지 않는다 (서명은 빌드 간에 같아야 함).</summary>
    internal static IEnumerable<string> Frames(Exception ex)
    {
        var frames = new StackTrace(ex, fNeedFileInfo: false).GetFrames();
        var result = new List<string>();
        foreach (var f in frames)
        {
            var m = f.GetMethod();
            if (m is null) continue;
            var type = m.DeclaringType?.FullName ?? "?";
            result.Add($"{type}.{m.Name}");
            if (result.Count >= MaxFrames) break;
        }
        // 트림·AOT 등으로 메서드 정보가 없으면 텍스트 스택에서 뽑는다
        if (result.Count == 0 && ex.StackTrace is { } text)
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim();
                if (!t.StartsWith("at ", StringComparison.Ordinal)) continue;
                var inIdx = t.IndexOf(" in ", StringComparison.Ordinal);
                var paren = t.IndexOf('(', StringComparison.Ordinal);
                var end = paren > 0 ? paren : inIdx > 0 ? inIdx : t.Length;
                result.Add(t[3..end]);
                if (result.Count >= MaxFrames) break;
            }
        return result;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : new StringBuilder(s, 0, max - 20, max).Append("\n…(잘림)").ToString();
}
