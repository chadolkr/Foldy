using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Foldy.Core.Mcp;

/// <summary>AI 앱마다 다른 MCP 설정 형식.</summary>
public enum McpConfigFormat
{
    /// <summary>JSON의 "mcpServers" (Claude Desktop, Gemini CLI, Cursor).</summary>
    McpServers,

    /// <summary>JSON의 "mcpServers" + "type": "stdio" (Claude Code의 ~/.claude.json).</summary>
    McpServersStdio,

    /// <summary>JSON의 "servers" + "type": "stdio" (VS Code 사용자 mcp.json).</summary>
    VsCodeServers,

    /// <summary>TOML의 [mcp_servers.foldy] (Codex CLI·Codex 앱의 ~/.codex/config.toml).</summary>
    CodexToml,
}

/// <summary>AI 앱 하나와 그 MCP 설정 파일.</summary>
/// <param name="Name">화면에 보이는 이름.</param>
/// <param name="ConfigPath">설정 파일 경로.</param>
/// <param name="Format">설정 형식.</param>
/// <param name="Installed">이 PC에 깔려 있는 것 같은가 (설정 폴더가 있다).</param>
public sealed record McpClientApp(string Name, string ConfigPath, McpConfigFormat Format, bool Installed);

/// <summary>
/// AI 앱 설정에 Foldy를 넣고 빼기 (사용자 2026-11-21: "사용자가 MCP를 알고 접속 정보를 직접 넣어야 하나?" → 버튼 하나로).
/// 바꾸기 전에 원래 파일을 .foldy-backup으로 남기고, "foldy" 항목 하나만 건드린다(다른 설정은 그대로).
/// 로컬 설정 파일로 MCP를 붙이는 앱만 다룬다. ChatGPT처럼 인터넷(원격) MCP만 받는 앱은 PC 안 Foldy를 밖에 열어야 해서 하지 않는다.
/// </summary>
public static class McpClientConfig
{
    public const string ServerName = "foldy";

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>알려진 AI 앱들. roaming = %APPDATA%, local = %LOCALAPPDATA%, home = 사용자 폴더.</summary>
    public static IReadOnlyList<McpClientApp> Known(string roaming, string local, string home)
    {
        // Claude Desktop: 설치 프로그램판은 %APPDATA%\Claude, Microsoft Store판은 패키지 폴더 안
        var desktopDir = Path.Combine(roaming, "Claude");
        if (!Directory.Exists(desktopDir) && StoreDesktopDir(local) is { } store)
        {
            desktopDir = store;
        }

        bool Dir(params string[] p) => Directory.Exists(Path.Combine(p));
        return
        [
            new("Claude Desktop", Path.Combine(desktopDir, "claude_desktop_config.json"), McpConfigFormat.McpServers, Directory.Exists(desktopDir)),
            new("Claude Code", Path.Combine(home, ".claude.json"), McpConfigFormat.McpServersStdio, File.Exists(Path.Combine(home, ".claude.json")) || Dir(home, ".claude")),
            new("Codex", Path.Combine(home, ".codex", "config.toml"), McpConfigFormat.CodexToml, Dir(home, ".codex")),
            new("Gemini CLI", Path.Combine(home, ".gemini", "settings.json"), McpConfigFormat.McpServers, Dir(home, ".gemini")),
            new("Cursor", Path.Combine(home, ".cursor", "mcp.json"), McpConfigFormat.McpServers, Dir(home, ".cursor")),
            new("VS Code", Path.Combine(roaming, "Code", "User", "mcp.json"), McpConfigFormat.VsCodeServers, Dir(roaming, "Code", "User")),
        ];
    }

    /// <summary>
    /// 연결을 제안할 AI 앱 이름: 이 PC에 깔린 앱이 있고 그중 어느 것도 아직 연결하지 않았을 때 첫 번째. 아니면 null
    /// (하나라도 연결했으면 더 묻지 않는다, 사용자 2026-11-21).
    /// </summary>
    public static string? SuggestTarget(IReadOnlyList<McpClientApp> apps, Func<McpClientApp, bool> connected)
    {
        var installed = apps.Where(a => a.Installed).ToList();
        return installed.Count == 0 || installed.Any(connected) ? null : installed[0].Name;
    }

    private static string? StoreDesktopDir(string local)
    {
        try
        {
            var pk = Path.Combine(local, "Packages");
            return Directory.Exists(pk)
                ? Directory.EnumerateDirectories(pk, "Claude_*").Select(d => Path.Combine(d, "LocalCache", "Roaming", "Claude")).FirstOrDefault(Directory.Exists)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Key(McpConfigFormat f) => f == McpConfigFormat.VsCodeServers ? "servers" : "mcpServers";

    /// <summary>이 앱 설정에 Foldy가 들어 있나.</summary>
    public static bool IsConnected(McpClientApp app)
    {
        try
        {
            if (!File.Exists(app.ConfigPath))
            {
                return false;
            }

            if (app.Format == McpConfigFormat.CodexToml)
            {
                return File.ReadLines(app.ConfigPath).Any(l => IsFoldyHeader(l.Trim()));
            }

            return JsonNode.Parse(File.ReadAllText(app.ConfigPath), documentOptions: Lenient) is JsonObject o
                && o[Key(app.Format)] is JsonObject s && s[ServerName] is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 설정에 Foldy를 넣는다 (이미 있으면 경로만 새로). 파일이 없으면 만든다.
    /// 읽을 수 없는 JSON이면 건드리지 않고 false (사용자가 고친 설정을 망가뜨리지 않게).
    /// </summary>
    public static bool Connect(McpClientApp app, string exePath)
    {
        if (app.Format == McpConfigFormat.CodexToml)
        {
            var lines = RemoveTomlBlock(ReadLines(app.ConfigPath));
            while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            if (lines.Count > 0)
            {
                lines.Add("");
            }

            lines.Add($"[mcp_servers.{ServerName}]");
            lines.Add($"command = {TomlString(exePath)}");
            lines.Add("args = [\"--mcp\"]");
            Save(app.ConfigPath, string.Join('\n', lines) + "\n");
            return true;
        }

        var root = Load(app.ConfigPath);
        if (root is null)
        {
            return false;
        }

        var key = Key(app.Format);
        if (root[key] is not JsonObject servers)
        {
            root[key] = servers = [];
        }

        var entry = new JsonObject();
        if (app.Format != McpConfigFormat.McpServers)
        {
            entry["type"] = "stdio";
        }

        entry["command"] = exePath;
        entry["args"] = new JsonArray("--mcp");
        servers[ServerName] = entry;
        Save(app.ConfigPath, root.ToJsonString(Pretty) + "\n");
        return true;
    }

    /// <summary>설정에서 Foldy만 뺀다.</summary>
    public static bool Disconnect(McpClientApp app)
    {
        if (!File.Exists(app.ConfigPath))
        {
            return true;
        }

        if (app.Format == McpConfigFormat.CodexToml)
        {
            var before = ReadLines(app.ConfigPath);
            var after = RemoveTomlBlock(before);
            if (after.Count != before.Count)
            {
                Save(app.ConfigPath, string.Join('\n', after).TrimEnd('\n') + "\n");
            }

            return true;
        }

        var root = Load(app.ConfigPath);
        if (root is null)
        {
            return false;
        }

        if (root[Key(app.Format)] is JsonObject servers && servers.Remove(ServerName))
        {
            Save(app.ConfigPath, root.ToJsonString(Pretty) + "\n");
        }

        return true;
    }

    private static bool IsFoldyHeader(string trimmed)
    {
        var t = trimmed.Replace(" ", "", StringComparison.Ordinal);
        return t.StartsWith($"[mcp_servers.{ServerName}]", StringComparison.Ordinal) || t.StartsWith($"[mcp_servers.{ServerName}.", StringComparison.Ordinal);
    }

    /// <summary>[mcp_servers.foldy] 와 그 아래 표([mcp_servers.foldy.env] 등)를 다음 다른 표 머리까지 지운다.</summary>
    private static List<string> RemoveTomlBlock(List<string> lines)
    {
        var outp = new List<string>();
        var skipping = false;
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (t.StartsWith('['))
            {
                skipping = IsFoldyHeader(t);
            }

            if (!skipping)
            {
                outp.Add(l);
            }
        }

        return outp;
    }

    /// <summary>TOML 문자열: 작은따옴표(그대로 쓰는 문자열)로, 작은따옴표가 들어 있으면 큰따옴표+이스케이프.</summary>
    private static string TomlString(string s) =>
        !s.Contains('\'', StringComparison.Ordinal) && !s.Contains('\n', StringComparison.Ordinal)
            ? $"'{s}'"
            : "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static List<string> ReadLines(string path) =>
        File.Exists(path) ? [.. File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')] : [];

    private static JsonObject? Load(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Save(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.Copy(path, path + ".foldy-backup", overwrite: true);
        }

        var temp = path + ".foldy-tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>직접 붙일 때 보여 줄 설정 (앱 형식대로).</summary>
    public static string Snippet(McpConfigFormat format, string exePath)
    {
        if (format == McpConfigFormat.CodexToml)
        {
            return $"[mcp_servers.{ServerName}]\ncommand = {TomlString(exePath)}\nargs = [\"--mcp\"]";
        }

        var entry = new JsonObject();
        if (format != McpConfigFormat.McpServers)
        {
            entry["type"] = "stdio";
        }

        entry["command"] = exePath;
        entry["args"] = new JsonArray("--mcp");
        return new JsonObject { [Key(format)] = new JsonObject { [ServerName] = entry } }.ToJsonString(Pretty);
    }
}
