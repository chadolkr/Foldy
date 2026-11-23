using Foldy.Core.Organize;
using Foldy.Presentation.Services;
using Foldy.Presentation.Templates;

namespace Foldy.Templates.Tests;

/// <summary>
/// 큰 폴더 정리 (사용자 2026-11-23: 파일 80만 개·폴더 11만 개에서 계획 만들기가 끝나지 않음).
/// 한 번만·동시에 훑기, 이름 겹침은 메모리에서, 중복은 앞뒤 조각 먼저, 트리는 폴더마다 200개까지, [뒤에서 하기].
/// </summary>
public class BigFolderOrganizeTests
{
    private static void MakeTree(TempDir tmp, int folders, int filesPerFolder)
    {
        for (var d = 0; d < folders; d++)
        {
            for (var f = 0; f < filesPerFolder; f++)
            {
                tmp.File($@"d{d % 7}\s{d}\f{f}.{(f % 3 == 0 ? "pdf" : f % 3 == 1 ? "jpg" : "mp3")}", "x");
            }
        }
    }

    [Fact]
    public void Parallel_scan_finds_the_same_as_one_thread_and_counts()
    {
        using var tmp = new TempDir();
        MakeTree(tmp, folders: 30, filesPerFolder: 5);
        tmp.File(@"proj\.git\HEAD", "ref");
        tmp.File(@"proj\code.cs", "x");
        tmp.File(@"d1\inner\Cargo.toml", "[package]");
        tmp.File(@"d1\inner\deep\never.txt", "x");

        var counter = new OrganizeScanCounter();
        var many = OrganizeScanner.Scan(tmp.Path, includeSubfolders: true, counter: counter, parallelism: 4);
        var one = OrganizeScanner.Scan(tmp.Path, includeSubfolders: true, parallelism: 1);

        many.Entries.Select(e => e.FullPath).Should().Equal(one.Entries.Select(e => e.FullPath), "결과 순서는 늘 같다");
        many.Skipped.Select(e => e.FullPath).Should().Equal(one.Skipped.Select(e => e.FullPath));
        many.Entries.Count(e => !e.IsFolder).Should().Be(150);
        many.Entries.Should().Contain(e => e.IsFolder && e.IsProjectFolder && e.Name == "proj");
        many.Entries.Should().Contain(e => e.IsFolder && e.IsProjectFolder && e.Name == "inner", "프로젝트 폴더는 안쪽에 있어도 통째로 다룬다");
        many.Entries.Should().NotContain(e => e.Name == "never.txt" || e.Name == "code.cs", "프로젝트 폴더 안으로는 들어가지 않는다");
        counter.Files.Should().Be(150);
        counter.Folders.Should().BeGreaterThan(30);
        counter.Bytes.Should().Be(150);
    }

    [Fact]
    public void Scan_can_be_cancelled()
    {
        using var tmp = new TempDir();
        MakeTree(tmp, folders: 10, filesPerFolder: 2);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => OrganizeScanner.Scan(tmp.Path, includeSubfolders: true, cts.Token, parallelism: 4);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Exists_cache_gives_the_same_names_as_asking_the_disk()
    {
        using var tmp = new TempDir();
        tmp.File("a.pdf", "new");
        tmp.File(@"문서\a.pdf", "old");
        tmp.File(@"문서\a (2).pdf", "old2");
        var scan = OrganizeScanner.Scan(tmp.Path, includeSubfolders: false);
        var opts = new OrganizeOptions { Mode = OrganizeMode.ByType, DetectDuplicates = false };
        var cached = OrganizePlanner.Plan(scan, opts, new PathExistsCache().Exists);
        var disk = OrganizePlanner.Plan(scan, opts, p => File.Exists(p) || Directory.Exists(p));
        cached.Moves.Select(m => m.Destination).Should().Equal(disk.Moves.Select(m => m.Destination));
        cached.Moves.Single().Destination.Should().Be(tmp[@"문서\a (3).pdf"]);
    }

    [Fact]
    public void Duplicates_compare_ends_first_then_whole_file()
    {
        using var tmp = new TempDir();
        var size = (Sha256ContentHasher.EndChunk * 2) + 1000;
        var same = new byte[size];
        new Random(1).NextBytes(same);
        var middle = (byte[])same.Clone();
        middle[size / 2] ^= 0xFF; // 앞뒤 조각은 같고 가운데만 다르다
        Directory.CreateDirectory(tmp["a"]);
        Directory.CreateDirectory(tmp["b"]);
        Directory.CreateDirectory(tmp["c"]);
        File.WriteAllBytes(tmp[@"a\movie.bin"], same);
        File.WriteAllBytes(tmp[@"b\movie.bin"], same);
        File.WriteAllBytes(tmp[@"c\movie.bin"], middle);
        File.SetLastWriteTimeUtc(tmp[@"a\movie.bin"], new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Sha256ContentHasher.Instance.HashEnds(tmp[@"a\movie.bin"]).Should().Be(Sha256ContentHasher.Instance.HashEnds(tmp[@"c\movie.bin"]));
        var scan = OrganizeScanner.Scan(tmp.Path, includeSubfolders: true);
        var plan = OrganizePlanner.Plan(scan, new OrganizeOptions { Mode = OrganizeMode.ByType }, new PathExistsCache().Exists);
        var dups = plan.Moves.Where(m => m.TargetFolder == OrganizeOptions.DuplicatesFolder || m.TargetFolder == OrganizeOptions.DuplicatesFolderKo).ToList();
        dups.Should().ContainSingle().Which.Source.Should().Be(tmp[@"b\movie.bin"], "가운데만 다른 파일은 중복이 아니고, 가장 오래된 것은 원래 규칙대로");
    }

    [Fact]
    public void Planning_reports_progress_and_can_be_cancelled()
    {
        using var tmp = new TempDir();
        MakeTree(tmp, folders: 4, filesPerFolder: 5);
        var scan = OrganizeScanner.Scan(tmp.Path, includeSubfolders: true);
        var progress = new OrganizePlanProgress();
        var plan = OrganizePlanner.Plan(scan, new OrganizeOptions(), new PathExistsCache().Exists, progress: progress);
        progress.Stage.Should().Be(OrganizePlanStage.Planning);
        progress.Done.Should().Be(progress.Total).And.Be(20);
        plan.Moves.Should().HaveCount(20);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => OrganizePlanner.Plan(scan, new OrganizeOptions(), new PathExistsCache().Exists, ct: cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Tree_shows_at_most_200_files_per_folder()
    {
        var paths = Enumerable.Range(0, 1000).Select(i => $@"문서\f{i:D4}.pdf").Append(@"문서\안\x.pdf").ToList();
        var tree = OrganizeTreeNode.Build(paths);
        var docs = tree.Single();
        docs.Count.Should().Be(1001);
        docs.Children.Count(c => !c.IsFolder && !c.IsMore).Should().Be(OrganizeTreeNode.MaxFilesPerFolder);
        docs.Children.Should().ContainSingle(c => c.IsMore).Which.Name.Should().Contain("800");
        docs.Children.Should().ContainSingle(c => c.IsFolder && c.Name == "안");
    }

    [Fact]
    public async Task Big_folder_offers_background_and_finishes_with_a_notice()
    {
        using var tmp = new TempDir();
        MakeTree(tmp, folders: 6, filesPerFolder: 4);
        var savedCount = OrganizeViewModel.LargeItemCount;
        var savedTick = OrganizeViewModel.TickInterval;
        OrganizeViewModel.LargeItemCount = 0;
        OrganizeViewModel.TickInterval = TimeSpan.FromMilliseconds(10);
        try
        {
            using var gate = new ManualResetEventSlim();
            var vm = new OrganizeViewModel(tmp.Path, new InlineDispatcher(), journalFolder: tmp["jobs"],
                scanner: (root, deep, ct) =>
                {
                    _ = gate.Wait(TimeSpan.FromSeconds(20), ct); // 큰 폴더처럼 오래 걸리게
                    return OrganizeScanner.Scan(root, deep, ct);
                })
            {
                SizeHint = (_, _) => Task.FromResult<long?>(93L * 1024 * 1024 * 1024),
            };
            vm.IncludeSubfolders = true;
            var finished = 0;
            vm.PlanFinished += (_, _) => finished++;

            var run = vm.PlanCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.ShowBackgroundOffer);
            vm.TotalBytes.Should().Be(93L * 1024 * 1024 * 1024, "색인이 아는 전체 크기를 바로 보여 준다");
            vm.ScanInfo.Should().Contain("93");

            vm.RunInBackgroundCommand.Execute(null);
            vm.IsInBackground.Should().BeTrue();
            vm.ShowBackgroundOffer.Should().BeFalse();
            vm.DialogResult.Should().BeFalse("창은 닫히고 계획은 계속 만든다");
            vm.State.Should().Be(OrganizeState.Planning);

            gate.Set();
            await run;
            finished.Should().Be(1);
            vm.State.Should().Be(OrganizeState.Ready);
            vm.Moves.Should().HaveCount(24);

            vm.PrepareToShowAgain();
            vm.DialogResult.Should().BeNull();
            vm.IsInBackground.Should().BeFalse();
        }
        finally
        {
            OrganizeViewModel.LargeItemCount = savedCount;
            OrganizeViewModel.TickInterval = savedTick;
        }
    }

    [Fact]
    public async Task Keep_waiting_hides_the_offer_for_this_plan()
    {
        using var tmp = new TempDir();
        tmp.File(@"a\b.txt");
        var savedCount = OrganizeViewModel.LargeItemCount;
        var savedTick = OrganizeViewModel.TickInterval;
        OrganizeViewModel.LargeItemCount = 0;
        OrganizeViewModel.TickInterval = TimeSpan.FromMilliseconds(10);
        try
        {
            using var gate = new ManualResetEventSlim();
            var vm = new OrganizeViewModel(tmp.Path, new InlineDispatcher(), journalFolder: tmp["jobs"],
                scanner: (root, deep, ct) =>
                {
                    _ = gate.Wait(TimeSpan.FromSeconds(20), ct);
                    return OrganizeScanner.Scan(root, deep, ct);
                });
            vm.IncludeSubfolders = true;
            var run = vm.PlanCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.ShowBackgroundOffer);
            vm.KeepWaitingCommand.Execute(null);
            await Task.Delay(100);
            vm.ShowBackgroundOffer.Should().BeFalse("한 번 기다리기로 했으면 다시 묻지 않는다");
            gate.Set();
            await run;
            vm.State.Should().Be(OrganizeState.Ready);
            vm.IsInBackground.Should().BeFalse();
        }
        finally
        {
            OrganizeViewModel.LargeItemCount = savedCount;
            OrganizeViewModel.TickInterval = savedTick;
        }
    }

    [Fact]
    public async Task Many_files_plan_quickly_and_big_plans_redraw_in_the_background()
    {
        using var tmp = new TempDir();
        MakeTree(tmp, folders: 100, filesPerFolder: 30);
        var saved = OrganizeViewModel.SyncEditLimit;
        OrganizeViewModel.SyncEditLimit = 100;
        try
        {
            var vm = new OrganizeViewModel(tmp.Path, new InlineDispatcher(), journalFolder: tmp["jobs"]) { IncludeSubfolders = true };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await vm.PlanCommand.ExecuteAsync(null);
            sw.Stop();
            vm.State.Should().Be(OrganizeState.Ready);
            vm.Moves.Should().HaveCount(3000);
            vm.AfterTree.Should().OnlyContain(n => n.Children.Count(c => !c.IsFolder && !c.IsMore) <= OrganizeTreeNode.MaxFilesPerFolder);
            vm.AfterTree.Should().Contain(n => n.Children.Any(c => c.IsMore), "폴더마다 200개를 넘으면 \"… 외 N개\"로 줄인다");
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));

            // 큰 계획에서 체크를 바꾸면 바로 돌아오고, 트리·요약은 잠시 뒤 뒤에서 다시 그린다
            var before = vm.Summary;
            vm.Moves[0].IsIncluded = false;
            await WaitUntil(() => vm.Summary != before);
            vm.Plan!.IncludedMoves.Should().HaveCount(2999);
        }
        finally
        {
            OrganizeViewModel.SyncEditLimit = saved;
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(20))
            {
                throw new TimeoutException("기다리던 상태가 오지 않았습니다");
            }

            await Task.Delay(10);
        }
    }
}
