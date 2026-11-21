using System.IO;
using System.Text;
using Foldy.Core.Mcp;
using Foldy.Indexer.Client;
using Foldy.Presentation.Persistence;

namespace Foldy.App.Services;

/// <summary>
/// Foldy.exe --mcp: 창 없이 MCP 서버로 돈다 (stdin/stdout, 공식 사양). Claude Desktop·Claude Code 같은 AI 앱이 띄운다.
/// 이 프로세스를 띄운 사용자의 권한으로만 파일을 본다. 색인 서비스도 연결한 사용자 기준으로 거른다(다른 사용자 프로필은 안 보인다).
/// </summary>
public static class McpRunner
{
    public static string SettingsPath => Path.Combine(App.DataDir, "settings.json");

    /// <summary>AI 앱 설정에 넣을 실행 파일 경로.</summary>
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Foldy.exe");

    private static AppSettings LoadSettings()
    {
        try
        {
            return AppSettings.Migrate(Foldy.Presentation.Persistence.JsonFileStore.Load(SettingsPath, () => new AppSettings()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>도구 목록 (서버와 "AI 연결" 화면이 같은 것을 쓴다).</summary>
    public static IReadOnlyList<McpTool> CreateTools()
    {
        IndexerClient? client = null;
        var gate = new SemaphoreSlim(1, 1);
        async Task<IndexerClient?> Client(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (client is null || client.IsFaulted)
                {
                    client = await IndexerClient.TryConnectAsync(timeout: TimeSpan.FromSeconds(1), ct: ct);
                }

                return client;
            }
            finally
            {
                gate.Release();
            }
        }

        var settings = LoadSettings();
        DateTime loaded = DateTime.UtcNow;
        AppSettings Settings()
        {
            if (DateTime.UtcNow - loaded > TimeSpan.FromSeconds(2))
            {
                settings = LoadSettings(); // 켜고 끄기가 바로 반영되게
                loaded = DateTime.UtcNow;
            }

            return settings;
        }

        var crawler = new Foldy.Core.Search.SlowCrawler(Path.Combine(App.DataDir, "crawl"), () => []);
        var notes = new Foldy.Core.Descriptions.FolderDescriptionService(new Foldy.Core.Descriptions.JsonFolderNoteStore(Path.Combine(App.DataDir, "folder_notes.json")));
        Foldy.Core.Tags.FileTags.Load(Path.Combine(App.DataDir, "tags-local.json"));

        IReadOnlyList<string> CrawledDrives() =>
            [.. DriveInfo.GetDrives().Select(d => d.Name).Where(d => crawler.StoreFor(d).Exists || File.Exists(crawler.StoreFor(d).FilePath + ".part"))];

        var host = new FoldyMcpHost
        {
            Enabled = () => Settings().McpEnabled,
            ShowHidden = () => Settings().ShowHidden,
            Find = async (query, folder, limit, ct) =>
            {
                var c = await Client(ct);
                var search = new FileSearchService(() => c, () => Settings().ShowHidden) { Crawler = crawler, CrawlDrives = CrawledDrives };
                await search.RefreshStateAsync();
                var list = new List<McpFound>();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                var prefix = folder is null ? null : folder.TrimEnd('\\') + "\\";
                try
                {
                    await foreach (var batch in search.SearchAsync(query, folder, cts.Token))
                    {
                        foreach (var e in batch)
                        {
                            var p = e.Path.Value;
                            if (prefix is not null && !p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            list.Add(new McpFound(p, e.Kind == Foldy.Core.FileSystem.FsKind.Folder, e.Size, e.Modified));
                            if (list.Count >= limit)
                            {
                                return list;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 오래 걸리면 찾은 데까지
                }

                return list;
            },
            Drives = async ct =>
            {
                var c = await Client(ct);
                StatusReply? st = null;
                try
                {
                    st = c is null ? null : await c.StatusAsync(ct);
                }
                catch (Exception ex) when (ex is IOException or ProtocolException or OperationCanceledException)
                {
                }

                var list = new List<McpDrive>();
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!d.IsReady)
                        {
                            continue;
                        }

                        var letter = d.Name.TrimEnd('\\');
                        var v = st?.Volumes.FirstOrDefault(x => string.Equals(x.Volume.TrimEnd('\\'), letter, StringComparison.OrdinalIgnoreCase));
                        var store = crawler.StoreFor(d.Name);
                        var index = v is not null
                            ? v.State switch { IndexState.Ready => "색인 완료", IndexState.Indexing => $"색인 중 {v.ProgressPermille / 10}%", _ => "색인 준비 중" }
                            : store.Exists ? "색인 완료 (Foldy가 천천히 모은 목록)"
                            : File.Exists(store.FilePath + ".part") ? "색인 중 (Foldy가 천천히 모으는 중)"
                            : "색인 없음";
                        var kind = d.DriveType switch
                        {
                            DriveType.Network => "네트워크 드라이브",
                            DriveType.Removable => "이동식",
                            DriveType.CDRom => "CD/DVD",
                            _ => string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase) ? "로컬" : $"로컬 {d.DriveFormat}",
                        };
                        list.Add(new McpDrive(d.Name, d.VolumeLabel, kind, index, d.AvailableFreeSpace, d.TotalSize));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                return list;
            },
            FolderSizes = async (path, ct) =>
            {
                var c = await Client(ct);
                if (c is null)
                {
                    return null;
                }

                try
                {
                    var r = await c.FolderSizesAsync(path, ct);
                    return r.Status == FolderSizesStatus.Ok ? [.. r.Entries.Select(e => new McpSize(e.Name, e.IsDir, (long)e.Size))] : null;
                }
                catch (Exception ex) when (ex is IOException or ProtocolException or OperationCanceledException)
                {
                    return null;
                }
            },
            FolderNote = path =>
            {
                var d = notes.Read(path).Description;
                if (d is null)
                {
                    return null;
                }

                var sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(d.Title))
                {
                    sb.Append("# ").Append(d.Title).Append('\n');
                }

                if (d.Tags.Count > 0)
                {
                    sb.Append("태그: ").Append(string.Join(", ", d.Tags)).Append('\n');
                }

                sb.Append(d.Body);
                return sb.ToString().Trim();
            },
            Tags = Foldy.Core.Tags.FileTags.Get,
        };
        return FoldyMcpTools.Create(host);
    }

    /// <summary>stdin/stdout으로 MCP 서버를 돌린다. 입력이 끝나면(AI 앱이 닫으면) 돌아온다.</summary>
    public static int Run()
    {
        var utf8 = new UTF8Encoding(false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false, NewLine = "\n" };
        var version = typeof(McpRunner).Assembly.GetName().Version?.ToString(3) ?? "0";
        var tools = CreateTools();
        var server = new McpServer(tools, version, FoldyMcpTools.Instructions);
        // WPF 디스패처 위에서 기다리면 await가 같은 스레드로 돌아오려다 멈춘다: 스레드 풀에서 돌린다
        Task.Run(() => server.RunAsync(input, output, CancellationToken.None)).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>Claude Desktop 등 (claude_desktop_config.json의 mcpServers).</summary>
    public static string DesktopConfigJson() =>
        "{\n  \"mcpServers\": {\n    \"foldy\": {\n      \"command\": " + System.Text.Json.JsonSerializer.Serialize(ExePath) + ",\n      \"args\": [\"--mcp\"]\n    }\n  }\n}";

    /// <summary>Claude Code 명령.</summary>
    public static string ClaudeCodeCommand() => $"claude mcp add foldy -- \"{ExePath}\" --mcp";
}
