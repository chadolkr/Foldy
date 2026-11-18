using System.Runtime.InteropServices;

namespace Foldy.Platform.Imaging;

/// <summary>
/// 썸네일·미리보기 디코더 (설계서 5.12 "썸네일을 빠르게 만드는 방법").
/// <list type="number">
/// <item>Windows 셸 썸네일 캐시 (IShellItemImageFactory, 캐시에 있는 것만). 탐색기가 만든 것을 재사용한다.</item>
/// <item>JPEG이면 EXIF 내장 썸네일 (충분히 크면 최종, 작으면 먼저 보여 주기용).</item>
/// <item>WIC로 필요한 크기로만 축소 디코딩 (EXIF 방향 적용).</item>
/// </list>
/// 클라우드 전용 파일은 내용을 열지 않고 1번만 쓴다.
/// <para>
/// 스레드: 전용 STA 스레드 묶음(코어 수의 절반)에서 COM을 부른다. 대기열은 나중 요청이 먼저(LIFO)라
/// 스크롤로 새로 보이는 칸이 먼저 채워지고, 취소된(지나간) 요청은 꺼낼 때 버린다.
/// 공개 메서드는 아무 스레드에서나 불러도 된다.
/// </para>
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    /// <summary>EXIF 썸네일의 긴 변이 요청 크기의 이 비율 이상이면 최종 결과로 쓴다.</summary>
    private const double ExifAcceptRatio = 0.75;

    /// <summary>미리보기 최대 변 (0 = 원본 요청도 여기서 자른다). 메모리 폭주 방지.</summary>
    private const int MaxPreviewEdge = 16384;

    /// <summary>클라우드 파일 미리보기는 셸 캐시에서 이 크기까지만 찾는다.</summary>
    private const int CloudPreviewEdge = 1024;

    /// <summary>이 크기 이하 파일은 통째로 읽어 메모리에서 디코딩한다.</summary>
    private const long InMemoryDecodeLimit = 96L * 1024 * 1024;

    private static readonly Lazy<ThumbnailService> SharedInstance = new(() => new ThumbnailService());

    private readonly object _gate = new(); // Monitor.Wait/Pulse를 쓰므로 Lock 대신 object
    private readonly List<Job> _high = [];
    private readonly List<Job> _normal = [];
    private readonly Thread[] _threads;
    private bool _disposed;
    private long _contentReads;
    private long _shellHits;
    private long _exifHits;
    private long _wicDecodes;
    private long _shellTicks;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Hits, int Misses)> _shellStats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>폴더마다 처음 이만큼은 셸 캐시를 확인한다.</summary>
    private const int ShellProbeCount = 8;

    /// <param name="workerCount">STA 작업 스레드 수. 0이면 코어 수의 절반(2~8).</param>
    public ThumbnailService(int workerCount = 0)
    {
        var n = workerCount > 0 ? workerCount : Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        _threads = new Thread[n];
        for (var i = 0; i < n; i++)
        {
            var t = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"ThumbSta {i + 1}",
                Priority = ThreadPriority.BelowNormal,
            };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            _threads[i] = t;
        }
    }

    /// <summary>앱 전체가 함께 쓰는 인스턴스.</summary>
    public static ThumbnailService Shared => SharedInstance.Value;

    public int WorkerCount => _threads.Length;

    /// <summary>셸 썸네일 캐시를 먼저 볼지 (벤치에서 단계별 시간을 재기 위해 끌 수 있다).</summary>
    public bool UseShellCache { get; set; } = true;

    /// <summary>셸 캐시 조회에 쓴 누적 시간(ms, 진단용).</summary>
    public double ShellLookupMs => Interlocked.Read(ref _shellTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>파일 내용을 연 횟수 (진단·테스트용: 클라우드 파일은 0이어야 한다).</summary>
    public long ContentReadCount => Interlocked.Read(ref _contentReads);

    public long ShellCacheHits => Interlocked.Read(ref _shellHits);

    public long ExifHits => Interlocked.Read(ref _exifHits);

    public long WicDecodes => Interlocked.Read(ref _wicDecodes);

    /// <summary>
    /// 이미지를 긴 변 <paramref name="maxEdge"/> 픽셀 이하로 불러온다 (0 = 원본 크기, 미리보기용).
    /// 만들 수 없으면 null. 취소되면 TaskCanceledException.
    /// </summary>
    public Task<PixelImage?> LoadAsync(string path, int maxEdge, ImageLoadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<PixelImage?>(cancellationToken);
        }

        var job = new Job(path, Math.Max(0, maxEdge), options, cancellationToken);
        if (cancellationToken.CanBeCanceled)
        {
            // 대기 중에 취소되면 바로 끝낸다. 작업 스레드는 꺼낼 때 완료된 작업을 건너뛴다.
            job.Registration = cancellationToken.Register(static s => ((Job)s!).Tcs.TrySetCanceled(), job);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                job.Registration.Dispose();
                throw new ObjectDisposedException(nameof(ThumbnailService));
            }

            (options.HighPriority ? _high : _normal).Add(job);
            Monitor.Pulse(_gate);
        }

        return job.Tcs.Task;
    }

    /// <summary>썸네일용 간편 호출.</summary>
    public Task<PixelImage?> GetThumbnailAsync(string path, int size, CancellationToken cancellationToken = default) =>
        LoadAsync(path, size, ImageLoadOptions.Default, cancellationToken);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var j in _high.Concat(_normal))
            {
                j.Tcs.TrySetCanceled();
            }

            _high.Clear();
            _normal.Clear();
            Monitor.PulseAll(_gate);
        }

        foreach (var t in _threads)
        {
            t.Join(TimeSpan.FromSeconds(2));
        }
    }

    // ───────────── 작업 스레드 ─────────────

    private void WorkerLoop()
    {
        IWICImagingFactory? factory = null;
        try
        {
            // 셸·WIC 첫 호출은 DLL 로드로 수백 ms가 든다(실측): 첫 폴더를 열기 전에 미리 데워 둔다
            try
            {
                factory = (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(ImagingNative.ClsidWicImagingFactory, true)!)!;
                if (UseShellCache && Environment.ProcessPath is { } exe)
                {
                    _ = TryShellCache(exe, 32);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                factory = null;
            }

            while (TakeNext() is { } job)
            {
                job.Registration.Dispose();
                if (job.Tcs.Task.IsCompleted)
                {
                    continue; // 스크롤로 지나가 취소된 요청
                }

                try
                {
                    factory ??= (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(ImagingNative.ClsidWicImagingFactory, true)!)!;
                    var result = Process(job, factory);
                    job.Tcs.TrySetResult(result);
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException or ArgumentException or OutOfMemoryException)
                {
                    job.Tcs.TrySetResult(null);
                }
                catch (OperationCanceledException)
                {
                    job.Tcs.TrySetCanceled();
                }
            }
        }
        finally
        {
            if (factory is not null)
            {
                Marshal.ReleaseComObject(factory);
            }
        }
    }

    private Job? TakeNext()
    {
        lock (_gate)
        {
            while (true)
            {
                if (_disposed)
                {
                    return null;
                }

                var list = _high.Count > 0 ? _high : _normal.Count > 0 ? _normal : null;
                if (list is not null)
                {
                    var job = list[^1];
                    list.RemoveAt(list.Count - 1);
                    return job;
                }

                Monitor.Wait(_gate); // STA에서의 관리 대기는 COM 메시지를 펌프한다
            }
        }
    }

    private PixelImage? Process(Job job, IWICImagingFactory factory)
    {
        var path = job.Path;
        var opts = job.Options;
        var cloud = CloudFileGuard.MustNotReadContent(path);
        var preview = opts.Purpose == ImageLoadPurpose.Preview;
        var maxEdge = job.MaxEdge == 0 ? MaxPreviewEdge : Math.Min(job.MaxEdge, MaxPreviewEdge);

        // 1. 셸 캐시: 썸네일은 항상, 미리보기는 내용을 못 읽을 때만
        // 셸 캐시 조회는 없을 때도 ~20ms가 든다(실측). 내용을 읽을 수 있는 폴더에서 처음 몇 개가 모두 없으면
        // 탐색기가 이 폴더를 연 적이 없는 것이므로 나머지는 건너뛴다. 클라우드·네트워크는 셸이 유일한 길이라 항상 본다.
        var mustUseShell = cloud || !opts.AllowContentRead;
        var dir = Path.GetDirectoryName(path) ?? "";
        if (mustUseShell || (UseShellCache && !preview && !ShellMissesDominate(dir)))
        {
            var shellEdge = preview ? Math.Min(maxEdge, CloudPreviewEdge) : maxEdge;
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var shell = TryShellCache(path, shellEdge);
            Interlocked.Add(ref _shellTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            RecordShell(dir, shell is not null);
            if (shell is not null)
            {
                Interlocked.Increment(ref _shellHits);
                return shell;
            }
        }

        if (cloud || !opts.AllowContentRead)
        {
            return null; // 클라우드 전용·네트워크: 내용을 열지 않는다
        }

        job.Cancellation.ThrowIfCancellationRequested();

        // 2. JPEG: EXIF 방향과 내장 썸네일
        var orientation = 1;
        if (IsJpeg(path))
        {
            Interlocked.Increment(ref _contentReads);
            var exif = ExifReader.TryReadFile(path);
            if (exif is not null)
            {
                orientation = exif.Orientation;
                if (!preview && !exif.Thumbnail.IsEmpty && DecodeMemory(factory, exif.Thumbnail, maxEdge, orientation, ThumbnailSource.ExifEmbedded, false) is { } small)
                {
                    if (Math.Max(small.Width, small.Height) >= ExifAcceptRatio * maxEdge)
                    {
                        Interlocked.Increment(ref _exifHits);
                        return small;
                    }

                    opts.Preliminary?.Invoke(small);
                }
            }
        }

        job.Cancellation.ThrowIfCancellationRequested();

        // 3. WIC 축소 디코딩
        Interlocked.Increment(ref _contentReads);
        var image = DecodeFile(factory, path, maxEdge, orientation);
        if (image is not null)
        {
            Interlocked.Increment(ref _wicDecodes);
        }

        return image;
    }

    /// <summary>이 폴더에서 셸 캐시가 계속 빗나갔는지 (적중이 한 번이라도 있으면 계속 본다).</summary>
    private bool ShellMissesDominate(string dir) =>
        _shellStats.TryGetValue(dir, out var s) && s.Hits == 0 && s.Misses >= ShellProbeCount;

    private void RecordShell(string dir, bool hit)
    {
        _shellStats.AddOrUpdate(dir, _ => hit ? (1, 0) : (0, 1), (_, s) => hit ? (s.Hits + 1, s.Misses) : (s.Hits, s.Misses + 1));
        if (_shellStats.Count > 4096)
        {
            _shellStats.Clear(); // 오래 돌면 가끔 비운다
        }
    }

    private static bool IsJpeg(string path) =>
        Path.GetExtension(path).ToUpperInvariant() is ".JPG" or ".JPEG" or ".JPE" or ".JFIF";

    // ───────────── 셸 썸네일 캐시 ─────────────

    private static PixelImage? TryShellCache(string path, int size)
    {
        var hr = ImagingNative.SHCreateItemFromParsingName(path, IntPtr.Zero, ImagingNative.IidShellItemImageFactory, out var ptr);
        if (hr < 0 || ptr == IntPtr.Zero)
        {
            return null;
        }

        IShellItemImageFactory? sf = null;
        try
        {
            sf = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ptr);
            var flags = ImagingNative.SiigbfResizeToFit | ImagingNative.SiigbfThumbnailOnly | ImagingNative.SiigbfInCacheOnly;
            hr = sf.GetImage(new NativeSize { Cx = size, Cy = size }, flags, out var hbm);
            if (hr < 0 || hbm == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return FromHBitmap(hbm);
            }
            finally
            {
                ImagingNative.DeleteObject(hbm);
            }
        }
        finally
        {
            if (sf is not null)
            {
                Marshal.ReleaseComObject(sf);
            }

            Marshal.Release(ptr);
        }
    }

    /// <summary>32bpp HBITMAP → 위에서 아래 PBGRA. 알파가 모두 0이면(알파 없는 비트맵) 불투명으로 본다.</summary>
    internal static unsafe PixelImage? FromHBitmap(IntPtr hbm)
    {
        if (ImagingNative.GetObject(hbm, Marshal.SizeOf<NativeBitmap>(), out var bm) == 0 || bm.Width <= 0 || bm.Height == 0)
        {
            return null;
        }

        var w = bm.Width;
        var h = Math.Abs(bm.Height);
        var pixels = new byte[w * h * 4];
        var bih = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = w,
            Height = -h, // 위에서 아래
            Planes = 1,
            BitCount = 32,
            Compression = 0, // BI_RGB
        };

        var hdc = ImagingNative.GetDC(IntPtr.Zero);
        try
        {
            fixed (byte* p = pixels)
            {
                if (ImagingNative.GetDIBits(hdc, hbm, 0, (uint)h, p, ref bih, 0) == 0)
                {
                    return null;
                }
            }
        }
        finally
        {
            _ = ImagingNative.ReleaseDC(IntPtr.Zero, hdc);
        }

        var anyAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                anyAlpha = true;
                break;
            }
        }

        if (!anyAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        return new PixelImage(w, h, pixels, ThumbnailSource.ShellCache, 0, 0);
    }

    // ───────────── WIC ─────────────

    private static PixelImage? DecodeFile(IWICImagingFactory factory, string path, int maxEdge, int orientation)
    {
        // 직접 열어 삭제·이름 변경을 막지 않는다 (FileShare.Delete).
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var pickLargest = Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase);
        if (fs.Length <= InMemoryDecodeLimit)
        {
            // 핸들 스트림은 디코더가 작은 조각으로 읽어 JPEG에서 수십 배 느렸다(실측). 한 번에 읽어 메모리에서 디코딩한다.
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            return DecodeMemory(factory, bytes, maxEdge, orientation, ThumbnailSource.WicDecode, pickLargest);
        }

        var hr = factory.CreateDecoderFromFileHandle(fs.SafeFileHandle.DangerousGetHandle(), IntPtr.Zero, ImagingNative.WicDecodeMetadataCacheOnDemand, out var decoder);
        if (hr < 0 || decoder is null)
        {
            return null;
        }

        try
        {
            return DecodeFirstFrame(factory, decoder, maxEdge, orientation, ThumbnailSource.WicDecode, pickLargest);
        }
        finally
        {
            Marshal.ReleaseComObject(decoder);
        }
    }

    private static unsafe PixelImage? DecodeMemory(IWICImagingFactory factory, ReadOnlyMemory<byte> data, int maxEdge, int orientation, ThumbnailSource source, bool pickLargest)
    {
        using var pin = data.Pin();
        var stream = ImagingNative.SHCreateMemStream((byte*)pin.Pointer, (uint)data.Length); // 내용을 복사한다
        if (stream == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var hr = factory.CreateDecoderFromStream(stream, IntPtr.Zero, ImagingNative.WicDecodeMetadataCacheOnDemand, out var decoder);
            if (hr < 0 || decoder is null)
            {
                return null;
            }

            try
            {
                return DecodeFirstFrame(factory, decoder, maxEdge, orientation, source, pickLargest);
            }
            finally
            {
                Marshal.ReleaseComObject(decoder);
            }
        }
        finally
        {
            Marshal.Release(stream);
        }
    }

    /// <summary>
    /// 첫 프레임(움직이는 GIF도 첫 프레임만, ICO는 가장 큰 프레임)을 축소 → 회전 → PBGRA 변환해 복사한다.
    /// 축소를 먼저 해서 회전·변환이 작은 이미지에서 일어나게 한다 (JPEG은 디코더 단계 축소가 쓰인다).
    /// </summary>
    private static unsafe PixelImage? DecodeFirstFrame(IWICImagingFactory factory, IWICBitmapDecoder decoder, int maxEdge, int orientation, ThumbnailSource source, bool pickLargest)
    {
        if (decoder.GetFrameCount(out var frames) < 0 || frames == 0)
        {
            return null;
        }

        var toRelease = new List<object>(4);
        try
        {
            if (decoder.GetFrame(0, out var frame) < 0 || frame is null)
            {
                return null;
            }

            toRelease.Add(frame);
            frame.GetSize(out var w, out var h);
            if (pickLargest)
            {
                for (uint i = 1; i < Math.Min(frames, 32u); i++)
                {
                    if (decoder.GetFrame(i, out var f) >= 0 && f is not null)
                    {
                        toRelease.Add(f);
                        f.GetSize(out var fw, out var fh);
                        if ((long)fw * fh > (long)w * h)
                        {
                            (frame, w, h) = (f, fw, fh);
                        }
                    }
                }
            }

            if (w == 0 || h == 0)
            {
                return null;
            }

            IWICBitmapSource current = frame;
            var longEdge = Math.Max(w, h);
            if (maxEdge > 0 && longEdge > maxEdge)
            {
                var scale = (double)maxEdge / longEdge;
                var tw = (uint)Math.Max(1, Math.Round(w * scale));
                var th = (uint)Math.Max(1, Math.Round(h * scale));
                Check(factory.CreateBitmapScaler(out var scaler));
                toRelease.Add(scaler);
                Check(scaler.Initialize(current, tw, th, ImagingNative.WicInterpolationFant));
                current = (IWICBitmapSource)scaler;
            }

            var transform = OrientationToTransform(orientation);
            if (transform != 0)
            {
                Check(factory.CreateBitmapFlipRotator(out var rot));
                toRelease.Add(rot);
                Check(rot.Initialize(current, transform));
                current = (IWICBitmapSource)rot;
            }

            Check(factory.CreateFormatConverter(out var conv));
            toRelease.Add(conv);
            Check(conv.Initialize(current, ImagingNative.Pbgra32, ImagingNative.WicDitherNone, IntPtr.Zero, 0, ImagingNative.WicPaletteCustom));
            Check(conv.GetSize(out var ow, out var oh));

            var stride = checked((int)ow * 4);
            var pixels = new byte[checked(stride * (int)oh)];
            fixed (byte* p = pixels)
            {
                Check(conv.CopyPixels(IntPtr.Zero, (uint)stride, (uint)pixels.Length, (IntPtr)p));
            }

            var swap = orientation is >= 5 and <= 8;
            return new PixelImage((int)ow, (int)oh, pixels, source, (int)(swap ? h : w), (int)(swap ? w : h));
        }
        finally
        {
            foreach (var o in toRelease)
            {
                Marshal.ReleaseComObject(o);
            }
        }
    }

    /// <summary>
    /// EXIF 방향 → WICBitmapTransformOptions (회전은 시계 방향, 회전 뒤 뒤집기).
    /// 1 그대로, 2 좌우, 3 180°, 4 상하, 5 전치, 6 90°, 7 역전치, 8 270°.
    /// </summary>
    internal static int OrientationToTransform(int orientation) => orientation switch
    {
        2 => 8,       // FlipHorizontal
        3 => 2,       // Rotate180
        4 => 16,      // FlipVertical
        5 => 1 | 8,   // Rotate90 + FlipHorizontal
        6 => 1,       // Rotate90
        7 => 3 | 8,   // Rotate270 + FlipHorizontal
        8 => 3,       // Rotate270
        _ => 0,
    };

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    private sealed class Job(string path, int maxEdge, ImageLoadOptions options, CancellationToken ct)
    {
        public string Path { get; } = path;

        public int MaxEdge { get; } = maxEdge;

        public ImageLoadOptions Options { get; } = options;

        public CancellationToken Cancellation { get; } = ct;

        public TaskCompletionSource<PixelImage?> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }
    }
}
