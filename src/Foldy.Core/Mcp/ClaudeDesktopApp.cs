using System.Text.RegularExpressions;

namespace Foldy.Core.Mcp;

/// <summary>
/// Claude Desktop 프로그램 알아보기와 다시 켜는 방법 (사용자 2026-11-22: 연결한 뒤 Claude를 완전히 껐다 켜야 Foldy가 보인다).
/// Claude Desktop은 설정을 켤 때만 읽고, 창을 닫아도 알림 영역에 남아 있어서 "다시 시작" 단추가 필요하다.
/// </summary>
public static partial class ClaudeDesktopApp
{
    /// <summary>
    /// 이 실행 파일이 Claude Desktop인가. 같은 이름(claude.exe)인 Claude Code 명령줄은 아니라고 본다.
    /// Store판은 WindowsApps\…Claude_…\, 설치 프로그램판은 %LOCALAPPDATA%\AnthropicClaude\ 아래에 있다.
    /// </summary>
    public static bool IsDesktopExe(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.GetFileName(path).Equals("claude.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (PackageRoot(path) is { } root)
        {
            return Path.GetFileName(root).Contains("Claude_", StringComparison.OrdinalIgnoreCase);
        }

        return path.Contains($"{Path.DirectorySeparatorChar}AnthropicClaude{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Store(MSIX) 패키지 폴더 (WindowsApps 바로 아래). 패키지가 아니면 null.</summary>
    public static string? PackageRoot(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath);
        while (dir is not null)
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent is not null && Path.GetFileName(parent).Equals("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                return dir;
            }

            dir = parent;
        }

        return null;
    }

    /// <summary>
    /// 다시 켤 때 여는 것: Store판은 "shell:AppsFolder\패키지계열!앱ID"(explorer로 연다), 설치 프로그램판은 실행 파일.
    /// 패키지 폴더 이름은 "이름_버전_구조_리소스_게시자ID"이고, 패키지 계열 이름은 "이름_게시자ID"다.
    /// </summary>
    /// <param name="exePath">실행 중인 Claude.exe.</param>
    /// <param name="manifest">패키지의 AppxManifest.xml 내용 (앱 ID를 읽는다). 없으면 "Claude".</param>
    public static string LaunchTarget(string exePath, string? manifest)
    {
        if (PackageRoot(exePath) is not { } root)
        {
            // 설치 프로그램판: AnthropicClaude\app-x.y.z\claude.exe 위의 claude.exe가 늘 최신판을 띄운다
            var appDir = Path.GetDirectoryName(exePath);
            var stub = appDir is null ? null : Path.Combine(Path.GetDirectoryName(appDir) ?? appDir, "claude.exe");
            return Path.GetFileName(appDir ?? "").StartsWith("app-", StringComparison.OrdinalIgnoreCase) && stub is not null && File.Exists(stub) ? stub : exePath;
        }

        var parts = Path.GetFileName(root).Split('_');
        var family = parts.Length >= 2 ? parts[0] + "_" + parts[^1] : Path.GetFileName(root);
        var id = manifest is not null && AppId().Match(manifest) is { Success: true } m ? m.Groups[1].Value : "Claude";
        return $"shell:AppsFolder\\{family}!{id}";
    }

    [GeneratedRegex("<Application\\s+Id=\"([^\"]+)\"")]
    private static partial Regex AppId();
}
