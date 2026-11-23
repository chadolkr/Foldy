using Foldy.Core.Cleanup;

namespace Foldy.Core.Tests;

public sealed class RebuildableFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "foldy-junk-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);

    public RebuildableFoldersTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }

        Directory.Delete(_root, true);
    }

    private string Make(string rel, string? file = null, int bytes = 10)
    {
        var d = Path.Combine(_root, rel);
        Directory.CreateDirectory(d);
        if (file is not null)
        {
            File.WriteAllBytes(Path.Combine(d, file), new byte[bytes]);
        }

        return d;
    }

    private static void Age(string dir, int days) => Directory.SetLastWriteTimeUtc(dir, Now.AddDays(-days));

    [Fact]
    public void Finds_only_folders_next_to_their_project_files()
    {
        Make(@"web", "package.json");
        Age(Make(@"web\node_modules\left-pad", "index.js", 1000), 100);
        Make(@"web\node_modules\a\node_modules\b", "x.js"); // node_modules 안의 node_modules는 따로 세지 않는다
        Age(Path.Combine(_root, @"web\node_modules"), 100);
        Make(@"app", "App.csproj");
        Age(Make(@"app\bin\Debug", "App.dll", 500), 200);
        Age(Path.Combine(_root, @"app\bin"), 200);
        Make(@"photos\build", "사진.jpg"); // 그냥 "build"라는 이름의 폴더는 건드리지 않는다
        Make(@"py\.venv", "아무거나.txt"); // pyvenv.cfg가 없으면 가상 환경이 아니다

        var found = RebuildableFolders.Scan(_root, RebuildableFolders.Defaults, Now);
        found.Select(f => Path.GetRelativePath(_root, f.Path)).Should().BeEquivalentTo([@"web\node_modules", @"app\bin"]);
        found[0].Size.Should().BeGreaterThan(found[1].Size, "큰 것부터");
        found.Should().OnlyContain(f => f.Selected, "오래 안 쓴 다시 만들 수 있는 폴더는 기본으로 고른다");
    }

    [Fact]
    public void Git_is_never_preselected_and_recent_git_gets_a_red_warning()
    {
        var old = Make(@"백업\옛프로젝트\.git", "HEAD");
        File.SetLastWriteTimeUtc(Path.Combine(old, "HEAD"), Now.AddDays(-400));
        Directory.SetLastWriteTimeUtc(old, Now.AddDays(-400));
        var fresh = Make(@"지금프로젝트\.git", "HEAD");
        File.SetLastWriteTimeUtc(Path.Combine(fresh, "HEAD"), Now.AddDays(-50));
        File.WriteAllText(Path.Combine(fresh, "index"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(fresh, "index"), Now.AddDays(-2));
        Make(@"백업\옛프로젝트\__history", "Unit1.pas.~1~");

        var found = RebuildableFolders.Scan(_root, RebuildableFolders.Defaults, Now);
        var gits = found.Where(f => f.Kind.Name == ".git").ToList();
        gits.Should().HaveCount(2).And.OnlyContain(g => !g.Selected && g.Warning != null);
        gits.Single(g => g.Path == fresh).Warning.Should().Contain("2일 전에도 쓴");
        found.Should().Contain(f => f.Kind.Name == "__history", "Delphi 편집기 사본은 옆 파일 없이도 찾는다");
    }

    [Fact]
    public void Recently_used_folders_are_found_but_not_preselected()
    {
        Make(@"web", "package.json");
        Make(@"web\node_modules", "a.js"); // 방금 바뀜
        RebuildableFolders.Scan(_root, RebuildableFolders.Defaults, DateTime.UtcNow).Single().Selected.Should().BeFalse();
    }

    [Fact]
    public void User_added_names_are_matched_by_name_and_bad_names_ignored()
    {
        Make(@"a\.cache", "x");
        var kinds = RebuildableFolders.With([" .cache ", "..", "a|b", "node_modules"]);
        kinds.Count(k => k.Name == "node_modules").Should().Be(1);
        kinds.Should().NotContain(k => k.Name == ".." || k.Name == "a|b");
        RebuildableFolders.Scan(_root, kinds, Now).Should().ContainSingle(f => f.Kind.Name == ".cache");
    }

    [Fact]
    public void Index_size_is_used_when_given()
    {
        Make(@"rs", "Cargo.toml");
        Make(@"rs\target", "x");
        RebuildableFolders.Scan(_root, RebuildableFolders.Defaults, Now, _ => 123_456).Single().Size.Should().Be(123_456);
    }

    [Fact]
    public void Progress_counts_folders_and_reports_each_found_folder_as_it_goes()
    {
        Make(@"web", "package.json");
        Make(@"web\node_modules\a", "a.js", 300);
        Make(@"web\src\deep\deeper", "app.js");
        Make(@"py\__pycache__", "m.pyc", 200);

        var counter = new RebuildableScanCounter();
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        var found = RebuildableFolders.Scan(_root, RebuildableFolders.Defaults, Now, counter: counter, onFound: f => seen.Add(f.Path));

        found.Should().HaveCount(2);
        seen.Should().BeEquivalentTo(found.Select(f => f.Path), "찾는 대로 하나씩 알린다");
        counter.Found.Should().Be(2);
        counter.FoundBytes.Should().Be(500);
        counter.Folders.Should().BeGreaterThanOrEqualTo(6, "살펴본 폴더 수(뿌리·web·src·deep·deeper·py, 찾은 폴더)를 센다");
        counter.Current.Should().NotBeNull();
    }

    [Fact]
    public void Reading_several_folders_at_once_finds_the_same_folders()
    {
        for (var i = 0; i < 30; i++)
        {
            Make($@"p{i}", "package.json");
            Make($@"p{i}\node_modules\x", "x.js", i + 1);
            Make($@"p{i}\obj", "o.txt"); // csproj가 없으니 아니다
            Make($@"p{i}\docs\a\b", "readme.md");
        }

        var one = RebuildableFolders.Scan(_root, RebuildableFolders.Defaults, Now, parallelism: 1);
        var four = RebuildableFolders.Scan(_root, RebuildableFolders.Defaults, Now, parallelism: 4);
        one.Should().HaveCount(30);
        four.Select(f => f.Path).Should().Equal(one.Select(f => f.Path), "여러 개를 동시에 읽어도 결과와 순서는 같다");
    }

    [Fact]
    public void Stopping_ends_the_search_and_keeps_what_was_found()
    {
        for (var i = 0; i < 20; i++)
        {
            Make($@"p{i}", "package.json");
            Make($@"p{i}\node_modules", "x.js");
        }

        using var cts = new CancellationTokenSource();
        var seen = new List<RebuildableFolder>();
        var act = () => RebuildableFolders.Scan(_root, RebuildableFolders.Defaults, Now, onFound: f =>
        {
            seen.Add(f);
            cts.Cancel(); // 하나 찾자마자 [그만 찾기]
        }, parallelism: 1, ct: cts.Token);

        act.Should().Throw<OperationCanceledException>();
        seen.Should().HaveCount(1, "멈추기 전에 찾은 것은 이미 받았다");
    }

    [Fact]
    public void Hidden_folders_are_not_entered()
    {
        var hidden = Make(@"숨김");
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);
        Make(@"숨김\web", "package.json");
        Make(@"숨김\web\node_modules", "x.js");
        RebuildableFolders.Scan(_root, RebuildableFolders.Defaults, Now).Should().BeEmpty("숨김 폴더 안은 들어가지 않는다");
    }
}

public sealed class DevCachesTests : IDisposable
{
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "foldy-devbin-" + Guid.NewGuid().ToString("N"));

    public DevCachesTests() => Directory.CreateDirectory(_bin);

    public void Dispose() => Directory.Delete(_bin, true);

    [Fact]
    public void Only_installed_tools_with_a_cache_are_offered()
    {
        File.WriteAllText(Path.Combine(_bin, "npm.cmd"), "");
        File.WriteAllText(Path.Combine(_bin, "docker.exe"), "");
        File.WriteAllText(Path.Combine(_bin, "dotnet.exe"), "");
        var found = DevCaches.Find(_bin, s => s, d => d.Contains(@"LOCALAPPDATA%\npm-cache", StringComparison.Ordinal) ? 5_000 : 0);
        found.Select(c => c.Kind.Id).Should().Equal("npm", "docker"); // NuGet은 캐시가 비어서 빠진다, Docker는 크기를 몰라도 보인다
        found[0].Size.Should().Be(5_000, "npm 캐시 폴더 두 곳 중 있는 것만 더한다");
    }

    [Fact]
    public void Commands_are_the_official_ones_and_cmd_files_go_through_cmd()
    {
        var npm = new DevCache(DevCaches.Kinds.Single(k => k.Id == "npm"), @"C:\Program Files\nodejs\npm.cmd", 1);
        var (file, args) = DevCaches.Command(npm, @"C:\Windows\System32\cmd.exe");
        file.Should().EndWith("cmd.exe");
        args.Should().Equal("/d", "/c", @"C:\Program Files\nodejs\npm.cmd", "cache", "clean", "--force");

        DevCaches.Kinds.Single(k => k.Id == "docker").Args.Should().Equal(["builder", "prune", "--force"], "Docker는 빌드 캐시만: 이미지·컨테이너·볼륨은 건드리지 않는다");
        DevCaches.Kinds.Should().NotContain(k => k.Args.Contains("system") || k.Args.Contains("--all") || k.Args.Contains("-a"));
    }

    [Fact]
    public void Store_python_stub_is_not_taken_for_python()
    {
        var store = Path.Combine(_bin, "WindowsApps");
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "python.exe"), "");
        DevCaches.FindOnPath(["python.exe"], store).Should().BeNull();
    }

    [Fact]
    public void Relative_path_entries_are_ignored()
    {
        // PATH의 "."이나 "bin"은 Foldy가 지금 서 있는 폴더 기준이라, 그 폴더에 놓인 파일을 실행하게 될 수 있다
        var here = Directory.GetCurrentDirectory();
        var planted = Path.Combine(here, "foldy-test-npm.cmd");
        File.WriteAllText(planted, "");
        try
        {
            DevCaches.FindOnPath(["foldy-test-npm.cmd"], ".;.\\").Should().BeNull();
            DevCaches.FindOnPath(["foldy-test-npm.cmd"], here).Should().Be(planted);
        }
        finally
        {
            File.Delete(planted);
        }
    }
}

public sealed class DesktopSuggestTests
{
    [Fact]
    public void Counts_loose_files_without_shortcuts_and_asks_only_above_twenty()
    {
        var d = Path.Combine(Path.GetTempPath(), "foldy-desk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(d, "폴더"));
        for (var i = 0; i < 21; i++)
        {
            File.WriteAllText(Path.Combine(d, $"문서{i}.txt"), "x");
        }

        File.WriteAllText(Path.Combine(d, "프로그램.lnk"), "x");
        File.WriteAllText(Path.Combine(d, "사이트.url"), "x");
        try
        {
            var n = Foldy.Core.Desktop.DesktopTidy.CountLoose(d);
            n.Should().Be(21, "바로 가기와 폴더는 세지 않는다");
            var now = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
            Foldy.Core.Desktop.DesktopTidy.ShouldSuggest(n, true, null, now).Should().BeTrue();
            Foldy.Core.Desktop.DesktopTidy.ShouldSuggest(20, true, null, now).Should().BeFalse("20개는 넘어야");
            Foldy.Core.Desktop.DesktopTidy.ShouldSuggest(n, false, null, now).Should().BeFalse("다시 묻지 않기");
            Foldy.Core.Desktop.DesktopTidy.ShouldSuggest(n, true, now.AddDays(3), now).Should().BeFalse("나중에: 7일 동안은 묻지 않는다");
            Foldy.Core.Desktop.DesktopTidy.ShouldSuggest(n, true, now.AddDays(-1), now).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(d, true);
        }
    }
}
