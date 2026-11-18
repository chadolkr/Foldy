using Foldy.Platform.Imaging;
using Foldy.TestSupport;

namespace Foldy.Imaging.Tests;

public class ExifTests
{
    private static readonly DateTime Taken = new(2024, 5, 1, 14, 3, 27);

    private static (byte[] Jpeg, byte[] Thumb) MakeCameraJpeg(int orientation = 1) => Sta.Run(() =>
    {
        var thumb = TestImages.EncodeJpeg(TestImages.CreatePattern(160, 120, 7), 70);
        var spec = new ExifSpec
        {
            Make = "Canon",
            Model = "Canon EOS R5",
            DateTaken = Taken,
            Orientation = orientation,
            PixelWidth = 640,
            PixelHeight = 480,
            FNumber = (28, 10),
            ExposureTime = (1, 250),
            Iso = 400,
            FocalLength = (50, 1),
            ThumbnailJpeg = thumb,
        };
        return (TestImages.CreateJpegWithExif(640, 480, spec, seed: 3), thumb);
    });

    [Fact]
    public void Reads_camera_fields_and_embedded_thumbnail_from_generated_jpeg()
    {
        using var dir = new TempDir();
        var (jpeg, thumb) = MakeCameraJpeg(orientation: 6);
        var path = dir.File("photo.jpg");
        File.WriteAllBytes(path, jpeg);

        var exif = ExifReader.TryReadFile(path);

        exif.Should().NotBeNull();
        exif!.Make.Should().Be("Canon");
        exif.Model.Should().Be("Canon EOS R5");
        exif.Camera.Should().Be("Canon EOS R5"); // 모델에 제조사가 있으면 모델만
        exif.DateTaken.Should().Be(Taken);
        exif.Orientation.Should().Be(6);
        exif.SwapsAxes.Should().BeTrue();
        exif.Width.Should().Be(640);  // SOF 헤더
        exif.Height.Should().Be(480);
        exif.FNumber.Should().BeApproximately(2.8, 1e-9);
        exif.ExposureTime.Should().BeApproximately(1 / 250.0, 1e-12);
        exif.Iso.Should().Be(400);
        exif.FocalLength.Should().Be(50);
        exif.Thumbnail.ToArray().Should().Equal(thumb);
    }

    [Fact]
    public void Jpeg_without_exif_still_reports_size_from_sof()
    {
        var jpeg = Sta.Run(() => TestImages.EncodeJpeg(TestImages.CreatePattern(123, 45)));
        using var ms = new MemoryStream(jpeg);

        var exif = ExifReader.Read(ms);

        exif.Should().NotBeNull();
        exif!.Width.Should().Be(123);
        exif.Height.Should().Be(45);
        exif.DateTaken.Should().BeNull();
        exif.Thumbnail.IsEmpty.Should().BeTrue();
        exif.Orientation.Should().Be(1);
    }

    [Fact]
    public void Non_jpeg_and_garbage_return_null_without_throwing()
    {
        var png = Sta.Run(() => TestImages.EncodePng(TestImages.CreatePattern(8, 8)));
        ExifReader.Read(new MemoryStream(png)).Should().BeNull();
        ExifReader.Read(new MemoryStream([0xFF, 0xD8, 0xFF, 0xE1, 0x00])).Should().NotBeNull(); // 잘린 파일: 값 없이
        ExifReader.Read(new MemoryStream([])).Should().BeNull();
    }

    [Fact]
    public void Truncated_tiff_block_is_ignored()
    {
        var app1 = Sta.Run(() => TestImages.BuildExifApp1(new ExifSpec { Make = "Nikon", DateTaken = Taken }));
        var tiff = app1.AsMemory(10, 20); // 머리와 IFD 일부만
        var info = ExifReader.ParseTiff(tiff);
        info.DateTaken.Should().BeNull();
    }

    [Fact]
    public void Camera_name_joins_make_and_model_when_needed()
    {
        new ExifInfo { Make = "SONY", Model = "ILCE-7M4" }.Camera.Should().Be("SONY ILCE-7M4");
        new ExifInfo { Make = "NIKON CORPORATION", Model = "NIKON Z 6" }.Camera.Should().Be("NIKON Z 6");
        new ExifInfo { Make = "Apple" }.Camera.Should().Be("Apple");
        new ExifInfo().Camera.Should().BeNull();
    }

    [Fact]
    public void Parses_exif_date_format_only()
    {
        ExifReader.ParseDate("2023:12:31 23:59:58").Should().Be(new DateTime(2023, 12, 31, 23, 59, 58));
        ExifReader.ParseDate("0000:00:00 00:00:00").Should().BeNull();
        ExifReader.ParseDate("2023-12-31").Should().BeNull();
    }

    [Fact]
    public void Orientation_maps_to_wic_transform()
    {
        ThumbnailService.OrientationToTransform(1).Should().Be(0);
        ThumbnailService.OrientationToTransform(6).Should().Be(1);  // 90° 시계 방향
        ThumbnailService.OrientationToTransform(3).Should().Be(2);
        ThumbnailService.OrientationToTransform(8).Should().Be(3);
        ThumbnailService.OrientationToTransform(2).Should().Be(8);
        ThumbnailService.OrientationToTransform(5).Should().Be(9);
    }
}
