using System.Text;
using System.Text.Json;
using Foldy.Core.Managed;

namespace Foldy.Core.Tags;

/// <summary>상태 태그 하나: 이름과 색(#RRGGBB).</summary>
public sealed record TagDef(string Name, string Color);

/// <summary>
/// 파일 상태 태그 ("작업 완료", "반영 완료" …). 여러 파일을 하나씩 처리할 때 어디까지 했는지 보이게 한다. 파일 자체는 건드리지 않는다.
/// <para>
/// 원본은 그 폴더의 숨김 파일 <c>.foldy.md</c>의 "파일 상태 태그" 칸이다. 그래서 폴더를 다른 디스크·PC로 옮겨도 태그가 따라간다
/// (NTFS 대체 데이터 스트림은 USB·클라우드·zip에서 사라져서 쓰지 않는다).
/// Git 저장소 안이면 처음 만들 때 <c>.git/info/exclude</c>에 한 줄 넣어 git status에 뜨지 않게 한다(.gitignore는 건드리지 않음).
/// 쓸 수 없는 폴더(읽기 전용 등)만 이 PC의 Foldy 데이터 폴더에 남긴다.
/// </para>
/// </summary>
public static class FileTags
{
    public static readonly IReadOnlyList<TagDef> Builtin =
    [
        new("작업 완료", "#2E9E5B"),
        new("반영 완료", "#2F7BD8"),
        new("검토 중", "#D99A1E"),
        new("제출 완료", "#8A5CD6"),
    ];

    /// <summary>
    /// 화면에 보일 태그 이름 (앱 언어). 저장 값은 한국어 이름 그대로 두어 예전 태그·다른 언어 PC와 섞이지 않는다.
    /// 직접 만든 태그는 쓴 그대로 보인다.
    /// </summary>
    public static string DisplayName(string tag) => tag switch
    {
        "작업 완료" => Foldy.Core.Localization.Loc.T("tag.done"), // i18n:skip
        "반영 완료" => Foldy.Core.Localization.Loc.T("tag.applied"), // i18n:skip
        "검토 중" => Foldy.Core.Localization.Loc.T("tag.review"), // i18n:skip
        "제출 완료" => Foldy.Core.Localization.Loc.T("tag.submitted"), // i18n:skip
        _ => tag,
    };

    /// <summary>Ctrl+D로 켜고 끄는 기본 태그.</summary>
    public static string Default => Builtin[0].Name;

    public const string SectionTitle = "## 파일 상태 태그";

    private static readonly Lock Gate = new();

    // 폴더별 캐시: 폴더 → (파일 이름 → 태그들), 읽은 시점의 .foldy.md 시각
    private static readonly Dictionary<string, (Dictionary<string, List<string>> Tags, DateTime Stamp, DateTime Checked)> Folders = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, List<string>> _local = new(StringComparer.OrdinalIgnoreCase);
    private static string? _localStore;

    public static event Action? Changed;

    /// <summary>이 PC에만 두는 태그(Git·AI 프로젝트 폴더용) 파일 위치를 정하고 읽는다.</summary>
    public static void Load(string localStorePath)
    {
        _localStore = localStorePath;
        try
        {
            if (File.Exists(localStorePath) && JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(localStorePath)) is { } d)
            {
                lock (Gate)
                {
                    _local = new Dictionary<string, List<string>>(d.Select(kv => KeyValuePair.Create(Norm(kv.Key), kv.Value)), StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }


    public static IReadOnlyList<string> Get(string path)
    {
        var p = Norm(path);
        var folder = Path.GetDirectoryName(p);
        if (folder is null)
        {
            return [];
        }

        lock (Gate)
        {
            if (_local.TryGetValue(p, out var lt))
            {
                return [.. lt];
            }

            return FolderTags(folder).TryGetValue(Path.GetFileName(p), out var t) ? [.. t] : [];
        }
    }

    public static bool Has(string path, string tag) => Get(path).Contains(tag, StringComparer.Ordinal);

    public static TagDef Def(string tag) => Builtin.FirstOrDefault(t => t.Name == tag) ?? new TagDef(tag, "#7A7A7A");

    /// <summary>여러 파일에 태그를 켜고 끈다: 하나라도 없으면 모두 켜고, 모두 있으면 모두 끈다.</summary>
    public static void Toggle(IEnumerable<string> paths, string tag)
    {
        var list = paths.Select(Norm).ToList();
        var allHave = list.All(p => Has(p, tag));
        foreach (var group in list.GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase))
        {
            Edit(group.Key, tags =>
            {
                foreach (var p in group)
                {
                    var name = Path.GetFileName(p);
                    if (!tags.TryGetValue(name, out var t))
                    {
                        tags[name] = t = [];
                    }

                    if (allHave)
                    {
                        t.Remove(tag);
                    }
                    else if (!t.Contains(tag))
                    {
                        t.Add(tag);
                    }

                    if (t.Count == 0)
                    {
                        tags.Remove(name);
                    }
                }
            });
        }

        Changed?.Invoke();
    }

    /// <summary>이 폴더 바로 아래 파일들의 태그를 모두 지운다. 지운 파일 수.</summary>
    public static int ClearFolder(string folder)
    {
        var n = 0;
        Edit(Norm(folder), tags =>
        {
            n = tags.Count;
            tags.Clear();
        });
        Changed?.Invoke();
        return n;
    }

    /// <summary>Foldy로 이름을 바꾸거나 옮겼을 때 태그를 따라가게 한다.</summary>
    public static void Moved(string from, string to)
    {
        var f = Norm(from);
        var t = Norm(to);
        var tags = Get(f);
        if (tags.Count == 0)
        {
            return;
        }

        Edit(Path.GetDirectoryName(f)!, m => m.Remove(Path.GetFileName(f)));
        Edit(Path.GetDirectoryName(t)!, m => m[Path.GetFileName(t)] = [.. tags]);
        Changed?.Invoke();
    }

    /// <summary>태그별 개수 (주어진 파일들 중), 기본 태그 순서대로.</summary>
    public static IReadOnlyList<(TagDef Tag, int Count)> Counts(IEnumerable<string> paths)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in paths)
        {
            foreach (var tag in Get(p))
            {
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
            }
        }

        var order = Builtin.Select(b => b.Name).ToList();
        return [.. counts.OrderBy(kv => order.IndexOf(kv.Key) is var i && i < 0 ? 99 : i).Select(kv => (Def(kv.Key), kv.Value))];
    }

    // ───────────── 저장: 폴더의 .foldy.md (또는 이 PC) ─────────────

    private static void Edit(string folder, Action<Dictionary<string, List<string>>> change)
    {
        lock (Gate)
        {
            var tags = FolderTags(folder);
            change(tags);
            WriteFolder(folder, tags);
        }
    }

    private static Dictionary<string, List<string>> FolderTags(string folder)
    {
        // 목록을 그릴 때마다 디스크를 보지 않도록, 같은 폴더는 2초에 한 번만 .foldy.md가 바뀌었는지 본다
        if (Folders.TryGetValue(folder, out var recent) && DateTime.UtcNow - recent.Checked < TimeSpan.FromSeconds(2))
        {
            return recent.Tags;
        }

        var file = Path.Combine(folder, ManagedFolders.FileName);
        DateTime stamp;
        try
        {
            stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stamp = DateTime.MinValue;
        }

        if (Folders.TryGetValue(folder, out var c) && c.Stamp == stamp)
        {
            Folders[folder] = (c.Tags, stamp, DateTime.UtcNow);
            return c.Tags;
        }

        var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (stamp != DateTime.MinValue)
        {
            try
            {
                tags = Parse(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        Folders[folder] = (tags, stamp, DateTime.UtcNow);
        return tags;
    }

    /// <summary>.foldy.md의 "파일 상태 태그" 칸을 읽는다: <c>- `이름`: 태그1, 태그2</c>.</summary>
    internal static Dictionary<string, List<string>> Parse(string text)
    {
        var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSection = line.Trim() == SectionTitle;
                continue;
            }

            if (!inSection || !line.StartsWith("- `", StringComparison.Ordinal))
            {
                continue;
            }

            var end = line.IndexOf("`:", 3, StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            var name = line[3..end];
            var list = line[(end + 2)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (name.Length > 0 && list.Count > 0)
            {
                tags[name] = list;
            }
        }

        return tags;
    }

    /// <summary>.foldy.md의 다른 내용(관리 방법 등)은 그대로 두고 태그 칸만 다시 쓴다. 태그가 없고 다른 내용도 없으면 파일을 지운다.</summary>
    internal static string Render(string existing, IReadOnlyDictionary<string, List<string>> tags)
    {
        var lines = existing.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var start = lines.FindIndex(l => l.Trim() == SectionTitle);
        if (start >= 0)
        {
            var end = lines.FindIndex(start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
            lines.RemoveRange(start, (end < 0 ? lines.Count : end) - start);
        }

        var rest = string.Join("\n", lines).TrimEnd();
        if (tags.Count == 0)
        {
            return rest.Length == 0 ? "" : rest + "\n";
        }

        var sb = new StringBuilder();
        if (rest.Length == 0)
        {
            sb.Append("# Foldy 폴더 메모\n\n이 파일은 Foldy가 이 폴더의 정보를 기억하는 메모예요. 지워도 괜찮아요 (파일은 그대로예요).\n");
        }
        else
        {
            sb.Append(rest).Append('\n');
        }

        sb.Append('\n').Append(SectionTitle).Append("\n\n");
        foreach (var (name, t) in tags.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("- `").Append(name).Append("`: ").Append(string.Join(", ", t)).Append('\n');
        }

        return sb.ToString();
    }

    private static void WriteFolder(string folder, Dictionary<string, List<string>> tags)
    {
        var file = Path.Combine(folder, ManagedFolders.FileName);
        try
        {
            var existing = File.Exists(file) ? File.ReadAllText(file) : "";
            var text = Render(existing, tags);
            if (File.Exists(file))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            if (text.Length == 0)
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            else
            {
                var created = !File.Exists(file);
                File.WriteAllText(file, text, new UTF8Encoding(true));
                File.SetAttributes(file, FileAttributes.Hidden);
                if (created)
                {
                    ExcludeFromGit(folder);
                }
            }

            Folders[folder] = (tags, File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 쓸 수 없는 폴더(읽기 전용 등): 이 PC에만 남긴다
            foreach (var (name, t) in tags)
            {
                _local[Path.Combine(folder, name)] = t;
            }

            SaveLocal();
        }
    }

    /// <summary>폴더가 들어 있는 Git 저장소의 .git 폴더 (없으면 null).</summary>
    public static string? GitDir(string folder)
    {
        for (var d = new DirectoryInfo(folder); d is not null; d = d.Parent)
        {
            var git = Path.Combine(d.FullName, ".git");
            if (Directory.Exists(git))
            {
                return git;
            }
        }

        return null;
    }

    private static string? ExcludeFile(string folder) => GitDir(folder) is { } g ? Path.Combine(g, "info", "exclude") : null;

    private static bool IsFoldyLine(string l) => l.Trim() is ".foldy.md" or "**/.foldy.md" or "# Foldy 폴더 메모 (태그)";

    /// <summary>
    /// 태그를 저장소에 함께 올리는지 (기본 끔: .git/info/exclude로 감춘다). 켜면 같이 쓰는 사람에게도 보인다.
    /// </summary>
    public static bool IsSharedInGit(string folder) =>
        ExcludeFile(folder) is { } f && !(File.Exists(f) && File.ReadAllLines(f).Any(l => l.Trim() is ".foldy.md" or "**/.foldy.md"));

    public static void SetSharedInGit(string folder, bool shared)
    {
        if (ExcludeFile(folder) is not { } f)
        {
            return;
        }

        try
        {
            if (shared)
            {
                if (File.Exists(f))
                {
                    File.WriteAllLines(f, File.ReadAllLines(f).Where(l => !IsFoldyLine(l)));
                }
            }
            else
            {
                ExcludeFromGit(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Git 저장소 안이면 .git/info/exclude에 ".foldy.md"를 한 번 넣는다 (커밋되지 않게, .gitignore는 그대로).</summary>
    internal static void ExcludeFromGit(string folder)
    {
        try
        {
            for (var d = new DirectoryInfo(folder); d is not null; d = d.Parent)
            {
                var git = Path.Combine(d.FullName, ".git");
                if (!Directory.Exists(git))
                {
                    continue;
                }

                var info = Path.Combine(git, "info");
                Directory.CreateDirectory(info);
                var exclude = Path.Combine(info, "exclude");
                var lines = File.Exists(exclude) ? File.ReadAllLines(exclude) : [];
                if (!lines.Any(l => l.Trim() is ".foldy.md" or "**/.foldy.md"))
                {
                    File.AppendAllText(exclude, (lines.Length > 0 && lines[^1].Length > 0 ? "\n" : "") + "# Foldy 폴더 메모 (태그)\n.foldy.md\n");
                }

                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void SaveLocal()
    {
        if (_localStore is null)
        {
            return;
        }

        try
        {
            var tmp = _localStore + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_local));
            File.Move(tmp, _localStore, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\');
}
