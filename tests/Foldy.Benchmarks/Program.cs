using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Foldy.Core.FileSystem;
using Foldy.Core.Threading;
using Foldy.Providers.Local;

// 사용법:
//   Foldy.Benchmarks gen  <폴더> [개수=100000]   빈 파일 N개 생성 (벤치 데이터)
//   Foldy.Benchmarks enum <폴더> [반복=5]         LocalProvider 첫 배치·전체 열거 시간
if (args.Length >= 1 && args[0] == "search")
{
    // 색인 서비스 측정:  Foldy.Benchmarks search <질의1> [질의2 …]
    var client = await Foldy.Indexer.Client.IndexerClient.TryConnectAsync(timeout: TimeSpan.FromSeconds(2));
    if (client is null)
    {
        Console.WriteLine("색인 서비스에 연결하지 못함");
        return 2;
    }

    var st = await client.StatusAsync();
    Console.WriteLine($"상태 {st.State}, 항목 {st.IndexedEntries:N0}, 진행 {st.ProgressPermille / 10.0:0.0}%");
    foreach (var v in st.Volumes)
    {
        Console.WriteLine($"  {v}");
    }

    foreach (var q in args.Skip(1))
    {
        for (var r = 0; r < 3; r++)
        {
            var sw = Stopwatch.StartNew();
            double first = -1;
            var n = 0;
            await foreach (var b in client.SearchStreamAsync(q, 1000))
            {
                if (first < 0 && b.Hits.Count > 0)
                {
                    first = sw.Elapsed.TotalMilliseconds;
                }

                n += b.Hits.Count;
            }

            Console.WriteLine($"검색 \"{q}\" #{r + 1}: {n:N0}개 (최대 1000) · 첫 결과 {first:F1}ms · 전체 {sw.Elapsed.TotalMilliseconds:F1}ms");
        }
    }

    await client.DisposeAsync();
    return 0;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: gen <dir> [count] | enum <dir> [runs]");
    return 1;
}

var dir = Path.GetFullPath(args[1]);
switch (args[0])
{
    case "gen":
    {
        var count = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 100_000;
        Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();
        Parallel.For(0, count, i =>
        {
            var p = Path.Combine(dir, $"file_{i:D6}.txt");
            if (!File.Exists(p))
            {
                using var _ = File.Create(p);
            }
        });
        Console.WriteLine($"{count:N0}개 생성: {sw.ElapsedMilliseconds}ms → {dir}");
        return 0;
    }

    case "enum":
    {
        var runs = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 5;
        await using var reg = new DriveQueueRegistry();
        var provider = new LocalProvider(reg);
        var results = new List<object>();
        for (var r = 0; r < runs; r++)
        {
            var sw = Stopwatch.StartNew();
            double first = -1;
            var n = 0;
            await foreach (var b in provider.ListBatchedAsync(FsPath.Local(dir), CancellationToken.None))
            {
                if (first < 0)
                {
                    first = sw.Elapsed.TotalMilliseconds;
                }

                n += b.Count;
            }

            var total = sw.Elapsed.TotalMilliseconds;
            results.Add(new { run = r + 1, entries = n, firstBatchMs = Math.Round(first, 2), totalMs = Math.Round(total, 1) });
            Console.WriteLine($"#{r + 1}: {n:N0}개 · 첫 배치 {first:F2}ms · 전체 {total:F1}ms");
        }

        Console.WriteLine(JsonSerializer.Serialize(results));
        return 0;
    }

    default:
        Console.Error.WriteLine("unknown command: " + args[0]);
        return 1;
}
