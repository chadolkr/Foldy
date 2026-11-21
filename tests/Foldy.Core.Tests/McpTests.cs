using System.Text.Json;
using System.Text.Json.Nodes;
using Foldy.Core.Mcp;

namespace Foldy.Core.Tests;

public sealed class McpServerTests
{
    private static McpServer Server(FoldyMcpHost? host = null) => new(FoldyMcpTools.Create(host ?? new FoldyMcpHost()), "1.2.3", FoldyMcpTools.Instructions);

    private static async Task<JsonNode> Ask(McpServer s, string json) =>
        JsonNode.Parse((await s.HandleAsync(json, TestContext.Current.CancellationToken))!)!;

    [Fact]
    public async Task Initialize_agrees_on_a_protocol_version()
    {
        var s = Server();
        var r = await Ask(s, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");
        Assert.Equal("2025-03-26", (string?)r["result"]!["protocolVersion"]);
        Assert.Equal("foldy", (string?)r["result"]!["serverInfo"]!["name"]);
        Assert.NotNull(r["result"]!["capabilities"]!["tools"]);

        var unknown = await Ask(s, """{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");
        Assert.Equal(McpServer.ProtocolVersions[0], (string?)unknown["result"]!["protocolVersion"]);
        Assert.Equal("a", (string?)unknown["id"]); // 문자열 id도 그대로
    }

    [Fact]
    public async Task Notifications_get_no_reply_and_bad_input_gets_errors()
    {
        var s = Server();
        Assert.Null(await s.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", TestContext.Current.CancellationToken));
        Assert.Equal(-32700, (int)(await Ask(s, "{not json"))["error"]!["code"]!);
        Assert.Equal(-32601, (int)(await Ask(s, """{"jsonrpc":"2.0","id":2,"method":"resources/list"}"""))["error"]!["code"]!);
        Assert.Equal(-32602, (int)(await Ask(s, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"delete_everything"}}"""))["error"]!["code"]!);
        Assert.NotNull((await Ask(s, """{"jsonrpc":"2.0","id":4,"method":"ping"}"""))["result"]);
    }

    [Fact]
    public async Task Tools_are_listed_with_schema_and_read_only_hints()
    {
        var r = await Ask(Server(), """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var tools = r["result"]!["tools"]!.AsArray();
        Assert.Equal(["find_files", "list_folder", "folder_sizes", "read_folder_note", "get_file_tags", "drive_status"], tools.Select(t => (string)t!["name"]!));
        foreach (var t in tools)
        {
            Assert.Equal("object", (string?)t!["inputSchema"]!["type"]);
            Assert.True((bool)t["annotations"]!["readOnlyHint"]!); // 처음 버전은 모두 읽기 전용
            Assert.False((bool)t["annotations"]!["destructiveHint"]!);
            Assert.False(string.IsNullOrWhiteSpace((string?)t["description"]));
        }

        var find = tools.First(t => (string)t!["name"]! == "find_files")!;
        Assert.Equal(["query"], find["inputSchema"]!["required"]!.AsArray().Select(x => (string)x!));
        Assert.Equal("array", (string?)tools.First(t => (string)t!["name"]! == "get_file_tags")!["inputSchema"]!["properties"]!["paths"]!["type"]);
    }

    [Fact]
    public async Task Turned_off_means_tools_answer_nothing()
    {
        var called = false;
        var s = Server(new FoldyMcpHost
        {
            Enabled = () => false,
            Find = (_, _, _, _) =>
            {
                called = true;
                return Task.FromResult<IReadOnlyList<McpFound>>([]);
            },
        });
        var r = await Ask(s, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_files","arguments":{"query":"x"}}}""");
        Assert.True((bool)r["result"]!["isError"]!);
        Assert.Contains("꺼져", (string?)r["result"]!["content"]![0]!["text"], StringComparison.Ordinal);
        Assert.False(called);
    }

    [Fact]
    public async Task Find_files_passes_query_folder_and_limit_and_returns_items()
    {
        (string Q, string? F, int L)? seen = null;
        var s = Server(new FoldyMcpHost
        {
            Find = (q, f, l, _) =>
            {
                seen = (q, f, l);
                return Task.FromResult<IReadOnlyList<McpFound>>([new McpFound(@"D:\영업\2026 제안서.pptx", false, 2048, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc))]);
            },
        });
        var r = await Ask(s, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_files","arguments":{"query":" 제안서 ","folder":"D:\\영업","limit":9999}}}""");
        Assert.Equal(("제안서", @"D:\영업", 500), seen); // 앞뒤 빈칸 정리, 최대 500
        Assert.False((bool)r["result"]!["isError"]!);
        Assert.Contains("2026 제안서.pptx", (string?)r["result"]!["content"]![0]!["text"], StringComparison.Ordinal);
        var item = r["result"]!["structuredContent"]!["items"]![0]!;
        Assert.Equal("file", (string?)item["kind"]);
        Assert.Equal(2048, (long)item["size"]!);
    }

    [Theory]
    [InlineData("상대경로\\폴더")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData("")]
    public async Task Paths_must_be_full_normal_paths(string path)
    {
        var s = Server();
        var r = await Ask(s, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "list_folder", arguments = new { path } } }));
        Assert.True((bool)r["result"]!["isError"]!);
    }

    [Fact]
    public async Task List_folder_shows_entries_and_hides_hidden_files_unless_asked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "foldy-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "하위"));
        File.WriteAllText(Path.Combine(dir, "보임.txt"), "x");
        var hidden = Path.Combine(dir, "숨김.txt");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        try
        {
            var show = false;
            var s = Server(new FoldyMcpHost { ShowHidden = () => show });
            var q = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "list_folder", arguments = new { path = dir } } });
            var text = (string?)(await Ask(s, q))["result"]!["content"]![0]!["text"];
            Assert.Contains("[폴더] 하위", text, StringComparison.Ordinal);
            Assert.Contains("보임.txt", text, StringComparison.Ordinal);
            Assert.DoesNotContain("숨김.txt", text, StringComparison.Ordinal);
            show = true;
            Assert.Contains("숨김.txt", (string?)(await Ask(s, q))["result"]!["content"]![0]!["text"], StringComparison.Ordinal);
        }
        finally
        {
            File.SetAttributes(hidden, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Stdio_loop_answers_line_by_line_until_input_ends()
    {
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"ping"}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list"}
            """);
        var output = new StringWriter();
        await Server().RunAsync(input, output, TestContext.Current.CancellationToken);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.DoesNotContain('\n', l));
    }
}

public sealed class McpClientConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-mcpcfg-" + Guid.NewGuid().ToString("N"));
    private const string Exe = @"C:\Program Files\Foldy\Foldy.exe";

    public McpClientConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private McpClientApp App(string file, McpConfigFormat f) => new("시험", Path.Combine(_dir, file), f, true);

    [Fact]
    public void Connect_keeps_other_settings_and_leaves_a_backup()
    {
        var app = App("claude_desktop_config.json", McpConfigFormat.McpServers);
        File.WriteAllText(app.ConfigPath, """
            {
              // 사용자가 쓴 주석
              "mcpServers": { "other": { "command": "x" } },
              "theme": "dark",
            }
            """);
        Assert.False(McpClientConfig.IsConnected(app));
        Assert.True(McpClientConfig.Connect(app, Exe));
        Assert.True(McpClientConfig.IsConnected(app));
        var o = JsonNode.Parse(File.ReadAllText(app.ConfigPath))!;
        Assert.Equal("x", (string?)o["mcpServers"]!["other"]!["command"]);
        Assert.Equal("dark", (string?)o["theme"]);
        Assert.Equal(Exe, (string?)o["mcpServers"]!["foldy"]!["command"]);
        Assert.Equal("--mcp", (string?)o["mcpServers"]!["foldy"]!["args"]![0]);
        Assert.Null(o["mcpServers"]!["foldy"]!["type"]);
        Assert.True(File.Exists(app.ConfigPath + ".foldy-backup"));

        Assert.True(McpClientConfig.Disconnect(app));
        Assert.False(McpClientConfig.IsConnected(app));
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(app.ConfigPath))!["mcpServers"]!["other"]);
    }

    [Fact]
    public void Broken_json_is_left_alone()
    {
        var app = App("broken.json", McpConfigFormat.McpServers);
        File.WriteAllText(app.ConfigPath, "{ 고장난 설정");
        Assert.False(McpClientConfig.Connect(app, Exe));
        Assert.Equal("{ 고장난 설정", File.ReadAllText(app.ConfigPath));
    }

    [Fact]
    public void Missing_file_is_created_and_formats_differ_per_app()
    {
        var code = App(Path.Combine("home", ".claude.json"), McpConfigFormat.McpServersStdio);
        Assert.True(McpClientConfig.Connect(code, Exe));
        Assert.Equal("stdio", (string?)JsonNode.Parse(File.ReadAllText(code.ConfigPath))!["mcpServers"]!["foldy"]!["type"]);

        var vs = App("mcp.json", McpConfigFormat.VsCodeServers);
        Assert.True(McpClientConfig.Connect(vs, Exe));
        var o = JsonNode.Parse(File.ReadAllText(vs.ConfigPath))!;
        Assert.Null(o["mcpServers"]);
        Assert.Equal("stdio", (string?)o["servers"]!["foldy"]!["type"]);
        Assert.True(McpClientConfig.IsConnected(vs));
    }

    [Fact]
    public void Codex_toml_block_is_added_replaced_and_removed_without_touching_others()
    {
        var app = App("config.toml", McpConfigFormat.CodexToml);
        File.WriteAllText(app.ConfigPath, "model = \"gpt-5\"\n\n[mcp_servers.other]\ncommand = \"npx\"\n\n[mcp_servers.foldy]\ncommand = 'old.exe'\n\n[mcp_servers.foldy.env]\nA = \"1\"\n\n[profiles.work]\nmodel = \"x\"\n");
        Assert.True(McpClientConfig.IsConnected(app));
        Assert.True(McpClientConfig.Connect(app, Exe));
        var text = File.ReadAllText(app.ConfigPath);
        Assert.DoesNotContain("old.exe", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[mcp_servers.foldy.env]", text, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.other]\ncommand = \"npx\"", text, StringComparison.Ordinal);
        Assert.Contains("[profiles.work]\nmodel = \"x\"", text, StringComparison.Ordinal);
        Assert.Contains($"[mcp_servers.foldy]\ncommand = '{Exe}'\nargs = [\"--mcp\"]", text, StringComparison.Ordinal);
        Assert.Single(text.Split('\n'), l => l == "[mcp_servers.foldy]");

        Assert.True(McpClientConfig.Disconnect(app));
        text = File.ReadAllText(app.ConfigPath);
        Assert.DoesNotContain("foldy", text, StringComparison.Ordinal);
        Assert.Contains("model = \"gpt-5\"", text, StringComparison.Ordinal);
        Assert.Contains("[profiles.work]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Known_apps_are_found_by_their_folders()
    {
        var roaming = Path.Combine(_dir, "roaming");
        var local = Path.Combine(_dir, "local");
        var home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(Path.Combine(roaming, "Claude"));
        Directory.CreateDirectory(Path.Combine(home, ".codex"));
        var apps = McpClientConfig.Known(roaming, local, home);
        Assert.Equal(["Claude Desktop", "Claude Code", "Codex", "Gemini CLI", "Cursor", "VS Code"], apps.Select(a => a.Name));
        Assert.Equal(["Claude Desktop", "Codex"], apps.Where(a => a.Installed).Select(a => a.Name));
        Assert.DoesNotContain(apps, a => a.Name.Contains("ChatGPT", StringComparison.Ordinal)); // 원격 MCP만 받는 앱은 PC 안 Foldy를 밖에 열어야 해서 하지 않는다
    }
}

public sealed class McpSuggestTests
{
    private static McpClientApp A(string n, bool installed) => new(n, n + ".json", McpConfigFormat.McpServers, installed);

    [Fact]
    public void Suggests_only_when_an_ai_app_is_installed_and_none_is_connected()
    {
        McpClientConfig.SuggestTarget([A("Claude Desktop", false), A("Codex", false)], _ => false).Should().BeNull(); // AI 앱이 없으면 안 보임
        McpClientConfig.SuggestTarget([A("Claude Desktop", false), A("Codex", true)], _ => false).Should().Be("Codex");
        McpClientConfig.SuggestTarget([A("Claude Desktop", true), A("Codex", true)], a => a.Name == "Codex").Should().BeNull(); // 하나라도 연결했으면 묻지 않음
    }

    [Fact]
    public void Suggestion_line_carries_an_example_and_rotates()
    {
        var before = Foldy.Core.Localization.Loc.Current;
        Foldy.Core.Localization.Loc.Use("ko");
        try
        {
            var lines = Enumerable.Range(0, FoldyMcpTools.Examples.Count).Select(i => FoldyMcpTools.SuggestLine("Claude Desktop", i)).ToList();
            lines.Should().OnlyContain(l => l.StartsWith("Claude Desktop에게 \"", StringComparison.Ordinal) && l.Contains("읽기만", StringComparison.Ordinal));
            lines.Distinct().Should().HaveCount(FoldyMcpTools.Examples.Count);
            FoldyMcpTools.SuggestLine("Codex", -7).Should().Contain("Codex에게"); // 음수 씨앗도 안전
            Foldy.Core.Localization.Loc.Use("en");
            FoldyMcpTools.SuggestLine("Codex", 0).Should().StartWith("Ask Codex \"Find last year's quotes\"");
        }
        finally
        {
            Foldy.Core.Localization.Loc.Use(before);
        }
    }
}
