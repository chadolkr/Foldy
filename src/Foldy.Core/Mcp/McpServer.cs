using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Foldy.Core.Mcp;

/// <summary>입력값 하나 (도구 설명 화면과 JSON Schema에 함께 쓴다).</summary>
/// <param name="Name">이름 (영문, AI가 쓴다).</param>
/// <param name="Type">string / integer / boolean / array(문자열 목록).</param>
/// <param name="Description">AI와 사람이 함께 읽는 쉬운 설명.</param>
/// <param name="Required">꼭 넣어야 하나.</param>
public sealed record McpParam(string Name, string Type, string Description, bool Required = false);

/// <summary>도구 결과: 사람과 AI가 읽을 글 + (있으면) 구조화된 값.</summary>
public sealed record McpResult(string Text, bool IsError = false, JsonNode? Structured = null)
{
    public static McpResult Error(string text) => new(text, true);
}

/// <summary>
/// MCP 도구 하나. 이름·설명·입력값은 서버와 Foldy의 "AI 연결" 화면이 같은 것을 쓴다(따로 적으면 어긋난다).
/// </summary>
public sealed record McpTool(string Name, string Title, string Description, IReadOnlyList<McpParam> Params, bool ReadOnly,
    Func<JsonElement, CancellationToken, Task<McpResult>> Handler)
{
    /// <summary>tools/list에 내보내는 JSON Schema.</summary>
    public JsonObject InputSchema()
    {
        var props = new JsonObject();
        foreach (var p in Params)
        {
            var o = new JsonObject { ["description"] = p.Description };
            if (p.Type == "array")
            {
                o["type"] = "array";
                o["items"] = new JsonObject { ["type"] = "string" };
            }
            else
            {
                o["type"] = p.Type;
            }

            props[p.Name] = o;
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        var req = Params.Where(p => p.Required).Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray();
        if (req.Length > 0)
        {
            schema["required"] = new JsonArray(req);
        }

        return schema;
    }

    /// <summary>AI가 고를 때 보는 힌트 (MCP annotations).</summary>
    public JsonObject Annotations() => new()
    {
        ["title"] = Title,
        ["readOnlyHint"] = ReadOnly,
        ["destructiveHint"] = false,
        ["idempotentHint"] = ReadOnly,
        ["openWorldHint"] = false,
    };
}

/// <summary>
/// MCP 서버 (공식 사양, JSON-RPC 2.0 · 줄마다 메시지 하나 · stdio). Foldy.exe --mcp가 띄운다.
/// 지원: initialize, notifications/initialized, ping, tools/list, tools/call. 나머지는 "없는 메서드" 오류.
/// </summary>
public sealed class McpServer(IReadOnlyList<McpTool> tools, string version, string instructions)
{
    /// <summary>지원하는 사양 버전 (최신이 앞). 클라이언트가 이 중 하나를 말하면 그대로 쓴다.</summary>
    public static readonly IReadOnlyList<string> ProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public IReadOnlyList<McpTool> Tools => tools;

    /// <summary>입력이 끝날 때까지 한 줄씩 읽고 답한다. 출력은 줄마다 JSON 하나(UTF-8, BOM 없음).</summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(ct);
            if (line is null)
            {
                return;
            }

            if (line.Length == 0)
            {
                continue;
            }

            var reply = await HandleAsync(line, ct);
            if (reply is not null)
            {
                await output.WriteLineAsync(reply.AsMemory(), ct);
                await output.FlushAsync(ct);
            }
        }
    }

    /// <summary>메시지 하나를 처리한다. 알림(id 없음)이면 null.</summary>
    public async Task<string?> HandleAsync(string line, CancellationToken ct)
    {
        JsonNode? id = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Fail(null, -32600, "Invalid Request");
            }

            var hasId = root.TryGetProperty("id", out var idEl);
            if (hasId)
            {
                id = JsonNode.Parse(idEl.GetRawText());
            }

            var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (method is null)
            {
                return hasId ? Fail(id, -32600, "Invalid Request") : null; // 응답(우리가 보낸 요청이 없으므로) 무시
            }

            var prms = root.TryGetProperty("params", out var p) ? p : default;
            if (!hasId)
            {
                return null; // notifications/initialized, notifications/cancelled 등
            }

            JsonNode result;
            switch (method)
            {
                case "initialize":
                    result = Initialize(prms);
                    break;
                case "ping":
                    result = new JsonObject();
                    break;
                case "tools/list":
                    result = new JsonObject { ["tools"] = new JsonArray([.. tools.Select(ToolJson)]) };
                    break;
                case "tools/call":
                    var name = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var tool = tools.FirstOrDefault(t => t.Name == name);
                    if (tool is null)
                    {
                        return Fail(id, -32602, $"Unknown tool: {name}");
                    }

                    var args = prms.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a : EmptyArgs;
                    McpResult r;
                    try
                    {
                        r = await tool.Handler(args, ct);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException or TimeoutException or NotSupportedException)
                    {
                        r = McpResult.Error(ex.Message);
                    }

                    var o = new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = r.Text }),
                        ["isError"] = r.IsError,
                    };
                    if (r.Structured is not null)
                    {
                        o["structuredContent"] = r.Structured;
                    }

                    result = o;
                    break;
                default:
                    return Fail(id, -32601, $"Method not found: {method}");
            }

            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result }.ToJsonString(Compact);
        }
        catch (JsonException)
        {
            return Fail(id, -32700, "Parse error");
        }
    }

    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement.Clone();

    private JsonObject Initialize(JsonElement prms)
    {
        var asked = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("protocolVersion", out var v) ? v.GetString() : null;
        var use = asked is not null && ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0];
        return new JsonObject
        {
            ["protocolVersion"] = use,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "foldy", ["title"] = "Foldy", ["version"] = version },
            ["instructions"] = instructions,
        };
    }

    public static JsonObject ToolJson(McpTool t) => new()
    {
        ["name"] = t.Name,
        ["title"] = t.Title,
        ["description"] = t.Description,
        ["inputSchema"] = t.InputSchema(),
        ["annotations"] = t.Annotations(),
    };

    private static string Fail(JsonNode? id, int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(Compact);

    /// <summary>입력값 읽기 도우미.</summary>
    public static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int Number(JsonElement args, string name, int fallback, int min, int max)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
            {
                return Math.Clamp(i, min, max);
            }

            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j))
            {
                return Math.Clamp(j, min, max);
            }
        }

        return fallback;
    }

    public static IReadOnlyList<string> List(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v))
        {
            return [];
        }

        return v.ValueKind switch
        {
            JsonValueKind.Array => [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)],
            JsonValueKind.String => [v.GetString()!],
            _ => [],
        };
    }

    /// <summary>표 같은 글을 만들 때 쓰는 크기 표기.</summary>
    public static string Size(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double s = bytes;
        var i = 0;
        while (s >= 1024 && i < u.Length - 1)
        {
            s /= 1024;
            i++;
        }

        return i == 0 ? $"{bytes} B" : string.Create(CultureInfo.InvariantCulture, $"{s:0.#} {u[i]}");
    }

    internal static string Join(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            sb.Append(l).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }
}
