using Foldy.Core.Descriptions;

namespace Foldy.Core.Organize;

/// <summary>
/// 정리할 폴더 열거 (드라이브 큐에서 호출). 안전 규칙: 시스템·숨김 파일, 심볼릭 링크·정션은 건너뛰고
/// 프로젝트 폴더 안으로는 들어가지 않는다. 폴더 설명 파일은 절대 옮기지 않는다.
/// </summary>
public static class OrganizeScanner
{
    /// <summary>이것이 들어 있는 폴더는 프로젝트 폴더로 보고 쪼개지 않는다.</summary>
    private static readonly string[] ProjectMarkers =
    [
        ".git", ".hg", ".svn", "package.json", "Cargo.toml", "pyproject.toml", "setup.py", "go.mod", "pom.xml",
        "build.gradle", "build.gradle.kts", "CMakeLists.txt", "Makefile", "composer.json", "Gemfile", ".foldy-project",
        // AI 프로젝트 (Claude Code 등): 정리 도구가 구조를 건드리지 않는다
        ".claude", "CLAUDE.md", "AGENTS.md", "SKILL.md",
    ];

    private static readonly string[] ProjectMarkerExtensions = [".sln", ".slnx", ".csproj", ".vcxproj", ".xcodeproj", ".psd", ".prproj", ".aep", ".blend", ".uproject"];

    private static readonly HashSet<string> NeverMove = new(StringComparer.OrdinalIgnoreCase)
    {
        FolderDescription.FileName, "desktop.ini", "thumbs.db", ".DS_Store",
    };

    // 사용자 폴더(C:\Users\이름)의 .claude는 Claude Code의 사용자 설정이지 프로젝트 표시가 아니다
    private static readonly string UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');

    public static bool IsProjectFolder(string folder)
    {
        try
        {
            var home = string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), UserHome, StringComparison.OrdinalIgnoreCase);
            foreach (var e in Directory.EnumerateFileSystemEntries(folder))
            {
                var name = Path.GetFileName(e);
                if (home && name.Equals(".claude", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ProjectMarkers.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || ProjectMarkerExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    public const string ProjectRootReason = "Git 저장소·개발·AI 프로젝트 폴더(.git, .sln, package.json, .claude, CLAUDE.md 등이 있음)는 정리하지 않습니다. 파일을 옮기면 빌드나 Git 기록이 깨질 수 있어요. 프로젝트 밖의 일반 폴더에서 정리해 주세요.";

    /// <summary>정리하면 안 되는 폴더면 이유, 아니면 null. <paramref name="allowProject"/>면 프로젝트 폴더도 허용한다.</summary>
    public static string? CheckRoot(string root, bool allowProject = false)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(full))
        {
            return "폴더가 없습니다.";
        }

        if (FolderDescriptionService.IsSystemFolder(full))
        {
            return "Windows·프로그램 설치 폴더는 정리하지 않습니다.";
        }

        if (!allowProject && IsProjectFolder(full))
        {
            return ProjectRootReason;
        }

        var attrs = File.GetAttributes(full);
        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            return "링크·정션 폴더는 따라가지 않습니다.";
        }

        return null;
    }

    public static OrganizeScan Scan(string root, bool includeSubfolders, CancellationToken ct = default)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var scan = new OrganizeScan { Root = full };
        Walk(new DirectoryInfo(full), includeSubfolders, top: true, scan, ct);
        return scan;
    }

    private static void Walk(DirectoryInfo dir, bool deep, bool top, OrganizeScan scan, CancellationToken ct)
    {
        FileSystemInfo[] items;
        try
        {
            items = dir.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            scan.Skipped.Add(new(dir.FullName, "열 수 없음: " + ex.Message));
            return;
        }

        foreach (var info in items)
        {
            ct.ThrowIfCancellationRequested();
            var a = info.Attributes;
            if ((a & FileAttributes.ReparsePoint) != 0)
            {
                scan.Skipped.Add(new(info.FullName, "심볼릭 링크·정션은 따라가지 않음"));
                continue;
            }

            if ((a & FileAttributes.System) != 0)
            {
                scan.Skipped.Add(new(info.FullName, "시스템 파일"));
                continue;
            }

            if ((a & FileAttributes.Hidden) != 0 || NeverMove.Contains(info.Name))
            {
                scan.Skipped.Add(new(info.FullName, "숨김 파일"));
                continue;
            }

            if (info is DirectoryInfo d)
            {
                var project = IsProjectFolder(d.FullName);
                if (top || project)
                {
                    // 맨 위 단계 폴더는 항목으로 (규칙에 맞을 때만 통째로 옮김)
                    scan.Entries.Add(new OrganizeEntry
                    {
                        FullPath = d.FullName, Name = d.Name, IsFolder = true, IsProjectFolder = project,
                        LastWriteUtc = d.LastWriteTimeUtc, LastAccessUtc = d.LastAccessTimeUtc, Attributes = a,
                    });
                }

                if (deep && !project)
                {
                    Walk(d, deep, top: false, scan, ct);
                }
            }
            else if (info is FileInfo f)
            {
                scan.Entries.Add(new OrganizeEntry
                {
                    FullPath = f.FullName, Name = f.Name, Size = f.Length,
                    LastWriteUtc = f.LastWriteTimeUtc, LastAccessUtc = f.LastAccessTimeUtc, Attributes = a,
                });
            }
        }
    }
}
