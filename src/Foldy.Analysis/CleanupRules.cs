using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foldy.Analysis;

public enum CleanupRuleKind { Dir, File }

/// <summary>cleanup-rules.json 규칙 하나 (설계서 9장). 의미는 Rust foldy-index analysis/rules.rs와 같다.</summary>
public sealed class CleanupRule
{
    public string Id { get; set; } = "";

    public string? Tag { get; set; }

    public string? Title { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<CleanupRuleKind>))]
    public CleanupRuleKind Kind { get; set; } = CleanupRuleKind.Dir;

    public IReadOnlyList<string> Names { get; set; } = [];

    public IReadOnlyList<string> Paths { get; set; } = [];

    public IReadOnlyList<string> ParentHas { get; set; } = [];

    public IReadOnlyList<string> Extensions { get; set; } = [];

    public int? OlderThanDays { get; set; }

    public long? MinSize { get; set; }

    public string? Reason { get; set; }

    public bool Suggest { get; set; } = true;

    public string ReasonText => Reason ?? Title ?? "";
}

/// <summary>폴더 규칙 판정 결과. Suggest=false면 태그만.</summary>
public sealed record CleanupVerdict(CleanupRule Rule, bool Suggest);

/// <summary>
/// 정리 규칙 엔진. 이름·경로 패턴으로 태그를 붙이고 조건(크기, 오래됨)을 만족하면 정리 제안을 낸다.
/// 시스템 폴더는 규칙과 상관없이 <see cref="IsProtected"/>가 막는다.
/// </summary>
public sealed class CleanupRules
{
    private const long FileTimeDay = 864_000_000_000;

    private CleanupRules(int schemaVersion, IReadOnlyList<CleanupRule> rules)
    {
        SchemaVersion = schemaVersion;
        Rules = rules;
    }

    public int SchemaVersion { get; }

    public IReadOnlyList<CleanupRule> Rules { get; }

    public bool HasFileRules => Rules.Any(r => r.Kind == CleanupRuleKind.File && r.Suggest);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class FileShape
    {
        public int SchemaVersion { get; set; }

        public List<CleanupRule> Rules { get; set; } = [];
    }

    /// <exception cref="FormatException">JSON이 깨졌거나 모르는 버전.</exception>
    public static CleanupRules Parse(string json)
    {
        FileShape? f;
        try
        {
            f = JsonSerializer.Deserialize<FileShape>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("cleanup-rules.json: " + ex.Message, ex);
        }

        if (f is null || f.SchemaVersion > 1)
        {
            throw new FormatException("cleanup-rules.json: 모르는 schemaVersion");
        }

        foreach (var r in f.Rules)
        {
            r.Names = [.. r.Names.Select(s => s.ToLowerInvariant())];
            r.Paths = [.. r.Paths.Select(s => NormalizeRel(s.ToLowerInvariant()))];
            r.ParentHas = [.. r.ParentHas.Select(s => s.ToLowerInvariant())];
            r.Extensions = [.. r.Extensions.Select(s => s.TrimStart('.').ToLowerInvariant())];
        }

        return new CleanupRules(f.SchemaVersion, f.Rules);
    }

    /// <summary>앱에 들어 있는 기본 규칙 (assets/cleanup-rules.json).</summary>
    public static CleanupRules Default { get; } = LoadEmbedded();

    private static CleanupRules LoadEmbedded()
    {
        using var s = typeof(CleanupRules).Assembly.GetManifestResourceStream("Foldy.Analysis.cleanup-rules.json")
            ?? throw new InvalidOperationException("기본 cleanup-rules.json 리소스가 없음");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    /// <summary>%LOCALAPPDATA%\Foldy\cleanup-rules.json이 있으면 그것을, 없거나 깨졌으면 기본값.</summary>
    public static CleanupRules LoadUserOrDefault()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy", "cleanup-rules.json");
        try
        {
            return File.Exists(p) ? Parse(File.ReadAllText(p)) : Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return Default;
        }
    }

    /// <param name="siblings">부모 폴더 항목 이름들(소문자). parentHas가 필요할 때만, 한 번만 부른다.</param>
    public CleanupVerdict? MatchDir(string name, string relPath, string fullPath, long alloc, long newestMtime, bool systemOrReparse, long now, Func<IReadOnlyList<string>> siblings)
    {
        ArgumentNullException.ThrowIfNull(siblings);
        var n = name.ToLowerInvariant();
        var rel = relPath.ToLowerInvariant();
        IReadOnlyList<string>? sib = null;
        foreach (var r in Rules)
        {
            if (r.Kind != CleanupRuleKind.Dir || (r.Names.Count == 0 && r.Paths.Count == 0))
            {
                continue;
            }

            if (r.Names.Count > 0 && !r.Names.Any(p => Glob(p, n)))
            {
                continue;
            }

            if (r.Paths.Count > 0 && !r.Paths.Any(p => Glob(p, rel)))
            {
                continue;
            }

            if (r.ParentHas.Count > 0)
            {
                sib ??= siblings();
                if (!r.ParentHas.Any(p => sib.Any(s => Glob(p, s))))
                {
                    continue;
                }
            }

            var suggest = r.Suggest && !systemOrReparse && !IsProtected(fullPath) && Conditions(r, alloc, newestMtime, now);
            return new CleanupVerdict(r, suggest);
        }

        return null;
    }

    /// <summary>제안할 파일 규칙 (없으면 null).</summary>
    public CleanupRule? MatchFile(string name, string ext, string relPath, string fullPath, long alloc, long mtime, bool systemOrReparse, long now)
    {
        string? n = null;
        string? rel = null;
        foreach (var r in Rules)
        {
            if (r.Kind != CleanupRuleKind.File || !r.Suggest || (r.Names.Count == 0 && r.Paths.Count == 0 && r.Extensions.Count == 0))
            {
                continue;
            }

            if (r.Extensions.Count > 0 && !r.Extensions.Contains(ext))
            {
                continue;
            }

            if (!Conditions(r, alloc, mtime, now))
            {
                continue;
            }

            if (r.Names.Count > 0)
            {
                n ??= name.ToLowerInvariant();
                if (!r.Names.Any(p => Glob(p, n)))
                {
                    continue;
                }
            }

            if (r.Paths.Count > 0)
            {
                rel ??= relPath.ToLowerInvariant();
                if (!r.Paths.Any(p => Glob(p, rel)))
                {
                    continue;
                }
            }

            return systemOrReparse || IsProtectedFile(fullPath) ? null : r;
        }

        return null;
    }

    private static bool Conditions(CleanupRule r, long alloc, long newest, long now)
    {
        if (r.MinSize is { } min && alloc < min)
        {
            return false;
        }

        if (r.OlderThanDays is { } days && (newest <= 0 || now - newest < days * FileTimeDay))
        {
            return false;
        }

        return true;
    }

    private static string NormalizeRel(string p)
    {
        p = p.Replace('/', '\\');
        return p.StartsWith('\\') ? p : "\\" + p;
    }

    private static readonly HashSet<string> ProtectedTop = new(StringComparer.Ordinal)
    {
        "windows", "program files", "program files (x86)", "programdata", "$recycle.bin", "system volume information",
        "recovery", "boot", "efi", "perflogs", "$winreagent", "$windows.~bt", "$windows.~ws", "windows.old", "config.msi",
        "msocache", "intel", "amd", "nvidia", "drivers",
    };

    private static readonly HashSet<string> ProfileRootDirs = new(StringComparer.Ordinal)
    {
        "appdata", "desktop", "documents", "downloads", "pictures", "music", "videos", "onedrive",
    };

    private static readonly HashSet<string> OsMicrosoftDirs = new(StringComparer.Ordinal)
    {
        "windows", "windowsapps", "credentials", "protect", "crypto", "vault", "systemcertificates",
    };

    /// <summary>시스템 보호 폴더: 이 아래는 어떤 규칙도 정리를 제안하지 않는다. Rust is_protected와 같다.</summary>
    public static bool IsProtected(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        var p = fullPath.Replace('/', '\\').ToLowerInvariant();
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        var colon = p.IndexOf(':', StringComparison.Ordinal);
        var rel = colon is >= 0 and <= 2 ? p[(colon + 1)..] : p;
        var segs = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0)
        {
            return true;
        }

        if (ProtectedTop.Contains(segs[0]))
        {
            return true;
        }

        if (segs[0] == "users")
        {
            if (segs.Length <= 2 || segs[1] is "default" or "default user" or "all users" or "public")
            {
                return true;
            }

            var tail = segs.AsSpan(2);
            if (tail.Length <= 1 && ProfileRootDirs.Contains(tail[0]))
            {
                return true;
            }

            if (tail.Length <= 2 && tail[0] == "appdata")
            {
                return true;
            }

            if (tail.Length >= 3 && tail[0] == "appdata" && tail[2] == "microsoft" && (tail.Length == 3 || OsMicrosoftDirs.Contains(tail[3])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 파일 보호: 경로가 보호 폴더 아래이거나, 보호 폴더 바로 안의 파일(볼륨 루트의 pagefile.sys, 프로필 루트의 NTUSER.DAT 등).
    /// 단 다운로드·문서 같은 사용자 콘텐츠 폴더는 폴더 자체만 보호하고 그 안 파일은 제안할 수 있다. Rust is_protected_file과 같다.
    /// </summary>
    public static bool IsProtectedFile(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        if (IsProtected(fullPath))
        {
            return true;
        }

        var p = fullPath.Replace('/', '\\').TrimEnd('\\');
        var i = p.LastIndexOf('\\');
        if (i < 0)
        {
            return true;
        }

        var parent = p[..i];
        var parentName = parent[(parent.LastIndexOf('\\') + 1)..].ToLowerInvariant();
        return IsProtected(parent) && !(ProfileRootDirs.Contains(parentName) && parentName != "appdata");
    }

    /// <summary>대소문자 구분 없는 글롭 (둘 다 소문자로 준다). <c>*</c>는 <c>\</c>를 넘지 않고 <c>**</c>는 넘는다.</summary>
    public static bool Glob(string pattern, string s)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(s);
        return GlobAt(pattern.AsSpan(), s.AsSpan());
    }

    private static bool GlobAt(ReadOnlySpan<char> p, ReadOnlySpan<char> s)
    {
        if (p.IsEmpty)
        {
            return s.IsEmpty;
        }

        if (p[0] == '*')
        {
            if (p.Length > 1 && p[1] == '*')
            {
                var rest = p[2..];
                for (var i = 0; i <= s.Length; i++)
                {
                    if (GlobAt(rest, s[i..]))
                    {
                        return true;
                    }
                }

                return false;
            }

            var r1 = p[1..];
            for (var i = 0; i <= s.Length; i++)
            {
                if (GlobAt(r1, s[i..]))
                {
                    return true;
                }

                if (i < s.Length && s[i] == '\\')
                {
                    break;
                }
            }

            return false;
        }

        if (s.IsEmpty)
        {
            return false;
        }

        if (p[0] == '?')
        {
            return s[0] != '\\' && GlobAt(p[1..], s[1..]);
        }

        return s[0] == p[0] && GlobAt(p[1..], s[1..]);
    }
}
