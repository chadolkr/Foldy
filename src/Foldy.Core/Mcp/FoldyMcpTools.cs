using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Foldy.Core.Mcp;

/// <summary>찾은 파일·폴더 하나.</summary>
public sealed record McpFound(string Path, bool IsFolder, long Size, DateTime ModifiedUtc);

/// <summary>드라이브 하나의 상태.</summary>
public sealed record McpDrive(string Drive, string Label, string Kind, string Index, long FreeBytes, long TotalBytes);

/// <summary>폴더 안 항목 하나의 크기 (하위 폴더 포함 합계).</summary>
public sealed record McpSize(string Name, bool IsFolder, long Size);

/// <summary>
/// 도구가 Foldy에 묻는 것들. 앱(Foldy.exe --mcp)이 색인 서비스·천천히 모은 목록·설정으로 채운다.
/// 모든 결과는 이 프로세스를 띄운 사용자의 권한으로만 나온다(색인 서비스도 연결한 사용자 기준으로 거른다).
/// </summary>
public sealed class FoldyMcpHost
{
    /// <summary>Foldy 설정의 "AI 연결 켜기". 꺼져 있으면 도구가 아무것도 돌려주지 않는다.</summary>
    public Func<bool> Enabled { get; init; } = () => true;

    public Func<bool> ShowHidden { get; init; } = () => false;

    /// <summary>이름으로 찾기 (색인 → 천천히 모은 목록 → 폴더 직접 훑기). (낱말, 폴더 또는 null, 최대 개수)</summary>
    public Func<string, string?, int, CancellationToken, Task<IReadOnlyList<McpFound>>> Find { get; init; } = (_, _, _, _) => Task.FromResult<IReadOnlyList<McpFound>>([]);

    public Func<CancellationToken, Task<IReadOnlyList<McpDrive>>> Drives { get; init; } = _ => Task.FromResult<IReadOnlyList<McpDrive>>([]);

    /// <summary>폴더 안 항목의 크기 (색인이 있는 드라이브만). 모르면 null.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<McpSize>?>> FolderSizes { get; init; } = (_, _) => Task.FromResult<IReadOnlyList<McpSize>?>(null);

    /// <summary>폴더 설명(.folder_description.md 또는 이 PC에 둔 설명). 없으면 null.</summary>
    public Func<string, string?> FolderNote { get; init; } = _ => null;

    /// <summary>파일 상태 태그 (진행 중·완료 등).</summary>
    public Func<string, IReadOnlyList<string>> Tags { get; init; } = _ => [];
}

/// <summary>
/// Foldy MCP 도구 목록 (설계: docs/mcp.md). 처음 버전은 모두 읽기 전용이다: 옮기기·지우기·이름 바꾸기는 없다.
/// 쓰기 도구를 넣게 되면 Foldy 확인 창을 거치게 한다(위험한 일만 확인 원칙).
/// </summary>
public static class FoldyMcpTools
{
    /// <summary>AI에게 이렇게 물어보세요 (AI 연결 화면·도움말·README에 같은 예시). 도구 하나 이상으로 답할 수 있는 것만.</summary>
    public static readonly IReadOnlyList<(string Ask, string What)> Examples =
    [
        ("작년 견적서 찾아 줘", "이름으로 파일 찾기 (회사 드라이브·Google Drive 포함)"),
        ("D 드라이브에서 공간을 많이 차지하는 폴더가 뭐야?", "큰 폴더부터 짚어 보기"),
        ("다운로드 폴더에 뭐가 있는지 정리해서 알려 줘", "폴더 안 보기"),
        ("이 프로젝트 폴더 설명 읽어 줘", "Foldy에 적어 둔 폴더 설명"),
        ("보고서 폴더에서 아직 검토 중인 파일 알려 줘", "파일 상태 태그"),
        ("드라이브 남은 공간과 색인 상태 알려 줘", "드라이브 상태"),
    ];

    /// <summary>
    /// 연결 제안 한 줄: 무엇을 할 수 있는지 예시 하나를 담는다 (사용자 2026-11-21: "연결하기 물을 때 알려 줘야지"). 예시는 실행마다 돌아가며.
    /// 예: Claude Desktop에게 "작년 견적서 찾아 줘"라고 하면 Foldy가 찾아 줘요. 읽기만 해요.
    /// </summary>
    public static string SuggestLine(string app, int seed)
    {
        var (ask, _) = Examples[(int)((uint)seed % (uint)Examples.Count)];
        return $"{app}에게 \"{ask}\"라고 하면 Foldy가 내 PC에서 찾아 줘요. 읽기만 해요.";
    }

    public const string Instructions =
        "Foldy는 Windows 파일 관리자예요. 이 서버로 사용자의 PC에서 파일을 아주 빨리 찾고(드라이브 전체 색인, 회사 드라이브·Google Drive 포함), " +
        "폴더 안을 보고, 어느 폴더가 큰지, 폴더 설명과 파일 상태 태그를 읽을 수 있어요. 모두 읽기 전용이고, 사용자 권한으로 볼 수 있는 것만 나와요. " +
        "파일 이름을 모르면 find_files부터, 드라이브가 무엇이 있는지 모르면 drive_status부터 쓰세요.";

    private const string Off = "Foldy의 AI 연결이 꺼져 있어요. Foldy → 설정 → AI 연결(MCP)에서 켜 주세요.";

    public static IReadOnlyList<McpTool> Create(FoldyMcpHost host)
    {
        Func<JsonElement, CancellationToken, Task<McpResult>> Guard(Func<JsonElement, CancellationToken, Task<McpResult>> f) =>
            (a, ct) => host.Enabled() ? f(a, ct) : Task.FromResult(McpResult.Error(Off));

        return
        [
            new McpTool("find_files", "파일 찾기",
                "이름에 낱말이 들어간 파일과 폴더를 찾아요. 띄어 쓴 낱말은 모두 들어 있어야 해요(예: \"견적 2026\"). " +
                "PC의 모든 드라이브를 색인으로 바로 찾고, 회사 드라이브·Google Drive처럼 색인이 없는 드라이브는 Foldy가 모아 둔 목록에서 찾아요. " +
                "folder를 주면 그 폴더 아래에서만 찾아요. 결과는 경로·종류·크기·수정한 때예요.",
                [
                    new McpParam("query", "string", "찾을 낱말. 파일 이름의 일부면 돼요 (예: \"제안서\", \"휴가 신청\", \".pptx\").", Required: true),
                    new McpParam("folder", "string", "이 폴더 아래에서만 찾기 (예: \"D:\\프로젝트\"). 비우면 PC 전체."),
                    new McpParam("limit", "integer", "최대 개수 (기본 50, 최대 500)."),
                ], true, Guard(async (a, ct) =>
                {
                    var q = McpServer.Str(a, "query")?.Trim();
                    if (string.IsNullOrEmpty(q))
                    {
                        return McpResult.Error("query(찾을 낱말)를 넣어 주세요.");
                    }

                    var folder = McpServer.Str(a, "folder");
                    if (!string.IsNullOrWhiteSpace(folder) && CheckPath(folder) is { } bad)
                    {
                        return bad;
                    }

                    var limit = McpServer.Number(a, "limit", 50, 1, 500);
                    var found = await host.Find(q, string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder), limit, ct);
                    var list = found.Take(limit).ToList();
                    var arr = new JsonArray([.. list.Select(f => (JsonNode)new JsonObject
                    {
                        ["path"] = f.Path,
                        ["kind"] = f.IsFolder ? "folder" : "file",
                        ["size"] = f.IsFolder ? null : f.Size,
                        ["modified"] = f.ModifiedUtc > DateTime.MinValue ? f.ModifiedUtc.ToString("o", CultureInfo.InvariantCulture) : null,
                    })]);
                    var text = list.Count == 0
                        ? $"\"{q}\"(이)가 이름에 들어간 파일을 찾지 못했어요."
                        : $"\"{q}\" 찾음 {list.Count}개{(list.Count >= limit ? " (더 있을 수 있어요: limit를 늘리거나 folder로 좁혀 보세요)" : "")}\n" +
                          McpServer.Join(list.Select(f => f.IsFolder ? $"[폴더] {f.Path}" : $"{f.Path} · {McpServer.Size(f.Size)} · {Local(f.ModifiedUtc)}"));
                    return new McpResult(text, false, new JsonObject { ["items"] = arr });
                })),

            new McpTool("list_folder", "폴더 안 보기",
                "폴더 안의 파일과 하위 폴더를 보여 줘요 (이름·크기·수정한 때). 숨김 파일은 Foldy에서 '숨김 파일 보기'를 켰을 때만 나와요.",
                [
                    new McpParam("path", "string", "폴더 경로 (예: \"C:\\Users\\나\\Documents\").", Required: true),
                    new McpParam("limit", "integer", "최대 개수 (기본 200, 최대 2000)."),
                ], true, Guard((a, ct) => Task.FromResult(ListFolder(host, a)))),

            new McpTool("folder_sizes", "폴더 크기",
                "폴더 안의 하위 폴더와 파일을 크기가 큰 순서로 보여 줘요 (하위 폴더는 그 안 전체 합계). 디스크가 왜 꽉 찼는지 볼 때 좋아요. " +
                "색인이 있는 드라이브(이 PC의 NTFS 드라이브)에서 바로 답하고, 없으면 알려 줘요.",
                [
                    new McpParam("path", "string", "폴더 경로 (예: \"C:\\\" 또는 \"D:\\사진\").", Required: true),
                    new McpParam("limit", "integer", "최대 개수 (기본 30, 최대 500)."),
                ], true, Guard(async (a, ct) =>
                {
                    var path = McpServer.Str(a, "path");
                    if (CheckPath(path) is { } bad)
                    {
                        return bad;
                    }

                    var limit = McpServer.Number(a, "limit", 30, 1, 500);
                    var sizes = await host.FolderSizes(Path.GetFullPath(path!), ct);
                    if (sizes is null)
                    {
                        return McpResult.Error("이 폴더는 색인이 없어 크기를 바로 계산할 수 없어요 (네트워크·클라우드 드라이브 등). list_folder로 안을 볼 수 있어요.");
                    }

                    var top = sizes.OrderByDescending(s => s.Size).Take(limit).ToList();
                    var total = sizes.Sum(s => s.Size);
                    return new McpResult($"{path} 전체 {McpServer.Size(total)}, 큰 순서\n" + McpServer.Join(top.Select(s => $"{McpServer.Size(s.Size),10}  {(s.IsFolder ? "[폴더] " : "")}{s.Name}")),
                        false, new JsonObject
                        {
                            ["total"] = total,
                            ["items"] = new JsonArray([.. top.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["kind"] = s.IsFolder ? "folder" : "file", ["size"] = s.Size })]),
                        });
                })),

            new McpTool("read_folder_note", "폴더 설명 읽기",
                "Foldy에서 폴더에 적어 둔 설명(이 폴더는 무엇인지, 규칙, 메모)을 읽어요. 폴더 안 .folder_description.md 또는 이 PC에 둔 설명이에요.",
                [new McpParam("path", "string", "폴더 경로.", Required: true)], true, Guard((a, ct) =>
                {
                    var path = McpServer.Str(a, "path");
                    if (CheckPath(path) is { } bad)
                    {
                        return Task.FromResult(bad);
                    }

                    var note = host.FolderNote(Path.GetFullPath(path!));
                    return Task.FromResult(note is { Length: > 0 } ? new McpResult(note) : new McpResult($"{path}에는 폴더 설명이 없어요."));
                })),

            new McpTool("get_file_tags", "파일 상태 태그",
                "Foldy에서 파일에 붙인 상태 태그(예: 진행 중, 검토 중, 완료)를 알려 줘요. 여러 경로를 한 번에 물어볼 수 있어요.",
                [new McpParam("paths", "array", "파일 또는 폴더 경로 목록.", Required: true)], true, Guard((a, ct) =>
                {
                    var paths = McpServer.List(a, "paths").Take(500).ToList();
                    if (paths.Count == 0)
                    {
                        return Task.FromResult(McpResult.Error("paths(경로 목록)를 넣어 주세요."));
                    }

                    var o = new JsonObject();
                    var lines = new List<string>();
                    foreach (var p in paths)
                    {
                        var tags = host.Tags(p);
                        o[p] = new JsonArray([.. tags.Select(t => (JsonNode?)JsonValue.Create(t))]);
                        lines.Add($"{p}: {(tags.Count == 0 ? "태그 없음" : string.Join(", ", tags))}");
                    }

                    return Task.FromResult(new McpResult(McpServer.Join(lines), false, new JsonObject { ["tags"] = o }));
                })),

            new McpTool("drive_status", "드라이브와 색인 상태",
                "이 PC의 드라이브 목록과 남은 공간, 그리고 빠른 찾기 색인 상태(색인 완료 · 색인 중 · 색인 없음)를 알려 줘요. 색인 중인 드라이브는 찾기 결과가 덜 나올 수 있어요.",
                [], true, Guard(async (a, ct) =>
                {
                    var drives = await host.Drives(ct);
                    var text = drives.Count == 0 ? "드라이브를 찾지 못했어요." : McpServer.Join(drives.Select(d =>
                        $"{d.Drive} {(d.Label.Length > 0 ? d.Label + " " : "")}({d.Kind}) · 남은 공간 {McpServer.Size(d.FreeBytes)} / {McpServer.Size(d.TotalBytes)} · {d.Index}"));
                    return new McpResult(text, false, new JsonObject
                    {
                        ["drives"] = new JsonArray([.. drives.Select(d => (JsonNode)new JsonObject
                        {
                            ["drive"] = d.Drive, ["label"] = d.Label, ["kind"] = d.Kind, ["index"] = d.Index, ["free"] = d.FreeBytes, ["total"] = d.TotalBytes,
                        })]),
                    });
                })),
        ];
    }

    private static McpResult ListFolder(FoldyMcpHost host, JsonElement a)
    {
        var path = McpServer.Str(a, "path");
        if (CheckPath(path) is { } bad)
        {
            return bad;
        }

        var full = Path.GetFullPath(path!);
        if (!Directory.Exists(full))
        {
            return McpResult.Error($"폴더가 없어요: {full}");
        }

        var limit = McpServer.Number(a, "limit", 200, 1, 2000);
        var skip = host.ShowHidden() ? FileAttributes.None : FileAttributes.Hidden | FileAttributes.System;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = skip, RecurseSubdirectories = false };
        var items = new List<(string Name, bool Dir, long Size, DateTime Mod)>();
        var more = false;
        foreach (var fsi in new DirectoryInfo(full).EnumerateFileSystemInfos("*", opts))
        {
            if (items.Count >= limit)
            {
                more = true;
                break;
            }

            var dir = (fsi.Attributes & FileAttributes.Directory) != 0;
            items.Add((fsi.Name, dir, dir ? 0 : ((FileInfo)fsi).Length, fsi.LastWriteTimeUtc));
        }

        var sorted = items.OrderByDescending(i => i.Dir).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var head = $"{full} · 폴더 {sorted.Count(i => i.Dir)}개, 파일 {sorted.Count(i => !i.Dir)}개{(more ? $" (처음 {limit}개만)" : "")}";
        return new McpResult(head + "\n" + McpServer.Join(sorted.Select(i => i.Dir ? $"[폴더] {i.Name}" : $"{i.Name} · {McpServer.Size(i.Size)} · {Local(i.Mod)}")), false,
            new JsonObject
            {
                ["path"] = full,
                ["more"] = more,
                ["items"] = new JsonArray([.. sorted.Select(i => (JsonNode)new JsonObject
                {
                    ["name"] = i.Name, ["kind"] = i.Dir ? "folder" : "file", ["size"] = i.Dir ? null : i.Size, ["modified"] = i.Mod.ToString("o", CultureInfo.InvariantCulture),
                })]),
            });
    }

    /// <summary>경로 확인: 전체 경로(드라이브 문자 또는 \\서버\공유)만 받는다. 장치 경로(\\?\, \\.\)는 받지 않는다.</summary>
    internal static McpResult? CheckPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return McpResult.Error("path(경로)를 넣어 주세요.");
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return McpResult.Error($"이 경로는 쓸 수 없어요: {path}");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return McpResult.Error($"전체 경로를 넣어 주세요 (예: \"C:\\폴더\"): {path}");
        }

        return null;
    }

    private static string Local(DateTime utc) =>
        utc > DateTime.MinValue ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
}
