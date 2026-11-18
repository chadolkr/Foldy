using System.Windows.Media;
using System.Windows.Media.Imaging;
using Foldy.Platform.Imaging;
using Foldy.TestSupport;
using Foldy.UI.Imaging;

namespace Foldy.Imaging.Tests;

public class CacheTests
{
    private static BitmapSource Bitmap(int w, int h, byte alpha = 255)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = 10;
            px[i + 1] = 20;
            px[i + 2] = 30;
            px[i + 3] = alpha;
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static ThumbKey Key(string name, int size = 96) => new($@"C:\pics\{name}", 638_000_000_000_000_000, 1234, size);

    [Fact]
    public void Memory_cache_evicts_least_recently_used_over_budget()
    {
        Sta.Run(() =>
        {
            var one = 10 * 10 * 4;
            var cache = new ThumbnailMemoryCache(budgetBytes: 3 * one);
            cache.Add(Key("a"), Bitmap(10, 10));
            cache.Add(Key("b"), Bitmap(10, 10));
            cache.Add(Key("c"), Bitmap(10, 10));
            cache.TryGet(Key("a"), out _).Should().BeTrue(); // a를 최근으로

            cache.Add(Key("d"), Bitmap(10, 10));

            cache.Count.Should().Be(3);
            cache.TotalBytes.Should().Be(3 * one);
            cache.Contains(Key("b")).Should().BeFalse(); // 가장 오래 안 쓴 것
            cache.Contains(Key("a")).Should().BeTrue();
            cache.Contains(Key("c")).Should().BeTrue();
            cache.Contains(Key("d")).Should().BeTrue();
        });
    }

    [Fact]
    public void Memory_cache_replaces_same_key_and_rejects_oversized()
    {
        Sta.Run(() =>
        {
            var cache = new ThumbnailMemoryCache(budgetBytes: 1000);
            cache.Add(Key("a"), Bitmap(5, 5));
            cache.Add(Key("a"), Bitmap(6, 6));
            cache.Count.Should().Be(1);
            cache.TotalBytes.Should().Be(6 * 6 * 4);

            cache.Add(Key("huge"), Bitmap(20, 20)); // 1600 > 1000
            cache.Contains(Key("huge")).Should().BeFalse();
            cache.Count.Should().Be(1);
        });
    }

    [Fact]
    public void Key_ignores_path_case_but_not_mtime_or_size()
    {
        var k = Key("A.jpg");
        k.Should().Be(k with { Path = k.Path.ToUpperInvariant() });
        k.GetHashCode().Should().Be((k with { Path = k.Path.ToLowerInvariant() }).GetHashCode());
        k.Should().NotBe(k with { ModifiedTicks = k.ModifiedTicks + 1 });
        k.Should().NotBe(k with { Length = 1 });
        k.Should().NotBe(k with { PixelSize = 160 });
    }

    [Fact]
    public void Invalidate_and_any_size_lookup()
    {
        Sta.Run(() =>
        {
            var cache = new ThumbnailMemoryCache();
            cache.Add(Key("x", 96), Bitmap(4, 4));
            cache.Add(Key("x", 256), Bitmap(8, 8));
            cache.FindAnySize(Key("x", 160), [256, 160, 96])!.PixelWidth.Should().Be(8);

            cache.Invalidate(@"c:\PICS\x");
            cache.Count.Should().Be(0);
            cache.TotalBytes.Should().Be(0);
        });
    }

    [Fact]
    public void Disk_cache_round_trips_and_survives_reopen()
    {
        using var dir = new TempDir();
        var path = dir.File("thumbs.cache");
        Sta.Run(() =>
        {
            using (var disk = DiskThumbnailCache.TryOpen(path)!)
            {
                disk.Put(Key("opaque"), Bitmap(16, 12));
                disk.Put(Key("alpha"), Bitmap(8, 8, alpha: 128)); // PNG로 저장
                disk.Count.Should().Be(2);
            }

            using var reopened = DiskThumbnailCache.TryOpen(path)!;
            reopened.Count.Should().Be(2);
            var img = reopened.TryGet(Key("opaque"));
            img.Should().NotBeNull();
            (img!.PixelWidth, img.PixelHeight).Should().Be((16, 12));
            reopened.TryGet(Key("alpha"))!.PixelWidth.Should().Be(8);
            reopened.TryGet(Key("opaque") with { ModifiedTicks = 1 }).Should().BeNull(); // 파일이 바뀌면 무효
        });
    }

    [Fact]
    public void Disk_cache_truncates_broken_tail()
    {
        using var dir = new TempDir();
        var path = dir.File("thumbs.cache");
        Sta.Run(() =>
        {
            using (var disk = DiskThumbnailCache.TryOpen(path)!)
            {
                disk.Put(Key("a"), Bitmap(8, 8));
            }

            var good = new FileInfo(path).Length;
            File.AppendAllText(path, "garbage after crash");

            using var reopened = DiskThumbnailCache.TryOpen(path)!;
            reopened.Count.Should().Be(1);
            new FileInfo(path).Length.Should().Be(good);
        });
    }

    [Fact]
    public void Disk_cache_compacts_oldest_records_over_limit()
    {
        using var dir = new TempDir();
        var path = dir.File("thumbs.cache");
        Sta.Run(() =>
        {
            var src = TestImages.CreatePattern(64, 64, 5);
            using var disk = DiskThumbnailCache.TryOpen(path, maxBytes: 20_000)!;
            for (var i = 0; i < 40; i++)
            {
                disk.Put(Key($"f{i}"), src);
            }

            disk.FileBytes.Should().BeLessThanOrEqualTo(20_000);
            disk.Contains(Key("f39")).Should().BeTrue();  // 최근 것은 남고
            disk.Contains(Key("f0")).Should().BeFalse();  // 오래된 것은 지워진다
            disk.TryGet(Key("f39")).Should().NotBeNull();
        });
    }

    [Fact]
    public void Image_extensions_and_mostly_images_rule()
    {
        ImageFormats.IsImage("a.JPG").Should().BeTrue();
        ImageFormats.IsImage(".webp").Should().BeTrue();
        ImageFormats.IsImage("icon.ico").Should().BeTrue();
        ImageFormats.IsImage("notes.txt").Should().BeFalse();
        ImageFormats.IsImage("noext").Should().BeFalse();
        ImageFormats.IsKnownImageFormat("x.heic").Should().BeTrue();
        ImageFormats.IsImage("x.heic").Should().Be(ImageFormats.IsCodecAvailable(".heic"));
        ImageFormats.NeedsCodec("x.heic").Should().Be(!ImageFormats.IsCodecAvailable(".heic"));
        ImageFormats.MayBeAnimated("a.gif").Should().BeTrue();

        string[] seven = ["1.jpg", "2.png", "3.gif", "4.bmp", "5.tif", "6.webp", "7.ico", "a.txt", "b.doc", "c.zip"];
        ImageFormats.IsMostlyImages(seven).Should().BeTrue();          // 70%
        ImageFormats.IsMostlyImages(seven.Append("d.pdf")).Should().BeFalse(); // 63%
        ImageFormats.IsMostlyImages(["1.jpg", "2.jpg"]).Should().BeFalse();     // 파일이 너무 적다
        ImageFormats.IsMostlyImages(["1.jpg", "2.jpg"], minFiles: 1).Should().BeTrue();
        ImageFormats.CountImages(seven).Should().Be((7, 10));
    }

    [Fact]
    public void Info_line_formats_resolution_size_date_and_camera()
    {
        var item = new ImageItemInfo(@"C:\p\a.jpg", "a.jpg", default, 3_355_443, default);
        var exif = new ExifInfo { Make = "Canon", Model = "Canon EOS R5", DateTaken = new DateTime(2024, 5, 1, 14, 3, 0), FNumber = 2.8, ExposureTime = 0.004, Iso = 100, FocalLength = 50 };
        var text = ImageInfoText.Format(item, 4000, 3000, exif);
        text.Should().Contain("4000 × 3000").And.Contain("3.2 MB").And.Contain("2024-05-01").And.Contain("Canon EOS R5")
            .And.Contain("f/2.8").And.Contain("1/250s").And.Contain("ISO 100").And.Contain("50mm");

        ImageInfoText.Format(item, 0, 0, null).Should().Be("3.2 MB");
    }

    [Fact]
    public void Default_adapter_reads_fileitem_like_objects()
    {
        var entry = new FakeEntry(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 42, FileAttributes.Archive);
        var info = ImageItemAdapter.Convert(new FakeItem(@"C:\x\y.png", "y.png", entry));
        info.Should().Be(new ImageItemInfo(@"C:\x\y.png", "y.png", entry.Modified, 42, FileAttributes.Archive));
    }

    public sealed record FakeEntry(DateTime Modified, long Size, FileAttributes Attributes);

    public sealed record FakeItem(string FullPath, string Name, FakeEntry Entry);
}
