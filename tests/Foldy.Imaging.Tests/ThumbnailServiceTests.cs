using Foldy.Platform.Imaging;
using Foldy.TestSupport;

namespace Foldy.Imaging.Tests;

public sealed class ThumbnailServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ThumbnailService _service = new(workerCount: 2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _service.Dispose();
        _dir.Dispose();
    }

    private string WritePng(string name, int w, int h)
    {
        var path = _dir.File(name);
        File.WriteAllBytes(path, Sta.Run(() => TestImages.EncodePng(TestImages.CreatePattern(w, h))));
        return path;
    }

    private string WriteCameraJpeg(string name, int w, int h, int orientation, int thumbW, int thumbH)
    {
        var path = _dir.File(name);
        File.WriteAllBytes(path, Sta.Run(() =>
        {
            var thumb = TestImages.EncodeJpeg(TestImages.CreatePattern(thumbW, thumbH, 1), 70);
            return TestImages.CreateJpegWithExif(w, h, new ExifSpec { Orientation = orientation, ThumbnailJpeg = thumb });
        }));
        return path;
    }

    [Fact]
    public async Task Wic_decodes_to_requested_long_edge_keeping_aspect()
    {
        var path = WritePng("wide.png", 400, 100);

        var img = await _service.LoadAsync(path, 64, ImageLoadOptions.Default, Ct);

        img.Should().NotBeNull();
        img!.Source.Should().Be(ThumbnailSource.WicDecode); // 새 임시 파일은 셸 캐시에 없다
        (img.Width, img.Height).Should().Be((64, 16));
        img.SourceWidth.Should().Be(400);
        img.Pixels.Length.Should().Be(64 * 16 * 4);
        img.Pixels[3].Should().Be(255); // 불투명 PBGRA
    }

    [Fact]
    public async Task Small_images_are_not_upscaled()
    {
        var path = WritePng("tiny.png", 20, 10);
        var img = await _service.LoadAsync(path, 256, ImageLoadOptions.Default, Ct);
        (img!.Width, img.Height).Should().Be((20, 10));
    }

    [Fact]
    public async Task Exif_thumbnail_is_used_when_big_enough_and_rotated_by_orientation()
    {
        var path = WriteCameraJpeg("cam.jpg", 800, 400, orientation: 6, thumbW: 160, thumbH: 80);

        var img = await _service.LoadAsync(path, 96, ImageLoadOptions.Default, Ct);

        img!.Source.Should().Be(ThumbnailSource.ExifEmbedded);
        (img.Width, img.Height).Should().Be((48, 96)); // 96×48을 90° 돌림
    }

    [Fact]
    public async Task Small_exif_thumbnail_is_preliminary_then_replaced_by_wic()
    {
        var path = WriteCameraJpeg("cam2.jpg", 800, 400, orientation: 1, thumbW: 160, thumbH: 80);
        PixelImage? prelim = null;
        var opts = ImageLoadOptions.Default with { Preliminary = p => prelim = p };

        var img = await _service.LoadAsync(path, 512, opts, Ct);

        prelim.Should().NotBeNull();
        prelim!.Source.Should().Be(ThumbnailSource.ExifEmbedded);
        img!.Source.Should().Be(ThumbnailSource.WicDecode);
        (img.Width, img.Height).Should().Be((512, 256));
    }

    [Fact]
    public async Task Preview_with_zero_edge_returns_original_size()
    {
        var path = WritePng("orig.png", 300, 200);
        var img = await _service.LoadAsync(path, 0, ImageLoadOptions.Default with { Purpose = ImageLoadPurpose.Preview }, Ct);
        (img!.Width, img.Height).Should().Be((300, 200));
    }

    [Fact]
    public async Task Animated_gif_gives_first_frame()
    {
        var path = _dir.File("anim.gif");
        File.WriteAllBytes(path, Sta.Run(() =>
        {
            var enc = new System.Windows.Media.Imaging.GifBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(TestImages.CreatePattern(40, 20, 1)));
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(TestImages.CreatePattern(40, 20, 2)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }));

        var img = await _service.LoadAsync(path, 96, ImageLoadOptions.Default, Ct);
        (img!.Width, img.Height).Should().Be((40, 20));
    }

    [Fact]
    public async Task Broken_file_returns_null()
    {
        var path = _dir.File("broken.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0x00, 0x01, 0x02]);
        (await _service.LoadAsync(path, 96, ImageLoadOptions.Default, Ct)).Should().BeNull();
        (await _service.LoadAsync(_dir.File("missing.png"), 96, ImageLoadOptions.Default, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Cancelled_request_is_cancelled()
    {
        var path = WritePng("c.png", 50, 50);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var act = () => _service.LoadAsync(path, 96, ImageLoadOptions.Default, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Many_cancelled_requests_are_skipped_and_queue_keeps_working()
    {
        var paths = Enumerable.Range(0, 30).Select(i => WritePng($"q{i}.png", 64, 64)).ToList();
        using var cts = new CancellationTokenSource();
        var cancelled = paths.Select(p => _service.LoadAsync(p, 32, ImageLoadOptions.Default, cts.Token)).ToList();
        await cts.CancelAsync();
        var ok = await _service.LoadAsync(paths[0], 32, ImageLoadOptions.Default, Ct);

        ok.Should().NotBeNull();
        foreach (var t in cancelled)
        {
            try
            {
                await t;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancelled.Count(t => t.IsCanceled).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Cloud_only_file_content_is_never_read()
    {
        var path = WriteCameraJpeg("cloud.jpg", 400, 300, 1, 160, 120);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        if ((File.GetAttributes(path) & FileAttributes.Offline) == 0)
        {
            Assert.Skip("이 파일 시스템은 OFFLINE 속성을 지원하지 않습니다.");
        }

        var before = _service.ContentReadCount;
        var thumb = await _service.LoadAsync(path, 96, ImageLoadOptions.Default, Ct);
        var preview = await _service.LoadAsync(path, 0, ImageLoadOptions.Default with { Purpose = ImageLoadPurpose.Preview }, Ct);

        _service.ContentReadCount.Should().Be(before);
        thumb.Should().BeNull();   // 셸 캐시에 없으니 아무것도 만들지 않는다
        preview.Should().BeNull();
        ExifReader.TryReadFile(path).Should().BeNull();
    }

    [Fact]
    public async Task Content_read_disabled_uses_shell_cache_only()
    {
        var path = WritePng("net.png", 64, 64);
        var before = _service.ContentReadCount;
        var img = await _service.LoadAsync(path, 96, new ImageLoadOptions { AllowContentRead = false }, Ct);
        _service.ContentReadCount.Should().Be(before);
        img.Should().BeNull();
    }

    [Fact]
    public void Cloud_guard_recognises_placeholder_attributes()
    {
        CloudFileGuard.MustNotReadContent(FileAttributes.Normal).Should().BeFalse();
        CloudFileGuard.MustNotReadContent(FileAttributes.Archive | FileAttributes.ReadOnly).Should().BeFalse();
        CloudFileGuard.MustNotReadContent(FileAttributes.Offline).Should().BeTrue();
        CloudFileGuard.MustNotReadContent((FileAttributes)CloudFileGuard.RecallOnDataAccess).Should().BeTrue();
        CloudFileGuard.MustNotReadContent((FileAttributes)CloudFileGuard.RecallOnOpen | FileAttributes.Archive).Should().BeTrue();
        CloudFileGuard.MustNotReadContent(_dir.File("does-not-exist.jpg")).Should().BeTrue();
    }
}
