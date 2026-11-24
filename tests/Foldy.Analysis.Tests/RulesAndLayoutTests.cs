using Foldy.Analysis;

namespace Foldy.Analysis.Tests;

public class RulesTests
{
    private static readonly long Now = TestData.Ft(TestData.Now);
    private const long Day = 864_000_000_000;

    [Fact]
    public void Glob_semantics_match_rust()
    {
        CleanupRules.Glob("node_modules", "node_modules").Should().BeTrue();
        CleanupRules.Glob("*.csproj", "app.csproj").Should().BeTrue();
        CleanupRules.Glob("*.csproj", "app.csproj.user").Should().BeFalse();
        CleanupRules.Glob(@"\users\*\appdata\local\temp", @"\users\me\appdata\local\temp").Should().BeTrue();
        CleanupRules.Glob(@"\users\*\appdata\local\temp", @"\users\me\x\appdata\local\temp").Should().BeFalse("*는 \\를 넘지 않는다");
        CleanupRules.Glob(@"\users\*\downloads\**", @"\users\me\downloads\a\b\setup.exe").Should().BeTrue();
        CleanupRules.Glob("a?c", "abc").Should().BeTrue();
        CleanupRules.Glob("a?c", @"a\c").Should().BeFalse();
        CleanupRules.Glob("**", "").Should().BeTrue();
    }

    [Fact]
    public void Default_rules_tag_and_suggest()
    {
        var rs = CleanupRules.Default;
        rs.Rules.Count.Should().BeGreaterThanOrEqualTo(10);
        string[] pkg = ["package.json", "src"];
        var v = rs.MatchDir("node_modules", @"\Users\me\proj\node_modules", @"C:\Users\me\proj\node_modules", 50 << 20, Now - (60 * Day), false, Now, () => pkg)!;
        v.Rule.Tag.Should().Be("node_modules");
        v.Suggest.Should().BeTrue();
        rs.MatchDir("node_modules", @"\x\node_modules", @"D:\x\node_modules", 50 << 20, Now - (2 * Day), false, Now, () => pkg)!.Suggest.Should().BeFalse("최근에 바뀌었으면 태그만");
        rs.MatchDir("node_modules", @"\x\node_modules", @"D:\x\node_modules", 50 << 20, Now - (60 * Day), false, Now, () => []).Should().BeNull("package.json 옆이 아니면 아니다");
        rs.MatchDir("bin", @"\src\app\bin", @"D:\src\app\bin", 20 << 20, Now - (30 * Day), false, Now, () => ["app.csproj"])!.Rule.Id.Should().Be("dotnet-bin-obj");
        var git = rs.MatchDir(".git", @"\src\app\.git", @"D:\src\app\.git", 5L << 30, Now - (400 * Day), false, Now, () => [])!;
        git.Rule.Tag.Should().Be("git");
        git.Suggest.Should().BeFalse(".git은 태그만");
        rs.MatchDir("Temp", @"\Users\me\AppData\Local\Temp", @"C:\Users\me\AppData\Local\Temp", 1L << 30, Now - (10 * Day), false, Now, () => [])!.Rule.Id.Should().Be("user-temp");
        rs.MatchDir("node_modules", @"\a\node_modules", @"D:\a\node_modules", 50 << 20, Now - (60 * Day), systemOrReparse: true, Now, () => pkg)!.Suggest.Should().BeFalse();
    }

    [Fact]
    public void Newer_rules_need_their_project_files()
    {
        var rs = CleanupRules.Default;
        var old = Now - (60 * Day);
        rs.MatchDir("Library", @"\Games\g\Library", @"D:\Games\g\Library", 2L << 30, old, false, Now, () => ["assets", "projectsettings", "packages"])!.Rule.Id.Should().Be("unity-library");
        rs.MatchDir("Library", @"\Books\Library", @"D:\Books\Library", 2L << 30, old, false, Now, () => ["책"]).Should().BeNull("Unity 프로젝트가 아니면 아니다");
        rs.MatchDir("Intermediate", @"\u\g\Intermediate", @"D:\u\g\Intermediate", 1L << 30, old, false, Now, () => ["g.uproject"])!.Rule.Id.Should().Be("unreal-intermediate");
        rs.MatchDir("Saved", @"\u\g\Saved", @"D:\u\g\Saved", 1L << 30, old, false, Now, () => ["g.uproject"]).Should().BeNull("Unreal Saved는 설정·자동 저장이라 제안하지 않는다");
        rs.MatchDir("build", @"\f\app\build", @"D:\f\app\build", 50 << 20, old, false, Now, () => ["pubspec.yaml"])!.Rule.Id.Should().Be("dart-build");
        rs.MatchDir("cmake-build-debug", @"\c\cmake-build-debug", @"D:\c\cmake-build-debug", 50 << 20, old, false, Now, () => ["cmakelists.txt"])!.Rule.Id.Should().Be("cmake-ide-build");
        rs.MatchDir("cache2", @"\Users\me\AppData\Local\Mozilla\Firefox\Profiles\ab.default\cache2", @"C:\Users\me\AppData\Local\Mozilla\Firefox\Profiles\ab.default\cache2", 500 << 20, old, false, Now, () => [])!.Rule.Id.Should().Be("browser-cache");
        rs.MatchDir("go-build", @"\Users\me\AppData\Local\go-build", @"C:\Users\me\AppData\Local\go-build", 1L << 30, old, false, Now, () => [])!.Suggest.Should().BeTrue();
        rs.MatchFile("big.iso.crdownload", "crdownload", @"\Users\me\Downloads\big.iso.crdownload", @"C:\Users\me\Downloads\big.iso.crdownload", 50 << 20, Now - (20 * Day), false, Now)!.Id.Should().Be("partial-downloads");
        rs.MatchFile("big.iso.crdownload", "crdownload", @"\Users\me\Downloads\big.iso.crdownload", @"C:\Users\me\Downloads\big.iso.crdownload", 50 << 20, Now - (2 * Day), false, Now).Should().BeNull("받는 중일 수 있다");
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"C:\Program Files\App\tmp")]
    [InlineData(@"C:\Program Files (x86)\x\node_modules")]
    [InlineData(@"C:\ProgramData\temp")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\me")]
    [InlineData(@"C:\Users\me\AppData")]
    [InlineData(@"C:\Users\me\AppData\Local")]
    [InlineData(@"C:\Users\me\Downloads")]
    [InlineData(@"C:\Users\Public\x")]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\Windows\INetCache")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5")]
    [InlineData(@"C:\")]
    [InlineData(@"\\?\C:\Windows\System32")]
    public void System_folders_are_protected(string path)
    {
        CleanupRules.IsProtected(path).Should().BeTrue();
        // 무엇이든 맞는 규칙 파일이라도 보호 폴더는 제안되지 않는다
        var evil = CleanupRules.Parse("""{"rules":[{"id":"all","names":["*"]},{"id":"f","kind":"file","extensions":["dll"]}]}""");
        var name = Path.GetFileName(path.TrimEnd('\\'));
        evil.MatchDir(name.Length == 0 ? "x" : name, path[2..], path, 10L << 30, Now - (365 * Day), false, Now, () => [])?.Suggest.Should().NotBe(true);
        // 다운로드 같은 사용자 콘텐츠 폴더는 폴더 자체만 보호 (안의 파일은 제안 가능)
        if (path.EndsWith("Downloads", StringComparison.Ordinal))
        {
            return;
        }

        evil.MatchFile("kernel32.dll", "dll", path[2..] + @"\kernel32.dll", path + @"\kernel32.dll", 1 << 20, Now - (400 * Day), false, Now).Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\Users\me\proj\node_modules")]
    [InlineData(@"D:\work\bin")]
    [InlineData(@"C:\Users\me\AppData\Local\Temp")]
    [InlineData(@"C:\Users\me\Downloads\setup.exe")]
    public void Ordinary_folders_are_not_protected(string path) => CleanupRules.IsProtected(path).Should().BeFalse();

    [Fact]
    public void File_rules()
    {
        var rs = CleanupRules.Default;
        rs.MatchFile("setup.exe", "exe", @"\Users\me\Downloads\setup.exe", @"C:\Users\me\Downloads\setup.exe", 100 << 20, Now - (120 * Day), false, Now)!.Id.Should().Be("old-installers");
        rs.MatchFile("setup.exe", "exe", @"\Users\me\Downloads\setup.exe", @"C:\Users\me\Downloads\setup.exe", 100 << 20, Now - (10 * Day), false, Now).Should().BeNull();
        rs.MatchFile("setup.exe", "exe", @"\Users\me\Documents\setup.exe", @"C:\Users\me\Documents\setup.exe", 100 << 20, Now - (120 * Day), false, Now).Should().BeNull();
        rs.MatchFile("crash.dmp", "dmp", @"\Users\me\dumps\crash.dmp", @"C:\Users\me\dumps\crash.dmp", 50 << 20, Now - (30 * Day), false, Now)!.Id.Should().Be("crash-dumps");
        rs.MatchFile("crash.dmp", "dmp", @"\Users\me\crash.dmp", @"C:\Users\me\crash.dmp", 50 << 20, Now - (30 * Day), false, Now).Should().BeNull("프로필 루트 바로 안의 파일은 보호");
        rs.MatchFile("big.dmp", "dmp", @"\big.dmp", @"C:\big.dmp", 50 << 20, Now - (30 * Day), false, Now).Should().BeNull("볼륨 루트 바로 안의 파일은 보호");
        rs.MatchFile("x.dmp", "dmp", @"\Windows\Minidump\x.dmp", @"C:\Windows\Minidump\x.dmp", 50 << 20, Now - (30 * Day), false, Now).Should().BeNull();
    }

    [Fact]
    public void Broken_json_is_an_error()
    {
        FluentActions.Invoking(() => CleanupRules.Parse("{")).Should().Throw<FormatException>();
        FluentActions.Invoking(() => CleanupRules.Parse("""{"rules":[{"id":"x","kind":"weird"}]}""")).Should().Throw<FormatException>();
        FluentActions.Invoking(() => CleanupRules.Parse("""{"schemaVersion":2,"rules":[]}""")).Should().Throw<FormatException>();
    }
}

public class TreemapLayoutTests
{
    private static readonly TreemapRect Bounds = new(10, 20, 800, 500);

    [Theory]
    [InlineData(new double[] { 6, 6, 4, 3, 2, 2, 1 })]
    [InlineData(new double[] { 1000, 1, 1, 1 })]
    [InlineData(new double[] { 5 })]
    [InlineData(new double[] { 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3 })]
    public void Areas_are_proportional_inside_bounds_and_disjoint(double[] sizes)
    {
        var r = SquarifiedLayout.Layout(sizes, Bounds);
        r.Should().HaveCount(sizes.Length);
        var total = sizes.Sum();
        for (var i = 0; i < r.Length; i++)
        {
            r[i].Area.Should().BeApproximately(Bounds.Area * sizes[i] / total, Bounds.Area * 1e-6);
            r[i].X.Should().BeGreaterThanOrEqualTo(Bounds.X - 1e-6);
            r[i].Y.Should().BeGreaterThanOrEqualTo(Bounds.Y - 1e-6);
            r[i].Right.Should().BeLessThanOrEqualTo(Bounds.Right + 1e-6);
            r[i].Bottom.Should().BeLessThanOrEqualTo(Bounds.Bottom + 1e-6);
            for (var j = i + 1; j < r.Length; j++)
            {
                var ox = Math.Min(r[i].Right, r[j].Right) - Math.Max(r[i].X, r[j].X);
                var oy = Math.Min(r[i].Bottom, r[j].Bottom) - Math.Max(r[i].Y, r[j].Y);
                (ox <= 1e-6 || oy <= 1e-6).Should().BeTrue($"{i}와 {j}가 겹치면 안 된다");
            }
        }

        r.Sum(x => x.Area).Should().BeApproximately(Bounds.Area, Bounds.Area * 1e-6, "빈틈 없이 채운다");
    }

    [Fact]
    public void Squarified_keeps_aspect_ratios_reasonable()
    {
        // 논문의 예 (6,6,4,3,2,2,1) 를 6x4에: 가장 나쁜 비율이 3 이하
        var r = SquarifiedLayout.Layout([6, 6, 4, 3, 2, 2, 1], new TreemapRect(0, 0, 6, 4));
        r.Max(x => Math.Max(x.Width / x.Height, x.Height / x.Width)).Should().BeLessThan(3);
        // 같은 크기 16개는 거의 정사각형
        var q = SquarifiedLayout.Layout([.. Enumerable.Repeat(1.0, 16)], new TreemapRect(0, 0, 400, 400));
        q.Max(x => Math.Max(x.Width / x.Height, x.Height / x.Width)).Should().BeLessThan(1.6);
    }

    [Fact]
    public void Zero_sizes_and_empty_bounds_and_hit_test()
    {
        var r = SquarifiedLayout.Layout([5, 0, -1, 5], Bounds);
        r[1].Area.Should().Be(0);
        r[2].Area.Should().Be(0);
        (r[0].Area + r[3].Area).Should().BeApproximately(Bounds.Area, 1e-6);
        SquarifiedLayout.Layout([1, 2], new TreemapRect(0, 0, 0, 100)).Should().OnlyContain(x => x.Area == 0);
        SquarifiedLayout.Layout([], Bounds).Should().BeEmpty();
        var c0 = r[0];
        SquarifiedLayout.HitTest(r, c0.X + (c0.Width / 2), c0.Y + (c0.Height / 2)).Should().Be(0);
        SquarifiedLayout.HitTest(r, -5, -5).Should().Be(-1);
    }
}
