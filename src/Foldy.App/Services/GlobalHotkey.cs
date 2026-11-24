using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Foldy.App.Services;

/// <summary>
/// 다른 프로그램을 쓰는 중에도 눌리는 단축키 (빠른 검색). Windows RegisterHotKey를 쓴다.
/// 다른 프로그램이 이미 쓰고 있으면 등록이 실패하고, 그때는 조용히 넘어가지 않고 사용자에게 알린 뒤 다른 키를 고르게 한다.
/// </summary>
internal static class GlobalHotkey
{
    public const int WmHotkey = 0x0312;
    public const int QuickSearchId = 0xF01D;

    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>바꿔 고를 수 있는 키 (처음 키가 겹칠 때 보여 주는 후보, 앞쪽이 추천).</summary>
    public static readonly string[] Candidates = ["Ctrl+Space", "Ctrl+Shift+Space", "Ctrl+Alt+Space", "Ctrl+Alt+F", "Win+Shift+F"];

    public static bool TryParse(string? gesture, out ModifierKeys mods, out Key key)
    {
        mods = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        foreach (var part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                    mods |= ModifierKeys.Control;
                    break;
                case "ALT":
                    mods |= ModifierKeys.Alt;
                    break;
                case "SHIFT":
                    mods |= ModifierKeys.Shift;
                    break;
                case "WIN":
                    mods |= ModifierKeys.Windows;
                    break;
                default:
                    if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                    {
                        key = char.IsDigit(part[0]) ? Key.D0 + (part[0] - '0') : Key.A + (char.ToUpperInvariant(part[0]) - 'A');
                    }
                    else if (!Enum.TryParse(part, ignoreCase: true, out key))
                    {
                        return false;
                    }

                    break;
            }
        }

        // 다른 프로그램에서도 눌리는 키라서 수식 키 하나는 꼭 있어야 한다 (Space 하나만으로는 글을 못 쓴다)
        return key != Key.None && mods != ModifierKeys.None;
    }

    public static bool Register(IntPtr hwnd, int id, string? gesture)
    {
        Unregister(hwnd, id);
        return TryNative(gesture, out var mods, out var vk) && RegisterHotKey(hwnd, id, mods | ModNoRepeat, vk);
    }

    /// <summary>RegisterHotKey에 넘기는 값 (수식 키 비트, 가상 키 코드). 단축키 도우미 설정 파일에도 이 값을 적는다.</summary>
    public static bool TryNative(string? gesture, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (!TryParse(gesture, out var m, out var key))
        {
            return false;
        }

        if (m.HasFlag(ModifierKeys.Control))
        {
            mods |= ModControl;
        }

        if (m.HasFlag(ModifierKeys.Alt))
        {
            mods |= ModAlt;
        }

        if (m.HasFlag(ModifierKeys.Shift))
        {
            mods |= ModShift;
        }

        if (m.HasFlag(ModifierKeys.Windows))
        {
            mods |= ModWin;
        }

        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    public static void Unregister(IntPtr hwnd, int id) => UnregisterHotKey(hwnd, id);

    /// <summary>
    /// 지금 이 키를 쓸 수 있는지 (잠깐 등록해 보고 바로 푼다). 이미 다른 프로그램이 쓰면 false.
    /// Ctrl+Space는 중국어·일본어 입력기가 한/영 같은 전환 키로 쓰므로, 그 입력기가 설치돼 있으면 겹친다고 본다.
    /// </summary>
    public static bool IsFree(IntPtr hwnd, string gesture)
    {
        if (ConflictsWithInputMethod(gesture))
        {
            return false;
        }

        const int probe = 0xF0FE;
        var ok = Register(hwnd, probe, gesture);
        Unregister(hwnd, probe);
        return ok;
    }

    public static bool ConflictsWithInputMethod(string gesture)
    {
        if (!TryParse(gesture, out var mods, out var key) || key != Key.Space || mods != ModifierKeys.Control)
        {
            return false;
        }

        // 설치된 키보드 배열의 언어 (LANGID 아래 10비트가 주 언어: 0x04 중국어, 0x11 일본어)
        var n = GetKeyboardLayoutList(0, null);
        if (n <= 0)
        {
            return false;
        }

        var list = new IntPtr[n];
        n = GetKeyboardLayoutList(n, list);
        for (var i = 0; i < n; i++)
        {
            var primary = (int)((long)list[i] & 0x3FF);
            if (primary is 0x04 or 0x11)
            {
                return true;
            }
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);
}
