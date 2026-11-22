using System.IO.Enumeration;

namespace Foldy.Core.Cleanup;

/// <summary>지워도 되는 폴더 한 종류: 이름, 그 폴더가 맞는지 확인하는 방법, 왜 지워도 되는지.</summary>
/// <param name="Name">폴더 이름 (대소문자 무시).</param>
/// <param name="Why">사용자에게 보여 줄 한 줄 (다시 만드는 방법).</param>
/// <param name="Markers">옆(같은 부모 폴더)에 이 중 하나가 있어야 맞다고 본다. 비면 이름만으로.</param>
/// <param name="Inside">그 폴더 안에 이 파일이 있어야 맞다고 본다 (예: .venv의 pyvenv.cfg). null이면 보지 않는다.</param>
/// <param name="CanRebuild">다시 만들 수 있나. 아니면(.git) 기본으로 고르지 않고 경고한다.</param>
public sealed record RebuildableKind(string Name, string Why, IReadOnlyList<string> Markers, string? Inside = null, bool CanRebuild = true);

/// <summary>찾은 폴더 하나.</summary>
/// <param name="Path">폴더 전체 경로.</param>
/// <param name="Kind">종류.</param>
/// <param name="Size">크기 (바이트, 모르면 -1).</param>
/// <param name="LastWriteUtc">마지막으로 바뀐 때 (.git은 그 안의 index·HEAD 기준).</param>
/// <param name="Selected">기본으로 고르나.</param>
/// <param name="Warning">빨간 글씨로 보여 줄 경고 (없으면 null).</param>
public sealed record RebuildableFolder(string Path, RebuildableKind Kind, long Size, DateTime LastWriteUtc, bool Selected, string? Warning);

/// <summary>
/// 개발 찌꺼기 폴더 찾기 (npkill·kondo처럼, 2026-11-21 사용자 피드백 "백업 폴더의 .git·__history를 정리하고 싶다").
/// <list type="bullet">
/// <item>사용자가 고른 폴더 안에서만 찾는다. 찾은 폴더 안으로는 더 들어가지 않는다(node_modules 안의 node_modules).</item>
/// <item>프로젝트 파일이 옆에 있을 때만 그 이름을 찌꺼기로 본다(예: package.json 옆의 node_modules, .csproj 옆의 bin·obj). 그냥 "build"라는 이름의 폴더는 건드리지 않는다.</item>
/// <item>.git은 다시 만들 수 없으니 기본으로 고르지 않고, 최근에 바뀐 .git은 빨간 경고를 붙인다.</item>
/// <item>지우기는 이 클래스가 하지 않는다: 앱이 휴지통으로 보낸다(되살릴 수 있음).</item>
/// </list>
/// </summary>
public static class RebuildableFolders
{
    private static readonly string[] DotNet = ["*.csproj", "*.vbproj", "*.fsproj", "*.sln", "*.slnx"];
    private static readonly string[] Js = ["package.json"];

    /// <summary>기본 종류 (이름 순서는 화면에 보이는 순서).</summary>
    public static readonly IReadOnlyList<RebuildableKind> Defaults =
    [
        new("node_modules", Foldy.Core.Localization.Loc.T("rb.npm"), Js),
        new("bin", Foldy.Core.Localization.Loc.T("rb.dotnet"), DotNet),
        new("obj", Foldy.Core.Localization.Loc.T("rb.dotnet"), DotNet),
        new("target", Foldy.Core.Localization.Loc.T("rb.rustJava"), ["Cargo.toml", "pom.xml"]),
        new("__pycache__", Foldy.Core.Localization.Loc.T("rb.pycache"), []),
        new(".venv", Foldy.Core.Localization.Loc.T("rb.venv"), [], "pyvenv.cfg"),
        new("venv", Foldy.Core.Localization.Loc.T("rb.venv"), [], "pyvenv.cfg"),
        new("dist", Foldy.Core.Localization.Loc.T("rb.build"), ["package.json", "pyproject.toml", "setup.py"]),
        new("build", Foldy.Core.Localization.Loc.T("rb.build"), ["package.json", "build.gradle", "build.gradle.kts", "CMakeLists.txt", "pyproject.toml", "setup.py"]),
        new(".gradle", Foldy.Core.Localization.Loc.T("rb.gradle"), ["build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"]),
        new("__history", Foldy.Core.Localization.Loc.T("rb.history"), []),
        new("__recovery", Foldy.Core.Localization.Loc.T("rb.recovery"), []),
        new(".git", Foldy.Core.Localization.Loc.T("rb.git"), [], "HEAD", CanRebuild: false),
    ];

    /// <summary>"최근에 썼다"로 보는 기간.</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromDays(30);

    /// <summary>사용자가 더한 이름까지 합친 종류 목록. 더한 이름은 옆 파일을 보지 않고 이름만으로 찾는다.</summary>
    public static IReadOnlyList<RebuildableKind> With(IEnumerable<string>? extra)
    {
        var list = Defaults.ToList();
        foreach (var raw in extra ?? [])
        {
            var name = raw.Trim().Trim('\\', '/');
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".."
                || list.Any(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            list.Add(new RebuildableKind(name, Foldy.Core.Localization.Loc.T("rb.custom"), []));
        }

        return list;
    }

    /// <summary>
    /// root 안을 훑어 찾는다 (숨김·시스템 폴더 안, 바로 가기 폴더(링크)는 들어가지 않는다. .git·.venv는 숨김이어도 찾는다).
    /// sizeOf: 폴더 크기를 아는 방법(색인). null을 돌려주면 직접 더한다.
    /// </summary>
    public static IReadOnlyList<RebuildableFolder> Scan(string root, IReadOnlyList<RebuildableKind> kinds, DateTime nowUtc, Func<string, long?>? sizeOf = null, CancellationToken ct = default)
    {
        var byName = kinds.ToDictionary(k => k.Name, StringComparer.OrdinalIgnoreCase);
        var found = new List<RebuildableFolder>();
        var stack = new Stack<string>();
        stack.Push(root);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<(string Path, string Name, FileAttributes Attr)> subs;
            try
            {
                subs = [.. new FileSystemEnumerable<(string, string, FileAttributes)>(dir, (ref FileSystemEntry e) => (e.ToFullPath(), e.FileName.ToString(), e.Attributes), opts)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory,
                }];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var (path, name, attr) in subs)
            {
                if (byName.TryGetValue(name, out var kind) && Matches(dir, path, kind))
                {
                    found.Add(Describe(path, kind, nowUtc, sizeOf));
                    continue; // 찾은 폴더 안은 더 보지 않는다
                }

                if ((attr & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                {
                    stack.Push(path);
                }
            }
        }

        return [.. found.OrderByDescending(f => f.Size)];
    }

    /// <summary>그 이름의 폴더가 정말 찌꺼기인가 (옆의 프로젝트 파일, 안의 표시 파일).</summary>
    public static bool Matches(string parent, string path, RebuildableKind kind)
    {
        try
        {
            if (kind.Inside is { } inside && !File.Exists(Path.Combine(path, inside)))
            {
                return false;
            }

            return kind.Markers.Count == 0 || kind.Markers.Any(m => m.Contains('*', StringComparison.Ordinal)
                ? Directory.EnumerateFiles(parent, m).Any()
                : File.Exists(Path.Combine(parent, m)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static RebuildableFolder Describe(string path, RebuildableKind kind, DateTime nowUtc, Func<string, long?>? sizeOf)
    {
        var size = sizeOf?.Invoke(path) ?? SizeOf(path);
        var last = kind.CanRebuild ? SafeTime(() => Directory.GetLastWriteTimeUtc(path)) : GitLastUse(path);
        var recent = nowUtc - last < Recent;
        string? warning = null;
        if (!kind.CanRebuild)
        {
            warning = recent
                ? Foldy.Core.Localization.Loc.F("rb.gitRecent", (int)Math.Max(0, (nowUtc - last).TotalDays))
                : Foldy.Core.Localization.Loc.T("rb.gitWarn");
        }

        // 다시 만들 수 있고 최근에 안 쓴 것만 기본으로 고른다 (.git은 늘 사용자가 직접)
        return new RebuildableFolder(path, kind, size, last, kind.CanRebuild && !recent, warning);
    }

    /// <summary>.git을 마지막으로 쓴 때: 폴더 시각보다 index·HEAD·logs가 정확하다.</summary>
    private static DateTime GitLastUse(string git) =>
        new[] { "index", "HEAD", "FETCH_HEAD", Path.Combine("logs", "HEAD") }
            .Select(f => Path.Combine(git, f)).Where(File.Exists).Select(f => SafeTime(() => File.GetLastWriteTimeUtc(f)))
            .DefaultIfEmpty(SafeTime(() => Directory.GetLastWriteTimeUtc(git))).Max();

    private static DateTime SafeTime(Func<DateTime> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>폴더 크기를 직접 더한다 (색인이 없을 때). 바로 가기 폴더는 따라가지 않는다.</summary>
    public static long SizeOf(string dir)
    {
        try
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return new FileSystemEnumerable<long>(dir, (ref FileSystemEntry e) => e.Length, opts) { ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory }.Sum();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }
}
