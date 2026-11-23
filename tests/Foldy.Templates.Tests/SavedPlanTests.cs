using Foldy.Core.Organize;
using Foldy.Presentation.Services;
using Foldy.Presentation.Templates;

namespace Foldy.Templates.Tests;

/// <summary>
/// 정리 계획 저장·이어서 하기 (사용자 2026-11-23): 큰 폴더의 계획과 체크를 DB에 두고 다음에 그대로 이어서 한다.
/// 정리하기 직전에는 크기·수정한 날을 다시 보고, 그사이 바뀌거나 없어진 파일은 옮기지 않는다.
/// </summary>
public class SavedPlanTests
{
    private static readonly DateTime Old = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    private static OrganizePlan PlanOf(TempDir tmp) =>
        OrganizePlanner.Plan(OrganizeScanner.Scan(tmp.Path, includeSubfolders: true), new OrganizeOptions { Mode = OrganizeMode.ByType }, Exists);

    private static TempDir Messy()
    {
        var tmp = new TempDir();
        tmp.File(@"a\보고서.pdf", "same", Old);
        tmp.File(@"b\보고서.pdf", "same", New);
        tmp.File("사진.jpg", "p", New);
        tmp.File("노래.mp3", "m", New);
        tmp.File("이상한.xyz", "x", New);
        return tmp;
    }

    private static OrganizeViewModel NewVm(TempDir tmp, OrganizePlanStore store) =>
        new(tmp.Path, new InlineDispatcher(), journalFolder: tmp["jobs"])
        {
            PlanStore = store, SaveMinItems = 0, EditSaveDelay = TimeSpan.FromMilliseconds(10),
        };

    [Fact]
    public void Save_and_load_keeps_checks_keeper_skipped_and_settings()
    {
        using var tmp = Messy();
        var store = new OrganizePlanStore(tmp["db\\plans.db"]);
        var plan = PlanOf(tmp);
        plan.Moves.Single(m => m.Source.EndsWith("노래.mp3", StringComparison.Ordinal)).Included = false;
        var set = plan.DuplicateSets.Single();
        OrganizePlanner.ChooseKeeper(plan, set, set.Members[1], Exists);
        plan.Skipped.Add(new SkippedEntry(tmp["잠긴.bin"], "사용 중"));

        var settings = new SavedPlanSettings { Mode = OrganizeMode.ByDate, DateFormat = "yyyy", IncludeSubfolders = true, PrefixDate = true };
        store.Save(PlanSnapshot.Of(plan), settings);

        var info = store.Find(tmp.Path + "\\");
        info.Should().NotBeNull("끝의 \\나 대소문자가 달라도 같은 폴더");
        info!.Moves.Should().Be(plan.Moves.Count);
        info.Checked.Should().Be(plan.Moves.Count - 1);

        var loaded = store.Load(tmp.Path.ToUpperInvariant())!;
        loaded.Should().NotBeNull();
        loaded.Settings.Should().BeEquivalentTo(settings);
        var p = loaded.Plan;
        p.Root.Should().Be(tmp.Path);
        p.Moves.Select(m => (m.Source, m.Destination, m.TargetFolder, m.Reason, m.Included, m.Size, m.SourceLastWriteUtc))
            .Should().Equal(plan.Moves.Select(m => (m.Source, m.Destination, m.TargetFolder, m.Reason, m.Included, m.Size, m.SourceLastWriteUtc)));
        p.Skipped.Should().ContainSingle(s => s.Reason == "사용 중");
        var lset = p.DuplicateSets.Single();
        lset.Keeper.FullPath.Should().Be(tmp[@"b\보고서.pdf"], "고른 남길 파일이 그대로");
        lset.Members.Select(m => m.KeepTarget).Should().Equal(set.Members.Select(m => m.KeepTarget));
        p.Moves.Where(m => m.DuplicateSet is not null).Should().OnlyContain(m => m.DuplicateSet == lset);
    }

    [Fact]
    public void Update_writes_only_what_changed()
    {
        using var tmp = Messy();
        var store = new OrganizePlanStore(tmp["plans.db"]);
        var plan = PlanOf(tmp);
        var handle = store.Save(PlanSnapshot.Of(plan), new SavedPlanSettings());

        store.Update(handle, PlanSnapshot.Of(plan)).Should().Be(0, "안 바뀌었으면 쓰지 않는다");

        plan.Moves[0].Included = false;
        store.Update(handle, PlanSnapshot.Of(plan)).Should().Be(1);
        store.Update(handle, PlanSnapshot.Of(plan)).Should().Be(0);

        // 남길 파일을 바꾸면 줄이 바뀌거나 생긴다
        var set = plan.DuplicateSets.Single();
        OrganizePlanner.ChooseKeeper(plan, set, set.Members[1], Exists);
        store.Update(handle, PlanSnapshot.Of(plan)).Should().BeGreaterThan(0);

        var loaded = store.Load(tmp.Path)!.Plan;
        loaded.Moves.Select(m => (m.Source, m.Destination, m.Included)).Should().BeEquivalentTo(plan.Moves.Select(m => (m.Source, m.Destination, m.Included)));
        loaded.DuplicateSets.Single().Keeper.FullPath.Should().Be(set.Members[1].FullPath);
        store.Find(tmp.Path)!.Checked.Should().Be(plan.Moves.Count(m => m.Included));
    }

    [Fact]
    public void Plans_untouched_for_seven_days_are_removed()
    {
        using var tmp = Messy();
        var now = DateTime.UtcNow;
        var store = new OrganizePlanStore(tmp["plans.db"]) { UtcNow = () => now };
        store.Save(PlanSnapshot.Of(PlanOf(tmp)), new SavedPlanSettings());

        var later = new OrganizePlanStore(tmp["plans.db"]) { UtcNow = () => now.AddDays(6) };
        later.Find(tmp.Path).Should().NotBeNull();

        var muchLater = new OrganizePlanStore(tmp["plans.db"]) { UtcNow = () => now.AddDays(8) };
        muchLater.Find(tmp.Path).Should().BeNull();
        muchLater.Load(tmp.Path).Should().BeNull();
    }

    [Fact]
    public void Saving_again_replaces_and_delete_forgets()
    {
        using var tmp = Messy();
        var store = new OrganizePlanStore(tmp["plans.db"]);
        var plan = PlanOf(tmp);
        var first = store.Save(PlanSnapshot.Of(plan), new SavedPlanSettings());
        plan.Moves[0].Included = false;
        store.Save(PlanSnapshot.Of(plan), new SavedPlanSettings());
        store.Find(tmp.Path)!.Checked.Should().Be(plan.Moves.Count - 1);

        plan.Moves[1].Included = false;
        store.Update(first, PlanSnapshot.Of(plan)).Should().Be(0, "바꿔 끼운 예전 계획에는 쓰지 않는다");

        store.Delete(tmp.Path);
        store.Find(tmp.Path).Should().BeNull();
    }

    [Fact]
    public void Execute_skips_files_changed_or_gone_since_the_plan()
    {
        using var tmp = new TempDir();
        tmp.File("same.pdf", "A", Old);
        tmp.File("bigger.pdf", "B", Old);
        tmp.File("touched.pdf", "C", Old);
        tmp.File("gone.pdf", "D", Old);
        var plan = OrganizePlanner.Plan(OrganizeScanner.Scan(tmp.Path, false), new OrganizeOptions(), Exists);

        File.WriteAllText(tmp["bigger.pdf"], "B and more");
        File.SetLastWriteTimeUtc(tmp["touched.pdf"], New); // 크기는 같고 수정한 날만 바뀜
        File.Delete(tmp["gone.pdf"]);

        var r = OrganizeExecutor.Execute(plan, tmp["o.journal"], LocalMoveOperations.Instance);
        r.Moved.Should().Be(1);
        r.MovedSources.Should().Equal(tmp["same.pdf"]);
        r.Changed.Should().BeEquivalentTo([tmp["bigger.pdf"], tmp["touched.pdf"]]);
        r.Missing.Should().Equal(tmp["gone.pdf"]);
        r.Failed.Should().BeEmpty();
        File.Exists(tmp[@"문서\same.pdf"]).Should().BeTrue();
        File.ReadAllText(tmp["bigger.pdf"]).Should().Be("B and more", "바뀐 파일은 그 자리에 그대로");
        File.Exists(tmp["touched.pdf"]).Should().BeTrue();

        var u = OrganizeExecutor.Undo(tmp["o.journal"], LocalMoveOperations.Instance);
        u.Restored.Should().Be(1);
    }

    [Fact]
    public async Task Reopening_offers_to_continue_with_the_same_checks()
    {
        using var tmp = Messy();
        var store = new OrganizePlanStore(tmp["plans.db"]);
        var vm = NewVm(tmp, store);
        (await vm.CheckSavedPlanAsync()).Should().BeFalse("처음에는 저장한 계획이 없다");
        await vm.PlanCommand.ExecuteAsync(null);
        vm.State.Should().Be(OrganizeState.Ready);
        vm.Moves.Single(m => m.SourceName == "노래.mp3").IsIncluded = false;
        await vm.FlushSavesAsync();

        // 창을 닫고 다시 연다 (앱을 껐다 켜도 같다)
        var again = NewVm(tmp, store);
        (await again.CheckSavedPlanAsync()).Should().BeTrue();
        again.HasSavedPlan.Should().BeTrue();
        again.SavedPlanText.Should().Contain(vm.Moves.Count.ToString(System.Globalization.CultureInfo.CurrentCulture));

        await again.ResumeSavedPlanCommand.ExecuteAsync(null);
        again.State.Should().Be(OrganizeState.Ready);
        again.HasSavedPlan.Should().BeFalse();
        again.Moves.Should().HaveCount(vm.Moves.Count);
        again.Moves.Single(m => m.SourceName == "노래.mp3").IsIncluded.Should().BeFalse("체크한 것까지 그대로");
        again.Moves.Where(m => m.SourceName != "노래.mp3").Should().OnlyContain(m => m.IsIncluded);
        again.IncludeSubfolders.Should().Be(vm.IncludeSubfolders);

        // 이어서 한 계획에서 바꾼 것도 저장된다
        again.Moves.Single(m => m.SourceName == "사진.jpg").IsIncluded = false;
        await again.FlushSavesAsync();
        store.Find(tmp.Path)!.Checked.Should().Be(vm.Moves.Count - 2);

        // 정리를 마치면 저장한 계획은 지운다
        await again.ExecuteCommand.ExecuteAsync(null);
        again.State.Should().Be(OrganizeState.Done);
        await again.FlushSavesAsync();
        store.Find(tmp.Path).Should().BeNull();
        File.Exists(tmp["노래.mp3"]).Should().BeTrue();
        File.Exists(tmp["사진.jpg"]).Should().BeTrue();
    }

    [Fact]
    public async Task Start_over_forgets_the_saved_plan_and_makes_a_new_one()
    {
        using var tmp = Messy();
        var store = new OrganizePlanStore(tmp["plans.db"]);
        var vm = NewVm(tmp, store);
        await vm.PlanCommand.ExecuteAsync(null);
        vm.Moves.Single(m => m.SourceName == "노래.mp3").IsIncluded = false;
        await vm.FlushSavesAsync();

        tmp.File("새파일.txt", "n", New);
        var again = NewVm(tmp, store);
        (await again.CheckSavedPlanAsync()).Should().BeTrue();
        await again.StartFreshCommand.ExecuteAsync(null);
        again.State.Should().Be(OrganizeState.Ready);
        again.Moves.Should().Contain(m => m.SourceName == "새파일.txt");
        again.Moves.Should().OnlyContain(m => m.IsIncluded, "새 계획은 모두 체크된 채로 시작한다");
        await again.FlushSavesAsync();
        store.Find(tmp.Path)!.Checked.Should().Be(again.Moves.Count);
    }

    [Fact]
    public async Task Small_plans_are_not_kept()
    {
        using var tmp = Messy();
        var store = new OrganizePlanStore(tmp["plans.db"]);
        var vm = new OrganizeViewModel(tmp.Path, new InlineDispatcher(), journalFolder: tmp["jobs"]) { PlanStore = store };
        await vm.PlanCommand.ExecuteAsync(null);
        await vm.FlushSavesAsync();
        store.Find(tmp.Path).Should().BeNull("몇 개뿐인 폴더는 다시 만들면 금방이라 저장하지 않는다");
    }
}
