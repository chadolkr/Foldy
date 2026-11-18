using System.Diagnostics;
using System.Globalization;
using System.IO;
using Foldy.Platform.Imaging;
using Foldy.TestSupport;

namespace Foldy.ImageBench;

/// <summary>
/// 사용법:
///   Foldy.ImageBench gen-images &lt;dir&gt; &lt;count&gt; [width] [height]   카메라 사진 같은 JPEG(EXIF·내장 썸네일) 70% + PNG 30%
///   Foldy.ImageBench bench-thumbs &lt;dir&gt; [size=160] [firstScreen=40] [--no-shell]   첫 화면·전체 썸네일 시간
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "gen-images")
        {
            var w = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1920;
            var h = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 1280;
            Generate(args[1], int.Parse(args[2], CultureInfo.InvariantCulture), w, h);
            return 0;
        }

        if (args.Length >= 2 && args[0] == "bench-thumbs")
        {
            var size = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 160;
            var first = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 40;
            return Bench(args[1], size, first, !args.Contains("--no-shell")).GetAwaiter().GetResult();
        }

        if (args.Length >= 3 && args[0] == "bench-one")
        {
            // 파일 하나를 단일 스레드로 여러 번 디코딩: 단계별 비용 확인용
            using var one = new ThumbnailService(1) { UseShellCache = false };
            var size = int.Parse(args[2], CultureInfo.InvariantCulture);
            _ = one.GetThumbnailAsync(args[1], size).GetAwaiter().GetResult(); // 데우기
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
            {
                _ = one.GetThumbnailAsync(args[1], size).GetAwaiter().GetResult();
            }

            var wpf = Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(args[1]);
                bi.DecodePixelWidth = size;
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.EndInit();
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"service {sw.Elapsed.TotalMilliseconds / 10:F1}ms, wpf {wpf.Elapsed.TotalMilliseconds / 10:F1}ms"));
            return 0;
        }

        Console.Error.WriteLine("사용법: gen-images <dir> <count> [width] [height] | bench-thumbs <dir> [size] [firstScreen]");
        return 2;
    }

    private static void Generate(string dir, int count, int width, int height)
    {
        Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();
        // 무늬 생성이 가장 느리므로 8가지만 만들어 돌려 쓴다 (파일 내용은 EXIF 촬영일로 달라진다)
        var patterns = Enumerable.Range(0, 8).Select(s => TestImages.CreatePattern(width, height, s)).ToArray();
        var jpegs = patterns.Select(p => TestImages.EncodeJpeg(p, 85)).ToArray();
        var pngs = patterns.Select(p => TestImages.EncodePng(TestImages.CreatePattern(width / 2, height / 2, p.GetHashCode()))).ToArray();
        var thumbs = Enumerable.Range(0, 8).Select(s => TestImages.EncodeJpeg(TestImages.CreatePattern(160, 160 * height / width, s), 75)).ToArray();
        var cameras = new[] { ("Canon", "Canon EOS R5"), ("NIKON CORPORATION", "NIKON Z 6"), ("SONY", "ILCE-7M4"), ("FUJIFILM", "X-T5") };
        var start = new DateTime(2024, 1, 1, 9, 0, 0);

        for (var i = 0; i < count; i++)
        {
            var k = i % 8;
            if (i % 10 < 7)
            {
                var (make, model) = cameras[i % cameras.Length];
                var app1 = TestImages.BuildExifApp1(new ExifSpec
                {
                    Make = make,
                    Model = model,
                    DateTaken = start.AddMinutes(i * 7),
                    Orientation = i % 17 == 0 ? 6 : 1,
                    PixelWidth = width,
                    PixelHeight = height,
                    FNumber = (28, 10),
                    ExposureTime = (1, 125),
                    Iso = 200,
                    FocalLength = (35, 1),
                    ThumbnailJpeg = thumbs[k],
                });
                File.WriteAllBytes(Path.Combine(dir, $"IMG_{i:0000}.jpg"), TestImages.InsertApp1(jpegs[k], app1));
            }
            else
            {
                File.WriteAllBytes(Path.Combine(dir, $"shot_{i:0000}.png"), pngs[k]);
            }
        }

        Console.WriteLine($"{count}개 생성: {dir} ({sw.Elapsed.TotalSeconds:N1}s, {width}×{height})");
    }

    private static async Task<int> Bench(string dir, int size, int firstScreen, bool useShell)
    {
        var files = Directory.EnumerateFiles(dir).Where(ImageFormats.IsImage).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("이미지가 없습니다: " + dir);
            return 1;
        }

        using var service = new ThumbnailService { UseShellCache = useShell };
        var sw = Stopwatch.StartNew();
        // 격자와 같은 순서: 먼 것부터 넣어 LIFO 대기열에서 첫 칸이 먼저 나오게
        var screen = files.Take(firstScreen).ToList();
        var tasks = new Task<PixelImage?>[screen.Count];
        for (var i = screen.Count - 1; i >= 0; i--)
        {
            tasks[i] = service.GetThumbnailAsync(screen[i], size);
        }

        var firstTile = await tasks[0];
        var firstTileMs = sw.Elapsed.TotalMilliseconds;
        await Task.WhenAll(tasks);
        var screenMs = sw.Elapsed.TotalMilliseconds;

        var rest = files.Skip(firstScreen).Select(f => service.GetThumbnailAsync(f, size)).ToList();
        var all = await Task.WhenAll(rest);
        var totalMs = sw.Elapsed.TotalMilliseconds;
        var failed = tasks.Count(t => t.Result is null) + all.Count(r => r is null);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{{\"files\":{files.Count},\"size\":{size},\"workers\":{service.WorkerCount},\"firstTileMs\":{firstTileMs:F1},\"firstScreen\":{screen.Count},\"firstScreenMs\":{screenMs:F1},\"totalMs\":{totalMs:F1},\"perImageMs\":{totalMs / files.Count:F2},\"shell\":{service.ShellCacheHits},\"shellLookupMs\":{service.ShellLookupMs:F0},\"exif\":{service.ExifHits},\"wic\":{service.WicDecodes},\"failed\":{failed}}}"));
        _ = firstTile;
        return failed == 0 ? 0 : 1;
    }
}
