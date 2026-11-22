using Foldy.Core.Compare;
using Foldy.Core.Logs;
using Foldy.Core.Space;

namespace Foldy.Core.Tests;

/// <summary>시작 전 공간 확인 (사용자 원칙 2026-11-22 "제한은 미리 알려 시간을 아낀다").</summary>
[Collection("DiskSpace")]
public sealed class DiskSpaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-space-" + Guid.NewGuid().ToString("N"));

    public DiskSpaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        DiskSpace.ProbeOverride = null;
        Directory.Delete(_dir, true);
    }

    private static void Drive(long free, string format) => DiskSpace.ProbeOverride = _ => new DiskSpace.DriveRoom(free, format, @"D:\");

    [Fact]
    public void Not_enough_free_space_is_found_before_starting()
    {
        Drive(1L << 30, "NTFS");
        var r = DiskSpace.Check(@"D:\x", 2L << 30);
        r.Problem.Should().Be(SpaceProblem.NotEnoughSpace);
        (r.Need, r.Free, r.Root, r.DriveName).Should().Be((2L << 30, 1L << 30, @"D:\", "D:"));
        DiskSpace.Check(@"D:\x", 1L << 30).Ok.Should().BeTrue("딱 맞으면 된다");
    }

    [Fact]
    public void Fat32_cannot_hold_a_file_over_4GB_but_exFAT_can()
    {
        Drive(1L << 40, "FAT32");
        var r = DiskSpace.Check(@"D:\", 5L << 30, 5L << 30);
        (r.Problem, r.Need).Should().Be((SpaceProblem.Fat32TooBig, 5L << 30));
        DiskSpace.Check(@"D:\", 5L << 30, DiskSpace.Fat32MaxFile).Ok.Should().BeTrue("4GB-1바이트까지는 된다");

        Drive(1L << 40, "exFAT");
        DiskSpace.Check(@"D:\", 5L << 30, 5L << 30).Ok.Should().BeTrue();
    }

    [Fact]
    public void Unknown_drive_never_blocks()
    {
        DiskSpace.ProbeOverride = _ => null;
        var r = DiskSpace.Check(@"\\server\share", long.MaxValue / 2, long.MaxValue / 2);
        (r.Ok, r.Free, r.Root).Should().Be((true, -1L, (string?)null));
    }

    [Fact]
    public void Small_files_are_counted_by_cluster_and_vault_estimate_covers_overhead()
    {
        DiskSpace.OnDisk(0).Should().Be(0);
        DiskSpace.OnDisk(1).Should().Be(4096);
        DiskSpace.OnDisk(4097).Should().Be(8192);

        Drive(10_000 * 4096L, "NTFS");
        DiskSpace.CheckFiles(@"D:\", Enumerable.Repeat(1L, 10_000)).Problem.Should().Be(SpaceProblem.NotEnoughSpace, "작은 파일 1만 개는 1만 바이트가 아니라 클러스터 1만 개(+여유)");

        DiskSpace.VaultEstimate(1L << 30, 100).Should().BeGreaterThan((1L << 30) + 100 * 1024);
    }

    [Fact]
    public void Folder_compare_checks_each_destination_drive()
    {
        var a = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(a, new byte[50_000]);
        var steps = new[] { new CopyStep(a, @"D:\dst\a.bin", "a.bin") };

        Drive(10_000, "NTFS");
        FolderCompare.CheckSpace(steps).Problem.Should().Be(SpaceProblem.NotEnoughSpace);
        Drive(100L << 20, "NTFS");
        FolderCompare.CheckSpace(steps).Ok.Should().BeTrue();
        FolderCompare.CheckSpace([]).Ok.Should().BeTrue();
    }

    [Fact]
    public void Log_zip_needs_the_compressed_size_plus_the_biggest_rewrite()
    {
        var log = new LogFile(Path.Combine(_dir, "a.log"), 1_000_000, DateTime.UtcNow.AddDays(-30));
        var g = new LogGroup(_dir, "2026-10", [log]);
        var need = LogPlanner.SpaceNeeded([g]);
        need.Should().Be((long)(1_000_000 * (1 - LogPlanner.SavingRatio)) + DiskSpace.Slack);

        File.WriteAllBytes(g.ZipPath, new byte[30_000]);
        LogPlanner.SpaceNeeded([g]).Should().Be(need + 30_000, "이미 있는 월별 zip은 새로 다시 쓴다");
    }
}
