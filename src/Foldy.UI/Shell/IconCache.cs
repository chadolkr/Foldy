using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Foldy.Platform.Shell;
using Foldy.Platform.SystemInfo;
using Foldy.Presentation.Files;

namespace Foldy.UI.Shell;

/// <summary>
/// 목록 아이콘 캐시 (설계서 5.4 IconService).
/// <list type="bullet">
/// <item>확장자별 아이콘은 SHGFI_USEFILEATTRIBUTES로 디스크 접근 없이 즉시 (UI 스레드에서 처음 한 번만).</item>
/// <item>exe·lnk·ico처럼 파일마다 다른 아이콘은 로컬 고정 드라이브에서만, 보이는 행만 백그라운드 STA에서 만든 뒤 다시 그린다.</item>
/// <item>결과는 Freeze한 BitmapSource.</item>
/// </list>
/// </summary>
public sealed class IconCache
{
    private readonly Dictionary<string, ImageSource?> _byExt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ImageSource?> _byFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingQueue _queue;
    private ImageSource? _folder;

    public IconCache()
    {
        _queue = new BlockingQueue(this);
    }

    /// <summary>파일별 아이콘이 도착했을 때 (UI 스레드에서 발생).</summary>
    public event Action? IconsUpdated;

    public ImageSource? Get(object o)
    {
        if (o is not FileItem item)
        {
            return null;
        }

        if (item.IsFolder)
        {
            return _folder ??= Load(ShellInfo.GenericIcon("", isFolder: true));
        }

        var ext = item.Extension;
        if (ShellInfo.PerFileIconExtensions.Contains(ext) && IsLocalFixed(item.FullPath))
        {
            if (_byFile.TryGetValue(item.FullPath, out var img))
            {
                return img ?? Generic(ext);
            }

            if (_pending.TryAdd(item.FullPath, 0))
            {
                _queue.Add(item.FullPath);
            }
        }

        return Generic(ext);
    }

    private ImageSource? Generic(string ext)
    {
        if (!_byExt.TryGetValue(ext, out var img))
        {
            img = Load(ShellInfo.GenericIcon(ext, isFolder: false));
            _byExt[ext] = img;
        }

        return img;
    }

    private static bool IsLocalFixed(string path) =>
        path.Length > 2 && path[1] == ':' && Drives.TypeOf(path[..3]) == NativeDriveType.Fixed;

    private static BitmapSource? Load(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
        finally
        {
            ShellInfo.DestroyIcon(hIcon);
        }
    }

    /// <summary>파일별 아이콘 전용 STA 스레드 하나 (셸 COM 객체는 만든 스레드 밖으로 내보내지 않는다).</summary>
    private sealed class BlockingQueue
    {
        private readonly System.Collections.Concurrent.BlockingCollection<string> _items = new(new ConcurrentStack<string>());
        private readonly IconCache _owner;
        private readonly Dispatcher _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        private int _scheduled;

        public BlockingQueue(IconCache owner)
        {
            _owner = owner;
            var t = new Thread(Loop) { IsBackground = true, Name = "ShellSta Icons", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        public void Add(string path) => _items.Add(path);

        private void Loop()
        {
            foreach (var path in _items.GetConsumingEnumerable())
            {
                var img = Load(ShellInfo.FileIcon(path));
                _owner._byFile[path] = img;
                _owner._pending.TryRemove(path, out _);
                if (Interlocked.Exchange(ref _scheduled, 1) == 0)
                {
                    // 여러 아이콘을 한 번에 다시 그린다 (최대 초당 10회)
                    _ui.BeginInvoke(async () =>
                    {
                        await Task.Delay(100);
                        Interlocked.Exchange(ref _scheduled, 0);
                        _owner.IconsUpdated?.Invoke();
                    });
                }
            }
        }
    }
}
