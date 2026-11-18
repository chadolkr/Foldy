using Foldy.Analysis;

namespace Foldy.Analysis.Tests;

/// <summary>시험용 임시 폴더 (끝나면 지운다).</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foldy-an-" + Guid.NewGuid().ToString("N"))).FullName;

    public string File(string relative, int size, DateTime? mtimeUtc = null)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, new byte[size]);
        if (mtimeUtc is { } t)
        {
            System.IO.File.SetLastWriteTimeUtc(full, t);
        }

        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// 합성 트리 (Rust analysis/tests.rs 의 sample_index와 같은 모양):
/// D:\ ─ proj ─ package.json, node_modules ─ lib ─ big.js(30MB, 60일 전), src ─ app.ts
///     ─ Windows ─ Temp ─ junk.tmp(500MB, 1년 전)   (시스템 폴더: 제안 금지)
///     ─ Users ─ me ─ Downloads ─ setup.exe(80MB, 200일 전), photo.JPG(3MB)
/// </summary>
internal static class TestData
{
    public static readonly DateTime Now = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);

    public static long Ft(DateTime t) => t.ToFileTimeUtc();

    private static long Alloc(long size) => (size + 4095) / 4096 * 4096;

    public static SnapshotInput Sample(DateTime? now = null, long extraDownloads = 0)
    {
        var n = now ?? Now;
        var input = new SnapshotInput { RootPath = @"D:\", Volume = "D:", StartedAt = Ft(n) };
        var d = input.Dirs;
        var f = input.Files;
        int Dir(long id, int parent, string name)
        {
            d.Add(new InputDir(id, parent, name, Ft(n), (int)FileAttributes.Directory));
            return d.Count - 1;
        }

        void Add(long id, int dir, string name, long size, DateTime mtime) => f.Add(new InputFile(id, dir, name, size, Alloc(size), Ft(mtime), 0));

        var root = Dir(5, -1, @"D:\");
        var proj = Dir(20, root, "proj");
        Add(21, proj, "package.json", 10, n.AddDays(-60));
        var nm = Dir(22, proj, "node_modules");
        var lib = Dir(23, nm, "lib");
        Add(24, lib, "big.js", 30 << 20, n.AddDays(-60));
        var src = Dir(25, proj, "src");
        Add(26, src, "app.ts", 2000, n);
        var win = Dir(30, root, "Windows");
        var temp = Dir(31, win, "Temp");
        Add(32, temp, "junk.tmp", 500L << 20, n.AddDays(-365));
        var users = Dir(40, root, "Users");
        var me = Dir(41, users, "me");
        var dl = Dir(42, me, "Downloads");
        Add(43, dl, "setup.exe", 80 << 20, n.AddDays(-200));
        Add(44, dl, "photo.JPG", 3 << 20, n.AddDays(-5));
        if (extraDownloads > 0)
        {
            var newDir = Dir(50, dl, "new-stuff");
            Add(51, newDir, "movie.mp4", extraDownloads, n);
        }

        return input;
    }

    /// <summary>합성 트리로 DB를 만들어 경로를 돌려준다.</summary>
    public static string Build(string dir, SnapshotInput input, string key = "test") =>
        SnapshotBuilder.BuildIntoDirectory(input, dir, key, CleanupRules.Default, null, CancellationToken.None).Path;
}
