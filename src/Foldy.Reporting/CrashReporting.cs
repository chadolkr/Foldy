using System.Reflection;

namespace Foldy.Reporting;

/// <summary>
/// 앱이 쓰는 입구. App.xaml.cs에서:
///   시작 시  <c>CrashReporting.Initialize(DataDir, typeof(App).Assembly)</c>
///   예외 처리기에서  <c>CrashReporting.Record(ex, fatal)</c>
///   창이 뜬 뒤  대기 중 리포트가 있으면 동의를 묻고 <c>SendPendingAsync()</c>
///   버그 신고 버튼  <c>CrashReporting.BugReportUrl()</c>
/// </summary>
public static class CrashReporting
{
    private static CrashStore? s_store;
    private static CrashReportBuilder? s_builder;
    private static AppEnvironment? s_env;
    private static string s_logDir = "";

    public static void Initialize(string dataDirectory, Assembly appAssembly)
    {
        s_env = AppEnvironment.Current(appAssembly);
        s_builder = new CrashReportBuilder(s_env, ReportScrubber.ForCurrentUser);
        s_store = new CrashStore(Path.Combine(dataDirectory, "crashes"));
        s_logDir = Path.Combine(dataDirectory, "logs");
    }

    /// <summary>크래시 전송이 켜진 빌드인지 (릴레이 주소가 심겨 있는지).</summary>
    public static bool UploadEnabled => ReportingConfig.CrashRelayUrl is not null;

    /// <summary>예외 처리기에서 부른다. 던지지 않는다.</summary>
    public static void Record(Exception? ex, bool fatal)
    {
        if (ex is null || s_store is null || s_builder is null) return;
        try
        {
            s_store.TrySave(s_builder.Build(ex, LogTail.Read(s_logDir, maxLines: 30)), fatal);
        }
#pragma warning disable CA1031 // 크래시 경로
        catch { }
#pragma warning restore CA1031
    }

    public static IReadOnlyList<StoredCrash> Pending() => s_store?.Pending() ?? [];

    /// <summary>사용자가 거절했을 때: 대기 중 리포트를 지운다.</summary>
    public static void DiscardPending() => s_store?.Clear();

    /// <summary>
    /// 동의 후 호출. 보낸 것과 서버가 거부한 것은 지우고, 네트워크 실패는 남겨 다음에 다시 묻는다.
    /// </summary>
    public static async Task<IReadOnlyList<SendResult>> SendPendingAsync(CancellationToken ct = default)
    {
        if (s_store is null || ReportingConfig.CrashRelayUrl is not { } url) return [];
        using var sender = new CrashSender(url);
        var results = new List<SendResult>();
        foreach (var crash in s_store.Pending())
        {
            var r = await sender.SendAsync(crash.Report, ct).ConfigureAwait(false);
            results.Add(r);
            if (r.Outcome is SendOutcome.Sent or SendOutcome.Rejected) s_store.Remove(crash);
            else break; // 네트워크가 안 되면 나머지도 안 된다
        }
        return results;
    }

    /// <summary>"버그 신고" 버튼이 여는 주소.</summary>
    public static Uri BugReportUrl() =>
        BugReportLink.Create(ReportingConfig.RepositoryUrl,
            s_env ?? AppEnvironment.Current(Assembly.GetEntryAssembly() ?? typeof(CrashReporting).Assembly),
            LogTail.Read(s_logDir, maxLines: 40),
            ReportScrubber.ForCurrentUser);
}
