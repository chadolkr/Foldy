using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Foldy.Platform.Interop;

namespace Foldy.Platform.Shell;

/// <summary>
/// 셸 네임스페이스(IShellItem)로 폴더 목록을 읽는다 — 탐색기와 같은 경로.
/// 일반 파일 API(FindFirstFile)로는 비어 보이지만 탐색기에서는 보이는 드라이브(예: 회사 보안 드라이브 K:,
/// \\localhost\Redirector 같은 리다이렉터)에서만 대체 경로로 쓴다.
/// 셸 확장은 STA를 기대하므로 전용 STA 스레드에서 돌리고, 응답이 없으면 시간 초과로 포기한다.
/// </summary>
/// <summary>셸로 읽은 항목 하나.</summary>
public sealed record ShellEntry(string Name, string Path, bool IsFolder, long Size, DateTime ModifiedUtc);

public static class ShellListing
{
    private static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private static readonly Guid IidEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    private static readonly Guid IidShellItem2 = new("7e9fb0d3-919f-4307-ab2e-9b1860310c93");
    private static readonly Guid FmtStorage = new("b725f130-47ef-101a-a5f1-02608c9eebac");

    private const uint SfgaoFolder = 0x20000000;
    private const uint SfgaoStream = 0x00400000;
    private const uint SigdnDesktopAbsoluteParsing = 0x80028000;

    /// <summary>폴더 내용을 셸로 읽는다. 셸도 못 읽으면 null.</summary>
    public static IReadOnlyList<ShellEntry>? List(string folder, TimeSpan timeout, CancellationToken ct)
    {
        IReadOnlyList<ShellEntry>? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = ListOnSta(folder, ct);
            }
#pragma warning disable CA1031 // 셸 확장 예외는 대체 경로 실패로 본다
            catch (Exception ex)
            {
                error = ex;
            }
#pragma warning restore CA1031
        })
        { IsBackground = true, Name = "Foldy shell listing" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(timeout))
        {
            return null; // 응답 없는 셸 확장: 스레드는 버린다(백그라운드)
        }

        return error is null ? result : null;
    }

    private static List<ShellEntry> ListOnSta(string folder, CancellationToken ct)
    {
        var iidItem = typeof(IShellItem).GUID;
        Native.SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iidItem, out var parent);
        var entries = new List<ShellEntry>();
        try
        {
            var bhid = BhidEnumItems;
            var iidEnum = IidEnumShellItems;
            parent.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out var pEnum);
            var en = (IEnumShellItems)Marshal.GetObjectForIUnknown(pEnum);
            Marshal.Release(pEnum);
            try
            {
                while (!ct.IsCancellationRequested && en.Next(1, out var child, out var fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        if (ToEntry(child) is { } e)
                        {
                            entries.Add(e);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(child);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(en);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(parent);
        }

        return entries;
    }

    private static ShellEntry? ToEntry(IShellItem item)
    {
        var name = Name(item, (Sigdn)0);
        var path = Name(item, Sigdn.FileSysPath) ?? Name(item, (Sigdn)SigdnDesktopAbsoluteParsing);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
        {
            return null;
        }

        item.GetAttributes(SfgaoFolder | SfgaoStream, out var attrs);
        var isFolder = (attrs & SfgaoFolder) != 0 && (attrs & SfgaoStream) == 0;
        long size = 0;
        var modified = DateTime.MinValue;
        if (item is IShellItem2 item2)
        {
            if (!isFolder)
            {
                var key = new PropertyKey(FmtStorage, 12); // System.Size
                if (item2.GetUInt64(ref key, out var s) == 0)
                {
                    size = (long)s;
                }
            }

            var mk = new PropertyKey(FmtStorage, 14); // System.DateModified
            if (item2.GetFileTime(ref mk, out var ft) == 0)
            {
                var ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
                modified = ticks > 0 ? DateTime.FromFileTimeUtc(ticks) : DateTime.MinValue;
            }
        }

        return new ShellEntry(name, path, isFolder, size, modified);
    }

    private static string? Name(IShellItem item, Sigdn kind)
    {
        try
        {
            item.GetDisplayName(kind, out var p);
            try
            {
                return Marshal.PtrToStringUni(p);
            }
            finally
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
        catch (COMException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid FmtId = fmtid;
        public uint Pid = pid;
    }

    [ComImport]
    [Guid("70629033-e363-4a28-a567-0db78006e6d7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint celt, out IShellItem rgelt, out uint pceltFetched);

        [PreserveSig]
        int Skip(uint celt);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IEnumShellItems ppenum);
    }

    [ComImport]
    [Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        // IShellItem
        void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(Sigdn sigdnName, out IntPtr ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);

        // IShellItem2
        [PreserveSig] int GetPropertyStore(int flags, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig] int GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig] int GetPropertyDescriptionList(ref PropertyKey keyType, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig] int Update(IntPtr pbc);

        [PreserveSig] int GetProperty(ref PropertyKey key, IntPtr ppropvar);

        [PreserveSig] int GetCLSID(ref PropertyKey key, out Guid pclsid);

        [PreserveSig] int GetFileTime(ref PropertyKey key, out FILETIME pft);

        [PreserveSig] int GetInt32(ref PropertyKey key, out int pi);

        [PreserveSig] int GetString(ref PropertyKey key, out IntPtr ppsz);

        [PreserveSig] int GetUInt32(ref PropertyKey key, out uint pui);

        [PreserveSig] int GetUInt64(ref PropertyKey key, out ulong pull);

        [PreserveSig] int GetBool(ref PropertyKey key, out int pf);
    }
}
