using System.Runtime.InteropServices;

namespace Foldy.Platform.Interop;

// 셸 COM 인터페이스 (기본 제공 COM interop). vtable 순서가 Windows SDK(shobjidl_core.h)와 같아야 한다.
#pragma warning disable SYSLIB1096 // 기본 제공 COM interop 사용 (GeneratedComInterface 이전 대상)
#pragma warning disable CA1707, CA1712, IDE1006

internal enum Sigdn : uint
{
    NormalDisplay = 0,
    FileSysPath = 0x80058000,
}

[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);

    void GetParent(out IShellItem ppsi);

    void GetDisplayName(Sigdn sigdnName, out IntPtr ppszName);

    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    void Compare(IShellItem psi, uint hint, out int piOrder);
}

[ComImport]
[Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int hrResult);
    [PreserveSig] int PreRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
    [PreserveSig] int PostRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrRename, IShellItem? psiNewlyCreated);
    [PreserveSig] int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
    [PreserveSig] int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrMove, IShellItem? psiNewlyCreated);
    [PreserveSig] int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
    [PreserveSig] int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated);
    [PreserveSig] int PreDeleteItem(uint dwFlags, IShellItem psiItem);
    [PreserveSig] int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated);
    [PreserveSig] int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
    [PreserveSig] int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem);
    [PreserveSig] int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}

[ComImport]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    uint Advise(IFileOperationProgressSink pfops);
    void Unadvise(uint dwCookie);
    void SetOperationFlags(uint dwOperationFlags);
    void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
    void SetProgressDialog([MarshalAs(UnmanagedType.IUnknown)] object popd);
    void SetProperties([MarshalAs(UnmanagedType.IUnknown)] object pproparray);
    void SetOwnerWindow(IntPtr hwndOwner);
    void ApplyPropertiesToItem(IShellItem psiItem);
    void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
    void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IFileOperationProgressSink? pfopsItem);
    void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
    void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IFileOperationProgressSink? pfopsItem);
    void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
    void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IFileOperationProgressSink? pfopsItem);
    void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
    void DeleteItem(IShellItem psiItem, IFileOperationProgressSink? pfopsItem);
    void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
    void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IFileOperationProgressSink? pfopsItem);
    void PerformOperations();
    [return: MarshalAs(UnmanagedType.Bool)]
    bool GetAnyOperationsAborted();
}

[ComImport]
[Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
internal class FileOperationClass
{
}

[ComImport]
[Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
    void EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
    void BindToObject(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
    void BindToStorage(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
    [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
    void CreateViewObject(IntPtr hwndOwner, [In] ref Guid riid, out IntPtr ppv);
    void GetAttributesOf(uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
    void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, [In] ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
    void GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
    void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CMINVOKECOMMANDINFOEX
{
    public int cbSize;
    public uint fMask;
    public IntPtr hwnd;
    public IntPtr lpVerb;
    [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
    [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
    public int nShow;
    public uint dwHotKey;
    public IntPtr hIcon;
    [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
    public IntPtr lpVerbW;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
    public int ptInvokeX;
    public int ptInvokeY;
}

[ComImport]
[Guid("000214e4-0000-0000-c000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
}

[ComImport]
[Guid("000214f4-0000-0000-c000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu2
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
}

[ComImport]
[Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu3
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHFILEINFOW
{
    public IntPtr hIcon;
    public int iIcon;
    public uint dwAttributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
}

internal static class Native
{
    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    public static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    public static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    public static extern int SHBindToParent(IntPtr pidl, [In] ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    public static extern int SHGetDesktopFolder(out IntPtr ppshf);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "InsertMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InsertMenu(IntPtr hMenu, uint uPosition, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int AssocQueryStringW(uint flags, uint str, string pszAssoc, string? pszExtra, [Out] char[]? pszOut, ref uint pcchOut);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    public static extern int WNetGetConnectionW(string lpLocalName, [Out] char[] lpRemoteName, ref int lpnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetDriveTypeW(string lpRootPathName);

    [DllImport("kernel32.dll")]
    public static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformationW(string root, [Out] char[] volumeName, int volumeNameSize, out uint serial, out uint maxComponent, out uint flags, [Out] char[] fsName, int fsNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceExW(string dir, out ulong freeToCaller, out ulong total, out ulong totalFree);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CancelSynchronousIo(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    public static IShellItem ShellItem(string path)
    {
        var iid = IID_IShellItem;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
        return item;
    }

    public static string? FileSysPath(IShellItem? item)
    {
        if (item is null)
        {
            return null;
        }

        try
        {
            item.GetDisplayName(Sigdn.FileSysPath, out var p);
            try
            {
                return Marshal.PtrToStringUni(p);
            }
            finally
            {
                CoTaskMemFree(p);
            }
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static string DisplayName(IShellItem item)
    {
        try
        {
            item.GetDisplayName(Sigdn.NormalDisplay, out var p);
            try
            {
                return Marshal.PtrToStringUni(p) ?? "";
            }
            finally
            {
                CoTaskMemFree(p);
            }
        }
        catch (COMException)
        {
            return "";
        }
    }

}
