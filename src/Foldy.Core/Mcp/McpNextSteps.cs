using Foldy.Core.Localization;

namespace Foldy.Core.Mcp;

/// <summary>
/// AI 앱에 연결한 뒤 할 일, 앱마다 (사용자 2026-11-22: "연결하는 이런 내용을 안내해 줘야겠구나").
/// AI 앱은 MCP 설정을 켤 때나 새 대화를 열 때만 읽어서, 연결만 하고 그대로 쓰면 Foldy가 안 보인다.
/// </summary>
public static class McpNextSteps
{
    /// <summary>앱 이름(<see cref="McpClientApp.Name"/>)에 맞는 단계들. 모르는 앱은 공통 단계.</summary>
    public static IReadOnlyList<string> For(string appName) => appName switch
    {
        "Claude Desktop" => [Loc.T("mcp.next.claude.1"), Loc.T("mcp.next.claude.2"), Loc.T("mcp.next.claude.3"), Loc.T("mcp.next.claude.4")],
        "Claude Code" => [Loc.T("mcp.next.code.1"), Loc.T("mcp.next.code.2"), Loc.T("mcp.next.code.3")],
        "Codex" => [Loc.T("mcp.next.codex.1"), Loc.T("mcp.next.codex.2")],
        "Gemini CLI" => [Loc.T("mcp.next.gemini.1"), Loc.T("mcp.next.gemini.2")],
        "Cursor" => [Loc.T("mcp.next.cursor.1"), Loc.T("mcp.next.cursor.2"), Loc.T("mcp.next.cursor.3")],
        "VS Code" => [Loc.T("mcp.next.vscode.1"), Loc.T("mcp.next.vscode.2")],
        "Antigravity" => [Loc.T("mcp.next.antigravity.1"), Loc.T("mcp.next.antigravity.2")],
        _ => [Loc.T("mcp.next.other.1")],
    };

    /// <summary>[Claude 다시 시작] 단추를 보일 앱인가.</summary>
    public static bool CanRestart(string appName) => appName == "Claude Desktop";

    /// <summary>처음 해 볼 질문.</summary>
    public static string FirstAsk => Loc.T("mcp.next.ask");
}
