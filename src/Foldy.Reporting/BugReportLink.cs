using System.Text;

namespace Foldy.Reporting;

/// <summary>
/// "버그 신고" 버튼: 앱 버전·Windows 버전·최근 로그를 채운 GitHub 새 이슈 주소.
/// 토큰이 필요 없고, 사용자가 브라우저에서 내용을 보고 직접 제출한다 (브라우저가 프록시를 처리).
/// </summary>
public static class BugReportLink
{
    /// <summary>GitHub·브라우저가 받는 주소 길이에 여유를 둔 상한.</summary>
    public const int MaxUrlLength = 7500;

    public static Uri Create(string repositoryUrl, AppEnvironment env, string? logTail, ReportScrubber scrubber)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(scrubber);
        var prefix = $"{repositoryUrl.TrimEnd('/')}/issues/new?labels=bug&title={Uri.EscapeDataString("[버그] ")}&body=";
        var log = scrubber.Scrub(logTail).TrimEnd();

        // 로그는 최근 줄부터 남기고, 주소가 길면 오래된 줄을 버린다
        var lines = log.Length == 0 ? [] : log.Split('\n');
        for (var skip = 0; ; skip++)
        {
            var body = Body(env, string.Join('\n', lines.Skip(skip)), skip > 0);
            var url = prefix + Uri.EscapeDataString(body);
            if (url.Length <= MaxUrlLength || skip >= lines.Length) return new Uri(url);
        }
    }

    private static string Body(AppEnvironment env, string log, bool trimmed)
    {
        var sb = new StringBuilder();
        sb.Append("### 무슨 일이 있었나요?\n\n\n\n");
        sb.Append("### 재현 방법\n\n1. \n2. \n3. \n\n");
        sb.Append("### 기대한 동작\n\n\n\n");
        sb.Append("### 환경\n\n");
        sb.Append("- Foldy: ").Append(env.AppVersion).Append('\n');
        sb.Append("- Windows: ").Append(env.OsVersion).Append('\n');
        sb.Append("- 런타임: ").Append(env.Runtime).Append('\n');
        if (log.Length > 0)
        {
            sb.Append("\n<details><summary>최근 로그").Append(trimmed ? " (일부)" : "").Append("</summary>\n\n```text\n");
            sb.Append(log.Replace("```", "ʼʼʼ", StringComparison.Ordinal)).Append("\n```\n\n</details>\n");
        }
        sb.Append("\n> 로그의 경로·사용자 이름은 지웠습니다. 올리기 전에 한 번 더 확인해 주세요.\n");
        return sb.ToString();
    }
}
