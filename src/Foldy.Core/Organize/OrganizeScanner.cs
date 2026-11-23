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

    /// <summary>
    /// 확장자로 아는 프로젝트 파일. 델파이·C++Builder(.dproj, .groupproj, .dpr, .dpk, .cbproj, .bdsproj), VB(.vbp, .vbproj), F#(.fsproj),
    /// 예전 Visual C++(.vcproj, .dsp, .dsw)도 넣는다 (사용자 2026-11-23: 델파이 소스 폴더가 파일 단위로 흩어질 뻔했다).
    /// </summary>
    private static readonly string[] ProjectMarkerExtensions =
    [
        ".sln", ".slnx", ".csproj", ".vcxproj", ".xcodeproj", ".psd", ".prproj", ".aep", ".blend", ".uproject",
        ".dproj", ".groupproj", ".dpr", ".dpk", ".cbproj", ".bdsproj", ".vbp", ".vbproj", ".fsproj", ".vcproj", ".dsp", ".dsw",
    ];

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

    public static string ProjectRootReason => Foldy.Core.Localization.Loc.T("os.project");

    /// <summary>정리하면 안 되는 폴더면 이유, 아니면 null. <paramref name="allowProject"/>면 프로젝트 폴더도 허용한다.</summary>
    public static string? CheckRoot(string root, bool allowProject = false)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(full))
        {
            return Foldy.Core.Localization.Loc.T("os.noFolder");
        }

        if (FolderDescriptionService.IsSystemFolder(full))
        {
            return Foldy.Core.Localization.Loc.T("os.system");
        }

        if (!allowProject && IsProjectFolder(full))
        {
            return ProjectRootReason;
        }

        var attrs = File.GetAttributes(full);
        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            return Foldy.Core.Localization.Loc.T("os.link");
        }

        return null;
    }

    /// <summary>
    /// 폴더를 훑는다. 폴더마다 목록은 딱 한 번만 읽고(프로젝트 폴더 표시도 같은 목록에서 찾는다),
    /// 하위 폴더는 여러 개를 동시에 읽는다 (이 PC 드라이브 4개, 네트워크 드라이브 1개).
    /// <paramref name="counter"/>가 있으면 지금까지 센 파일·폴더·용량을 채운다 (화면은 그것을 가끔 읽어 보여 준다).
    /// </summary>
    public static OrganizeScan Scan(string root, bool includeSubfolders, OrganizeScanCounter? counter = null, int? parallelism = null, CancellationToken ct = default)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var scan = new OrganizeScan { Root = full };
        var walker = new Walker(scan, includeSubfolders, counter ?? new OrganizeScanCounter(), ct);
        walker.Run(full, parallelism ?? DefaultParallelism(full));
        scan.Entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath));
        scan.Skipped.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath));
        return scan;
    }

    /// <summary>동시에 읽을 폴더 수: 네트워크 드라이브는 서버에 부담을 주지 않게 1개.</summary>
    public static int DefaultParallelism(string root)
    {
        try
        {
            if (root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Network)
            {
                return 1;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return 1;
        }

        return Math.Clamp(Environment.ProcessorCount, 1, 4);
    }

    /// <summary>목록 한 줄 (FileSystemInfo를 만들지 않아 빠르다).</summary>
    private readonly record struct Item(string Name, string FullPath, FileAttributes Attributes, bool IsDirectory, long Length, DateTime LastWriteUtc, DateTime LastAccessUtc);

    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false,
    };

    /// <summary>폴더 하나의 목록 (열 수 없으면 null과 이유).</summary>
    private static (List<Item>? Items, string? Error) List(string dir)
    {
        try
        {
            var e = new System.IO.Enumeration.FileSystemEnumerable<Item>(
                dir,
                (ref System.IO.Enumeration.FileSystemEntry x) => new Item(
                    x.FileName.ToString(), x.ToFullPath(), x.Attributes, x.IsDirectory, x.IsDirectory ? 0 : x.Length,
                    x.LastWriteTimeUtc.UtcDateTime, x.LastAccessTimeUtc.UtcDateTime),
                ListOptions);
            return (e.ToList(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>목록에 프로젝트 표시(.git, .sln, package.json …)가 있는가. <paramref name="dir"/>이 사용자 폴더면 .claude는 빼고 본다.</summary>
    private static bool HasProjectMarker(string dir, List<Item> items)
    {
        var home = string.Equals(dir.TrimEnd('\\'), UserHome, StringComparison.OrdinalIgnoreCase);
        foreach (var i in items)
        {
            if (home && i.Name.Equals(".claude", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (MarkerNames.Contains(i.Name) || MarkerExtensions.Contains(Path.GetExtension(i.Name)))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly HashSet<string> MarkerNames = new(ProjectMarkers, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MarkerExtensions = new(ProjectMarkerExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 여러 스레드가 함께 쓰는 폴더 줄. 폴더 하나를 읽으면 그 안의 파일은 바로 기록하고, 하위 폴더는 줄에 넣는다.
    /// 결과 순서는 나중에 경로로 정렬해 늘 같게 만든다.
    /// </summary>
    private sealed class Walker(OrganizeScan scan, bool deep, OrganizeScanCounter counter, CancellationToken ct)
    {
        private readonly System.Collections.Concurrent.ConcurrentStack<(string Dir, List<Item> Items)> _work = new();
        private readonly object _gate = new();
        private int _pending;
        private Exception? _error;

        public void Run(string root, int parallelism)
        {
            var (items, error) = List(root);
            if (items is null)
            {
                scan.Skipped.Add(new(root, Foldy.Core.Localization.Loc.T("os.cantOpen") + error));
                return;
            }

            _pending = 1;
            _work.Push((root, items));
            if (parallelism <= 1)
            {
                Worker();
            }
            else
            {
                var threads = Enumerable.Range(0, parallelism).Select(_ => new Thread(Worker) { IsBackground = true, Name = "Foldy organize scan" }).ToList();
                threads.ForEach(t => t.Start());
                threads.ForEach(t => t.Join());
            }

            if (_error is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_error);
            }

            ct.ThrowIfCancellationRequested();
        }

        private void Worker()
        {
            var spin = new SpinWait();
            while (Volatile.Read(ref _pending) > 0)
            {
                if (_error is not null || ct.IsCancellationRequested)
                {
                    return;
                }

                if (!_work.TryPop(out var job))
                {
                    spin.SpinOnce(sleep1Threshold: 10);
                    continue;
                }

                spin.Reset();
                try
                {
                    Process(job.Dir, job.Items);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref _error, ex, null);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }

        private void Process(string dir, List<Item> items)
        {
            var isTop = string.Equals(dir, scan.Root, StringComparison.OrdinalIgnoreCase);
            var entries = new List<OrganizeEntry>();
            var skipped = new List<SkippedEntry>();
            long files = 0, bytes = 0, folders = 0;
            foreach (var info in items)
            {
                ct.ThrowIfCancellationRequested();
                var a = info.Attributes;
                if ((a & FileAttributes.ReparsePoint) != 0)
                {
                    skipped.Add(new(info.FullPath, Foldy.Core.Localization.Loc.T("os.linkSkip")));
                    continue;
                }

                if ((a & FileAttributes.System) != 0)
                {
                    skipped.Add(new(info.FullPath, Foldy.Core.Localization.Loc.T("os.systemFile")));
                    continue;
                }

                if ((a & FileAttributes.Hidden) != 0 || NeverMove.Contains(info.Name))
                {
                    skipped.Add(new(info.FullPath, Foldy.Core.Localization.Loc.T("os.hiddenFile")));
                    continue;
                }

                if (info.IsDirectory)
                {
                    folders++;

                    // 하위 폴더를 읽을 일이 있을 때만(프로젝트 표시 확인 또는 안으로 들어가기) 목록을 한 번 읽는다
                    if (!isTop && !deep)
                    {
                        continue;
                    }

                    var (sub, error) = List(info.FullPath);
                    var project = sub is not null && HasProjectMarker(info.FullPath, sub);
                    if (isTop || project)
                    {
                        // 맨 위 단계 폴더는 항목으로 (규칙에 맞을 때만 통째로 옮김)
                        entries.Add(new OrganizeEntry
                        {
                            FullPath = info.FullPath, Name = info.Name, IsFolder = true, IsProjectFolder = project,
                            LastWriteUtc = info.LastWriteUtc, LastAccessUtc = info.LastAccessUtc, Attributes = a,
                        });
                    }

                    if (deep && !project)
                    {
                        if (sub is null)
                        {
                            skipped.Add(new(info.FullPath, Foldy.Core.Localization.Loc.T("os.cantOpen") + error));
                        }
                        else
                        {
                            Interlocked.Increment(ref _pending);
                            _work.Push((info.FullPath, sub));
                        }
                    }
                }
                else
                {
                    files++;
                    bytes += info.Length;
                    entries.Add(new OrganizeEntry
                    {
                        FullPath = info.FullPath, Name = info.Name, Size = info.Length,
                        LastWriteUtc = info.LastWriteUtc, LastAccessUtc = info.LastAccessUtc, Attributes = a,
                    });
                }
            }

            lock (_gate)
            {
                scan.Entries.AddRange(entries);
                scan.Skipped.AddRange(skipped);
            }

            counter.Add(files, folders, bytes);
        }
    }
}

/// <summary>훑는 동안 센 수 (여러 스레드가 더하고, 화면이 가끔 읽는다).</summary>
public sealed class OrganizeScanCounter
{
    private long _files;
    private long _folders;
    private long _bytes;

    public long Files => Interlocked.Read(ref _files);

    public long Folders => Interlocked.Read(ref _folders);

    public long Bytes => Interlocked.Read(ref _bytes);

    public void Add(long files, long folders, long bytes)
    {
        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _folders, folders);
        Interlocked.Add(ref _bytes, bytes);
    }
}
