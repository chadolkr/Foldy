using System.Runtime.InteropServices;
using Foldy.Platform.Interop;

namespace Foldy.Platform.Shell;

/// <summary>
/// 탐색기의 우클릭 메뉴를 그대로 띄운다 (설계서 5.4: IShellFolder.GetUIObjectOf → IContextMenu → TrackPopupMenuEx).
/// "보내기", "연결 프로그램" 같은 하위 메뉴를 그리려면 소유 창의 메시지를 <see cref="HandleWindowMessage"/>로 넘겨야 한다.
/// UI 스레드(STA)에서 호출한다. 타사 셸 확장의 예외는 여기서 잡아 앱이 죽지 않게 한다.
/// </summary>
public sealed class ShellContextMenu
{
    private const uint CMF_NORMAL = 0x0;
    private const uint CMF_EXPLORE = 0x4;
    private const uint CMF_CANRENAME = 0x10;
    private const uint CMF_EXTENDEDVERBS = 0x100;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;
    private const uint FirstId = 1;
    private const uint LastId = 0x7FFF;

    private IContextMenu2? _menu2;
    private IContextMenu3? _menu3;

    /// <summary>창 메시지 훅: 처리했으면 true.</summary>
    public bool HandleWindowMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        const int WM_INITMENUPOPUP = 0x0117, WM_DRAWITEM = 0x002B, WM_MEASUREITEM = 0x002C, WM_MENUCHAR = 0x0120;
        if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR))
        {
            return false;
        }

        try
        {
            if (_menu3 is not null && _menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out result) == 0)
            {
                return true;
            }

            if (_menu2 is not null && _menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // 셸 확장 오류는 무시
        }

        return false;
    }

    /// <summary>
    /// 메뉴를 띄우고 고른 명령을 실행한다. 반환값: 실행한 동사(verb) 이름 또는 null.
    /// "rename"을 고르면 실행하지 않고 그대로 돌려준다 (Foldy의 이름 바꾸기 입력으로 처리).
    /// </summary>
    public string? Show(IntPtr hwnd, IReadOnlyList<string> paths, int screenX, int screenY, bool extended, string? backgroundFolder = null)
    {
        var pidls = new List<IntPtr>();
        IntPtr folderPtr = IntPtr.Zero, menuPtr = IntPtr.Zero, hmenu = IntPtr.Zero;
        IContextMenu? menu = null;
        try
        {
            if (backgroundFolder is not null)
            {
                menu = BackgroundMenu(hwnd, backgroundFolder, pidls, out folderPtr);
            }
            else
            {
                menu = ItemsMenu(hwnd, paths, pidls, out folderPtr);
            }

            if (menu is null)
            {
                return null;
            }

            _menu2 = menu as IContextMenu2;
            _menu3 = menu as IContextMenu3;
            hmenu = Native.CreatePopupMenu();
            var flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME | (extended ? CMF_EXTENDEDVERBS : 0);
            if (menu.QueryContextMenu(hmenu, 0, FirstId, LastId, flags) < 0)
            {
                return null;
            }

            var cmd = Native.TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, hwnd, IntPtr.Zero);
            if (cmd == 0)
            {
                return null;
            }

            var verb = Verb(menu, cmd - FirstId);
            if (string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase))
            {
                return verb;
            }

            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                hwnd = hwnd,
                lpVerb = (IntPtr)(cmd - FirstId),
                lpVerbW = (IntPtr)(cmd - FirstId),
                lpDirectoryW = backgroundFolder ?? Path.GetDirectoryName(paths[0]),
                nShow = 1,
                ptInvokeX = screenX,
                ptInvokeY = screenY,
            };
            menu.InvokeCommand(ref info);
            return verb ?? "";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or SEHException)
        {
            return null;
        }
        finally
        {
            _menu2 = null;
            _menu3 = null;
            if (hmenu != IntPtr.Zero)
            {
                Native.DestroyMenu(hmenu);
            }

            if (menu is not null)
            {
                Marshal.ReleaseComObject(menu);
            }

            if (folderPtr != IntPtr.Zero)
            {
                Marshal.Release(folderPtr);
            }

            if (menuPtr != IntPtr.Zero)
            {
                Marshal.Release(menuPtr);
            }

            foreach (var p in pidls)
            {
                Native.ILFree(p);
            }
        }
    }

    private static IContextMenu? ItemsMenu(IntPtr hwnd, IReadOnlyList<string> paths, List<IntPtr> pidls, out IntPtr folderPtr)
    {
        folderPtr = IntPtr.Zero;
        var children = new List<IntPtr>();
        IShellFolder? parent = null;
        foreach (var p in paths)
        {
            if (Native.SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) != 0)
            {
                continue;
            }

            pidls.Add(pidl);
            var iid = Native.IID_IShellFolder;
            if (Native.SHBindToParent(pidl, ref iid, out var pf, out var last) != 0)
            {
                continue;
            }

            if (parent is null)
            {
                folderPtr = pf;
                parent = (IShellFolder)Marshal.GetObjectForIUnknown(pf);
            }
            else
            {
                Marshal.Release(pf); // 같은 폴더의 항목이라고 가정 (한 패널의 선택)
            }

            children.Add(last);
        }

        if (parent is null || children.Count == 0)
        {
            return null;
        }

        var cm = Native.IID_IContextMenu;
        parent.GetUIObjectOf(hwnd, (uint)children.Count, [.. children], ref cm, IntPtr.Zero, out var ppv);
        try
        {
            return (IContextMenu)Marshal.GetObjectForIUnknown(ppv);
        }
        finally
        {
            Marshal.Release(ppv);
        }
    }

    private static IContextMenu? BackgroundMenu(IntPtr hwnd, string folder, List<IntPtr> pidls, out IntPtr folderPtr)
    {
        folderPtr = IntPtr.Zero;
        if (Native.SHParseDisplayName(folder, IntPtr.Zero, out var pidl, 0, out _) != 0)
        {
            return null;
        }

        pidls.Add(pidl);
        if (Native.SHGetDesktopFolder(out var desktopPtr) != 0)
        {
            return null;
        }

        try
        {
            var desktop = (IShellFolder)Marshal.GetObjectForIUnknown(desktopPtr);
            var iid = Native.IID_IShellFolder;
            desktop.BindToObject(pidl, IntPtr.Zero, ref iid, out folderPtr);
            var sf = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            var cm = Native.IID_IContextMenu;
            sf.CreateViewObject(hwnd, ref cm, out var ppv);
            try
            {
                return (IContextMenu)Marshal.GetObjectForIUnknown(ppv);
            }
            finally
            {
                Marshal.Release(ppv);
            }
        }
        finally
        {
            Marshal.Release(desktopPtr);
        }
    }

    private static string? Verb(IContextMenu menu, uint id)
    {
        const uint GCS_VERBW = 0x4;
        var buf = Marshal.AllocHGlobal(512);
        try
        {
            Marshal.WriteInt16(buf, 0);
            return menu.GetCommandString((UIntPtr)id, GCS_VERBW, IntPtr.Zero, buf, 256) == 0 ? Marshal.PtrToStringUni(buf) : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
