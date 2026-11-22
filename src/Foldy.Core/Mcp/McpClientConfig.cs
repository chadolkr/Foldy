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
public sealed record McpClientApp(string Name, string ConfigPath, McpConfigFormat Format, bool Installed)
{
    /// <summary>
    /// 같은 앱의 다른 설치본이 읽는 설정 파일 (Claude Desktop: Store판이 둘 깔려 있거나 설치 프로그램판도 있을 때).
    /// 연결·해제는 여기에도 해서, 어느 쪽 Claude를 켜도 Foldy가 보이게 한다. 연결됐는지는 <see cref="ConfigPath"/>(가장 최근에 쓴 설치본)로 본다.
    /// </summary>
    public IReadOnlyList<string> OtherConfigPaths { get; init; } = [];
}

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
        // Claude Desktop: 설정 폴더가 판마다 다르다 (ClaudeDesktopDirs). 가장 최근에 쓴 곳이 앞
        const string DesktopFile = "claude_desktop_config.json";
        var desktopDirs = ClaudeDesktopDirs(roaming, local);
        var desktop = new McpClientApp("Claude Desktop", Path.Combine(desktopDirs.Count > 0 ? desktopDirs[0] : Path.Combine(roaming, "Claude"), DesktopFile), McpConfigFormat.McpServers, desktopDirs.Count > 0)
        {
            OtherConfigPaths = [.. desktopDirs.Skip(1).Select(d => Path.Combine(d, DesktopFile))],
        };

        bool Dir(params string[] p) => Directory.Exists(Path.Combine(p));

        // Antigravity (Google): ~/.gemini/config/mcp_config.json, 예전 판은 ~/.gemini/antigravity/mcp_config.json (사용자 2026-11-22: Antigravity에서는 된다)
        var antigravity = Dir(home, ".gemini", "config") || !Dir(home, ".gemini", "antigravity")
            ? Path.Combine(home, ".gemini", "config", "mcp_config.json")
            : Path.Combine(home, ".gemini", "antigravity", "mcp_config.json");
        var antigravityInstalled = Dir(home, ".gemini", "antigravity") || Dir(home, ".gemini", "config")
            || (Directory.Exists(Path.Combine(local, "Programs")) && Directory.EnumerateDirectories(Path.Combine(local, "Programs"), "Antigravity*").Any());
        return
        [
            desktop,
            new("Claude Code", Path.Combine(home, ".claude.json"), McpConfigFormat.McpServersStdio, File.Exists(Path.Combine(home, ".claude.json")) || Dir(home, ".claude")),
            new("Codex", Path.Combine(home, ".codex", "config.toml"), McpConfigFormat.CodexToml, Dir(home, ".codex")),
            new("Gemini CLI", Path.Combine(home, ".gemini", "settings.json"), McpConfigFormat.McpServers, Dir(home, ".gemini")),
            new("Cursor", Path.Combine(home, ".cursor", "mcp.json"), McpConfigFormat.McpServers, Dir(home, ".cursor")),
            new("VS Code", Path.Combine(roaming, "Code", "User", "mcp.json"), McpConfigFormat.VsCodeServers, Dir(roaming, "Code", "User")),
            new("Antigravity", antigravity, McpConfigFormat.McpServers, antigravityInstalled),
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

    /// <summary>
    /// Claude Desktop이 지금 읽는 설정 폴더들, 가장 최근에 쓴 곳부터 (사용자 2026-11-22: 연결했는데 Claude 새 대화에 Foldy가 없었다).
    /// 판마다 위치가 다르다:
    /// 새 판(Store판·설치 프로그램판)은 %LOCALAPPDATA%\Claude-Data\&lt;패키지 이름&gt;, 설치 프로그램판은 %APPDATA%\Claude,
    /// 예전 Store판은 %LOCALAPPDATA%\Packages\…Claude_…\LocalCache\Roaming\Claude.
    /// 데이터가 다른 곳으로 옮겨 간 폴더에는 ".user-data-left.…" 표시 파일이 남는데, 그 폴더의 설정은 아무도 읽지 않으므로 뺀다.
    /// </summary>
    public static IReadOnlyList<string> ClaudeDesktopDirs(string roaming, string local)
    {
        var found = new List<string>();
        void Add(string dir)
        {
            if (Directory.Exists(dir) && !Moved(dir) && !found.Contains(dir, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(dir);
            }
        }

        try
        {
            var data = Path.Combine(local, "Claude-Data");
            if (Directory.Exists(data))
            {
                foreach (var d in Directory.EnumerateDirectories(data).Where(d => File.Exists(Path.Combine(d, "claude_desktop_config.json")) || File.Exists(Path.Combine(d, "config.json"))))
                {
                    Add(d);
                }
            }

            Add(Path.Combine(roaming, "Claude"));
            var pk = Path.Combine(local, "Packages");
            if (Directory.Exists(pk))
            {
                foreach (var d in Directory.EnumerateDirectories(pk, "*Claude_*"))
                {
                    Add(Path.Combine(d, "LocalCache", "Roaming", "Claude"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return [.. found.OrderByDescending(LastUsed)];
    }

    private static bool Moved(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, ".user-data-left.*").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>앱이 이 폴더에 마지막으로 쓴 때 (바로 아래 파일들 중 가장 늦은 것).</summary>
    private static DateTime LastUsed(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
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
        // 다른 설치본에도 넣는다 (어느 쪽 Claude를 켜도 보이게). 그쪽 실패는 결과에 넣지 않는다
        foreach (var other in app.OtherConfigPaths)
        {
            try
            {
                ConnectFile(other, app.Format, exePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return ConnectFile(app.ConfigPath, app.Format, exePath);
    }

    /// <summary>설정에서 Foldy만 뺀다 (다른 설치본 설정에서도).</summary>
    public static bool Disconnect(McpClientApp app)
    {
        foreach (var other in app.OtherConfigPaths)
        {
            try
            {
                DisconnectFile(other, app.Format);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return DisconnectFile(app.ConfigPath, app.Format);
    }

    private static bool ConnectFile(string path, McpConfigFormat format, string exePath)
    {
        if (format == McpConfigFormat.CodexToml)
        {
            var lines = RemoveTomlBlock(ReadLines(path));
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
            Save(path, string.Join('\n', lines) + "\n");
            return true;
        }

        var root = Load(path);
        if (root is null)
        {
            return false;
        }

        var key = Key(format);
        if (root[key] is not JsonObject servers)
        {
            root[key] = servers = [];
        }

        var entry = new JsonObject();
        if (format != McpConfigFormat.McpServers)
        {
            entry["type"] = "stdio";
        }

        entry["command"] = exePath;
        entry["args"] = new JsonArray("--mcp");
        servers[ServerName] = entry;
        Save(path, root.ToJsonString(Pretty) + "\n");
        return true;
    }

    private static bool DisconnectFile(string path, McpConfigFormat format)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        if (format == McpConfigFormat.CodexToml)
        {
            var before = ReadLines(path);
            var after = RemoveTomlBlock(before);
            if (after.Count != before.Count)
            {
                Save(path, string.Join('\n', after).TrimEnd('\n') + "\n");
            }

            return true;
        }

        var root = Load(path);
        if (root is null)
        {
            return false;
        }

        if (root[Key(format)] is JsonObject servers && servers.Remove(ServerName))
        {
            Save(path, root.ToJsonString(Pretty) + "\n");
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
