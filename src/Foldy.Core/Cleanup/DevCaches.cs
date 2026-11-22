namespace Foldy.Core.Cleanup;

/// <summary>개발 도구 캐시 한 종류.</summary>
/// <param name="Id">짧은 이름.</param>
/// <param name="Name">화면에 보이는 이름.</param>
/// <param name="Exes">PATH에서 찾을 실행 파일 (처음 찾은 것을 쓴다). 없으면 그 도구가 없는 것.</param>
/// <param name="Args">공식 정리 명령의 인자 (실행 파일 뒤).</param>
/// <param name="Folders">캐시 폴더 (크기 계산용, %LOCALAPPDATA% 등은 펼친다). Docker처럼 폴더로 못 재면 비운다.</param>
/// <param name="Why">왜 지워도 되나 (공식 문서 요지).</param>
/// <param name="Note">하기 전에 알아 둘 것 (없으면 null).</param>
public sealed record DevCacheKind(string Id, string Name, IReadOnlyList<string> Exes, IReadOnlyList<string> Args, IReadOnlyList<string> Folders, string Why, string? Note = null);

/// <summary>이 PC에서 찾은 캐시 하나.</summary>
/// <param name="Kind">종류.</param>
/// <param name="Exe">실행할 파일 (전체 경로).</param>
/// <param name="Size">지금 크기 (바이트, 모르면 -1).</param>
public sealed record DevCache(DevCacheKind Kind, string Exe, long Size);

/// <summary>
/// 개발 도구 캐시 정리 (2026-11-21 사용자 승인). 폴더를 직접 지우지 않고 <b>각 도구의 공식 명령</b>만 실행한다.
/// 다시 받을 수 있는 캐시만 다룬다: Docker는 build cache만(<c>docker builder prune</c>), 이미지·컨테이너·볼륨은 건드리지 않는다.
/// 되살릴 수 없으니 앱은 크기를 보여 주고 한 번 확인한 뒤에만 실행한다.
/// </summary>
public static class DevCaches
{
    public static readonly IReadOnlyList<DevCacheKind> Kinds =
    [
        new("npm", "npm 캐시", ["npm.cmd", "npm.exe"], ["cache", "clean", "--force"], [@"%LOCALAPPDATA%\npm-cache", @"%APPDATA%\npm-cache"],
            "npm install 할 때 다시 받아요. npm 공식 문서도 공간을 늘릴 때 말고는 지울 필요가 없다고 해요."),
        new("nuget", "NuGet 패키지 캐시", ["dotnet.exe"], ["nuget", "locals", "all", "--clear"],
            [@"%USERPROFILE%\.nuget\packages", @"%LOCALAPPDATA%\NuGet\v3-cache", @"%LOCALAPPDATA%\NuGet\plugins-cache", @"%LOCALAPPDATA%\Temp\NuGet"],
            "다음 빌드(restore) 때 다시 받아요.", "Visual Studio를 닫고 하세요. 다음 빌드는 처음 한 번 오래 걸려요."),
        new("pip", "pip 캐시", ["py.exe", "python.exe"], ["-m", "pip", "cache", "purge"], [@"%LOCALAPPDATA%\pip\cache"],
            "pip install 할 때 다시 받아요."),
        new("yarn", "Yarn 캐시", ["yarn.cmd", "yarn.exe"], ["cache", "clean"], [@"%LOCALAPPDATA%\Yarn\Cache"],
            "yarn install 할 때 다시 받아요."),
        new("pnpm", "pnpm 저장소 (안 쓰는 것만)", ["pnpm.cmd", "pnpm.exe"], ["store", "prune"], [@"%LOCALAPPDATA%\pnpm\store"],
            "어느 프로젝트도 쓰지 않는 패키지만 지워요."),
        new("docker", "Docker 빌드 캐시", ["docker.exe"], ["builder", "prune", "--force"], [],
            "다음 docker build 때 다시 만들어요. 이미지·컨테이너·볼륨은 건드리지 않아요.", "Docker Desktop이 켜져 있어야 해요."),
    ];

    /// <summary>PATH에서 실행 파일을 찾는다 (없으면 null).</summary>
    public static string? FindOnPath(IEnumerable<string> exes, string? path)
    {
        foreach (var dir in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var exe in exes)
            {
                try
                {
                    // PATH의 상대 경로(".", "bin" 등)는 Foldy가 지금 서 있는 폴더 기준이 되어, 그 폴더에 놓인 npm.cmd를 실행할 수 있다: 전체 경로만 본다
                    var d = dir.Trim('"');
                    if (!Path.IsPathFullyQualified(d))
                    {
                        continue;
                    }

                    var p = Path.Combine(d, exe);
                    if (File.Exists(p) && !p.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) // Store의 python.exe 바로 가기(설치 안내)는 뺀다
                    {
                        return p;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return null;
    }

    /// <summary>이 PC에 있는 캐시들 (도구가 있고, 폴더로 재는 것은 크기가 0보다 클 때). sizeOf는 시험에서 바꾼다.</summary>
    public static IReadOnlyList<DevCache> Find(string? path, Func<string, string> expand, Func<string, long>? sizeOf = null)
    {
        sizeOf ??= d => Directory.Exists(d) ? RebuildableFolders.SizeOf(d) : 0;
        var list = new List<DevCache>();
        foreach (var k in Kinds)
        {
            if (FindOnPath(k.Exes, path) is not { } exe)
            {
                continue;
            }

            if (k.Folders.Count == 0)
            {
                list.Add(new DevCache(k, exe, -1));
                continue;
            }

            var size = k.Folders.Select(expand).Distinct(StringComparer.OrdinalIgnoreCase).Sum(d => Math.Max(0, sizeOf(d)));
            if (size > 0)
            {
                list.Add(new DevCache(k, exe, size));
            }
        }

        return list;
    }

    /// <summary>실행할 명령: .cmd·.bat은 cmd.exe /c를 거친다 (인자는 고정 목록이라 사용자 입력이 섞이지 않는다).</summary>
    public static (string File, IReadOnlyList<string> Args) Command(DevCache c, string cmdExe)
    {
        var ext = Path.GetExtension(c.Exe);
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            ? (cmdExe, ["/d", "/c", c.Exe, .. c.Kind.Args])
            : (c.Exe, c.Kind.Args);
    }
}
