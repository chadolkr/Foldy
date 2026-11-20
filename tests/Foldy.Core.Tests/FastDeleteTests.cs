using Foldy.Core.FileSystem;

namespace Foldy.Core.Tests;

public sealed class FastDeleteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "foldy-fastdel-" + Guid.NewGuid().ToString("N"));

    public FastDeleteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var f in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, File.GetAttributes(f) & ~FileAttributes.ReadOnly);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Tree(string name, int dirs, int filesPerDir)
    {
        var top = Path.Combine(_root, name);
        for (var d = 0; d < dirs; d++)
        {
            var dir = Path.Combine(top, $"d{d % 5}", $"sub{d}");
            Directory.CreateDirectory(dir);
            for (var f = 0; f < filesPerDir; f++)
            {
                File.WriteAllText(Path.Combine(dir, $"f{f}.txt"), "x");
            }
        }

        return top;
    }

    [Fact]
    public void Deletes_whole_tree_and_counts_files()
    {
        var top = Tree("big", 20, 30);
        File.SetAttributes(Path.Combine(top, "d0", "sub0", "f0.txt"), FileAttributes.ReadOnly | FileAttributes.Hidden);
        var c = FastDelete.Count([top], TimeSpan.FromSeconds(10));
        Assert.Equal(600, c.Files);
        Assert.False(c.Capped);

        long last = -1;
        var r = FastDelete.Delete([top], (n, _) => last = Math.Max(last, n), parallelism: 4);
        Assert.False(r.Canceled);
        Assert.Equal(600, r.DeletedFiles);
        Assert.Equal(0, r.FailedCount);
        Assert.Equal(600, last);
        Assert.False(Directory.Exists(top));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void Single_files_and_missing_paths()
    {
        var a = Path.Combine(_root, "a.txt");
        File.WriteAllText(a, "a");
        var r = FastDelete.Delete([a, Path.Combine(_root, "nope")]);
        Assert.Equal(1, r.DeletedFiles);
        Assert.False(File.Exists(a));
    }

    [Fact]
    public void Junction_is_removed_but_its_target_is_kept()
    {
        var target = Tree("keep", 2, 3);
        var top = Path.Combine(_root, "withlink");
        Directory.CreateDirectory(top);
        var link = Path.Combine(top, "link");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 심볼릭 링크 권한이 없으면 정션으로
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false });
            p!.WaitForExit();
        }

        Assert.True(Directory.Exists(link));
        Assert.Equal(0, FastDelete.Count([top], TimeSpan.FromSeconds(5)).Files); // 링크 안은 세지 않는다
        var r = FastDelete.Delete([top]);
        Assert.False(r.Canceled);
        Assert.False(Directory.Exists(top));
        Assert.Equal(6, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Long_paths_are_deleted()
    {
        var dir = _root;
        while (dir.Length < 320)
        {
            dir = Path.Combine(dir, new string('긴', 40));
        }

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "deep.txt"), "x");
        var top = Path.Combine(_root, new string('긴', 40));
        var r = FastDelete.Delete([top]);
        Assert.Equal(1, r.DeletedFiles);
        Assert.False(Directory.Exists(top));
    }

    [Fact]
    public void Cancel_stops_and_reports()
    {
        var top = Tree("cancel", 10, 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = FastDelete.Delete([top], ct: cts.Token);
        Assert.True(r.Canceled);
        Assert.True(Directory.Exists(top));
    }

    [Fact]
    public void Count_stops_at_budget()
    {
        var top = Tree("budget", 40, 2);
        var c = FastDelete.Count([top], TimeSpan.Zero);
        Assert.True(c.Capped);
        Assert.True(c.Files < 80);
    }

    [Fact]
    public void Protected_places_are_refused()
    {
        Assert.True(FastDelete.IsProtected(@"C:\"));
        Assert.True(FastDelete.IsProtected(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.True(FastDelete.IsProtected(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.True(FastDelete.IsProtected(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\"));
        Assert.False(FastDelete.IsProtected(_root));
        Assert.Throws<InvalidOperationException>(() => FastDelete.Delete([_root, @"C:\"]));
        Assert.True(Directory.Exists(_root)); // 하나라도 보호된 곳이면 아무것도 지우지 않는다
    }
}
