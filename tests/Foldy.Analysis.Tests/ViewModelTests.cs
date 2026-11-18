using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Foldy.Analysis;
using Foldy.Presentation.Analysis;
using Foldy.Presentation.Tabs;
using Foldy.UI.Analysis;

namespace Foldy.Analysis.Tests;

/// <summary>서비스 대역: 미리 만든 스냅샷을 돌려준다 (BuildAsync는 새로 만든 것처럼).</summary>
internal sealed class FakeService(Func<SnapshotSet?> latest, Func<SnapshotSet?>? build = null) : IAnalysisSnapshotService
{
    public int Builds { get; private set; }

    public Task<SnapshotSet?> GetLatestAsync(string volume, CancellationToken ct) => Task.FromResult(latest());

    public Task<SnapshotSet?> BuildAsync(string volume, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        Builds++;
        progress?.Report(new AnalysisProgress(0.5, "서비스 분석 중"));
        return Task.FromResult((build ?? latest)());
    }
}

public class ViewModelTests
{
    private static SnapshotSet ServiceSet(string dir)
    {
        var prev = TestData.Build(dir, TestData.Sample(TestData.Now.AddDays(-7)));
        var cur = TestData.Build(dir, TestData.Sample(TestData.Now, extraDownloads: 1L << 30));
        return new SnapshotSet(new SnapshotRef(cur, TestData.Now), [new SnapshotRef(prev, TestData.Now.AddDays(-7))], true);
    }

    [Fact]
    public async Task Opens_service_snapshot_fills_lists_and_navigates_treemap()
    {
        using var tmp = new TempDir();
        var set = ServiceSet(tmp.Path);
        var svc = new FakeService(() => set);
        var vm = new AnalysisTabViewModel(new AnalysisCoordinator(svc, CleanupRules.Default, tmp.Path), @"D:\");
        vm.Kind.Should().Be(TabKind.Analysis);
        vm.Title.Should().Be(@"분석: D:\");

        await vm.OpenLatestAsync();
        vm.ErrorText.Should().BeEmpty();
        vm.HasSnapshot.Should().BeTrue();
        svc.Builds.Should().Be(0, "스냅샷이 있으면 새로 분석하지 않는다");
        vm.ScanText.Should().Contain("색인 서비스");
        vm.TopFiles[0].Name.Should().Be("movie.mp4");
        vm.TopFolders.Should().NotBeEmpty();
        vm.Groups.Select(g => g.Label).Should().Contain("동영상");
        vm.Candidates.Select(c => c.Row.Rule).Should().Equal("old-installers", "node-modules");
        vm.Candidates[1].Title.Should().Be("Node.js 패키지 폴더");
        vm.ReclaimText.Should().Contain("제안 2개");
        vm.PreviousSnapshots.Should().ContainSingle();
        vm.SelectedPrevious.Should().Be(set.Previous[0]);
        vm.Growth.Should().NotBeEmpty();
        vm.Growth.Should().OnlyContain(g => g.Row.Delta == 1L << 30);
        vm.GrowthText.Should().Contain("+1 GB");

        // 트리맵: 루트의 자식 → 폴더로 들어가기 → 브레드크럼으로 올라가기
        vm.Breadcrumb.Select(b => b.Name).Should().Equal(@"D:\");
        vm.TreemapItems.Select(t => t.Name).Should().StartWith("Users");
        var users = vm.TreemapItems.First(t => t.Name == "Users");
        await vm.DrillDownCommand.ExecuteAsync(users);
        vm.Breadcrumb.Select(b => b.Name).Should().Equal(@"D:\", "Users");
        vm.CurrentFolderText.Should().StartWith(@"D:\Users");
        await vm.DrillDownCommand.ExecuteAsync(vm.TreemapItems[0]);
        vm.Breadcrumb.Should().HaveCount(3);
        await vm.UpCommand.ExecuteAsync(null);
        vm.Breadcrumb.Should().HaveCount(2);
        await vm.NavigateToCommand.ExecuteAsync(vm.Breadcrumb[0]);
        vm.CurrentFolderId.Should().Be(5);

        // 토글
        vm.OwnSizeOnly = true;
        await WaitAsync(() => vm.TopFolders.Count > 0 && vm.TopFolders[0].SortValue == vm.TopFolders[0].Row.OwnAlloc && vm.TopFolders[0].Row.OwnAlloc > 0);
        vm.SetOldFileYearsCommand.Execute("5");
        vm.OldFileYears.Should().Be(5);
        await WaitAsync(() => vm.OldFiles.Count == 0);
        vm.Close();
    }

    [Fact]
    public async Task Recycle_only_raises_event_and_never_deletes()
    {
        using var tree = new TempDir();
        using var outDir = new TempDir();
        var old = DateTime.UtcNow.AddDays(-90);
        tree.File(@"p\package.json", 10, old);
        var big = tree.File(@"p\node_modules\x\big.js", 11 << 20, old);
        var vm = new AnalysisTabViewModel(new AnalysisCoordinator(null, CleanupRules.Default, outDir.Path), tree.Path);
        var opened = new List<OpenPathEventArgs>();
        vm.OpenRequested += (_, e) => opened.Add(e);
        RecycleRequestEventArgs? asked = null;
        var asks = 0;
        vm.RecycleRequested += (_, e) =>
        {
            asked = e;
            asks++;
        };

        await vm.OpenLatestAsync(); // 스냅샷이 없으므로 폴백 스캐너로 바로 분석
        vm.ErrorText.Should().BeEmpty();
        vm.HasSnapshot.Should().BeTrue();
        vm.ScanText.Should().Contain("직접 열거");
        var item = vm.Candidates.Should().ContainSingle().Subject;
        item.Path.Should().Be(Path.Combine(tree.Path, "p", "node_modules"));

        vm.RecycleCommand.Execute(item);
        asked.Should().NotBeNull();
        asked!.Paths.Should().Equal(item.Path);
        asked.TotalBytes.Should().Be(11 << 20);
        item.IsPending.Should().BeTrue();
        File.Exists(big).Should().BeTrue("ViewModel은 절대 지우지 않는다");
        vm.RecycleCommand.Execute(item);
        asks.Should().Be(1, "대기 중이면 다시 요청하지 않는다");

        vm.MarkRecycled(item, done: false);
        item.IsPending.Should().BeFalse();
        vm.Candidates.Should().ContainSingle();
        vm.MarkRecycled(item);
        vm.Candidates.Should().BeEmpty();
        vm.ReclaimText.Should().Contain("찾지 못했습니다");

        vm.OpenCommand.Execute(vm.TopFiles[0]);
        opened.Should().ContainSingle().Which.Path.Should().Be(big);
        opened[0].IsFolder.Should().BeFalse();
        vm.Close();
    }

    [Fact]
    public async Task Service_failure_falls_back_and_missing_db_shows_error()
    {
        using var tmp = new TempDir();
        using var tree = new TempDir();
        tree.File("a.txt", 10);
        // 서비스가 "모르는 볼륨"이면(null) 폴백 스캐너
        var svc = new FakeService(() => null);
        var vm = new AnalysisTabViewModel(new AnalysisCoordinator(svc, CleanupRules.Default, tmp.Path), tree.Path);
        await vm.AnalyzeAsync();
        svc.Builds.Should().Be(1);
        vm.HasSnapshot.Should().BeTrue();
        vm.ScanText.Should().Contain("직접 열거");

        var broken = new FakeService(() => new SnapshotSet(new SnapshotRef(Path.Combine(tmp.Path, "none.db"), TestData.Now), [], true));
        var vm2 = new AnalysisTabViewModel(new AnalysisCoordinator(broken, CleanupRules.Default, tmp.Path), @"D:\");
        await vm2.OpenLatestAsync();
        vm2.HasError.Should().BeTrue();
        vm2.HasSnapshot.Should().BeFalse();
    }

    [Fact]
    public void Treemap_nodes_add_remainder_and_colors()
    {
        var nodes = AnalysisTabViewModel.BuildNodes(1000, [
            new TreemapChild(1, true, "a", 500, 500, null),
            new TreemapChild(2, false, "f", 300, 300, null),
            new TreemapChild(3, true, "b", 0, 0, null),
        ]);
        nodes.Select(n => n.Name).Should().Equal("a", "f", "기타 (작은 파일·시스템)");
        nodes[0].ColorIndex.Should().Be(0);
        nodes[1].ColorIndex.Should().Be(-1);
        nodes[2].IsRemainder.Should().BeTrue();
        nodes[2].Alloc.Should().Be(200);
        AnalysisTabViewModel.BuildNodes(100, [new TreemapChild(1, true, "a", 100, 100, null)]).Should().ContainSingle();
    }

    private static async Task WaitAsync(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++)
        {
            await Task.Delay(25);
        }

        cond().Should().BeTrue();
    }
}

/// <summary>AnalysisView XAML이 테마 토큰으로 로드·배치·렌더되는지 (STA). 결과 PNG는 테스트 출력 폴더에 남긴다.</summary>
public class AnalysisViewRenderTests
{
    private static void Sta(Func<Task> body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            var d = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            d.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception e)
                {
                    error = e;
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            Dispatcher.PushFrame(frame);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(60)))
        {
            throw new TimeoutException("STA 테스트 시간 초과");
        }

        if (error is not null)
        {
            ExceptionDispatchInfo.Throw(error);
        }
    }

    [Theory]
    [InlineData("Modern.Light.xaml")]
    [InlineData("Modern.Dark.xaml")]
    [InlineData("Retro.xaml")]
    public void View_loads_and_renders_with_theme_tokens(string palette)
    {
        using var tmp = new TempDir();
        var prev = TestData.Build(tmp.Path, TestData.Sample(TestData.Now.AddDays(-7)));
        var cur = TestData.Build(tmp.Path, TestData.Sample(TestData.Now, extraDownloads: 1L << 30));
        var set = new SnapshotSet(new SnapshotRef(cur, TestData.Now), [new SnapshotRef(prev, TestData.Now.AddDays(-7))], true);
        Sta(async () =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Clear();
            // ThemeManager와 같은 순서: 팔레트 + 컨트롤, 레트로는 그 위에 덮는다.
            var basePalette = palette == "Retro.xaml" ? "Modern.Light.xaml" : palette;
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Foldy.UI;component/Themes/{basePalette}") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Foldy.UI;component/Themes/Modern.Controls.xaml") });
            if (palette == "Retro.xaml")
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Foldy.UI;component/Themes/Retro.xaml") });
            }

            var vm = new AnalysisTabViewModel(new AnalysisCoordinator(new FakeService(() => set), CleanupRules.Default, tmp.Path), @"D:\");
            await vm.OpenLatestAsync();
            vm.HasSnapshot.Should().BeTrue(vm.ErrorText);
            var view = new AnalysisView { DataContext = vm, Width = 1200, Height = 720 };
            var win = new Window { Content = view, Width = 1200, Height = 720, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -4000 };
            win.Show();
            view.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Background);
            var bmp = new RenderTargetBitmap(1200, 720, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(view);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(Path.Combine(AppContext.BaseDirectory, "analysis-view-" + Path.GetFileNameWithoutExtension(palette) + ".png")))
            {
                enc.Save(fs);
            }

            // 트리맵 칸 색이 테마 토큰(Brush.Tab0)을 따른다
            var map = FindChild<TreemapControl>(view)!;
            map.Should().NotBeNull();
            ((SolidColorBrush)map.GetValue(TreemapControl.TileBrushProperty(0))).Color
                .Should().Be(((SolidColorBrush)app.FindResource("Brush.Tab0")).Color);
            map.ItemsSource.Should().NotBeEmpty();
            win.Close();
            vm.Close();
        });
    }

    private static T? FindChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t)
            {
                return t;
            }

            if (FindChild<T>(c) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
