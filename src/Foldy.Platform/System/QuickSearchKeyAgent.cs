using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Foldy.Platform.SystemInfo;

/// <summary>
/// 단축키 도우미 foldy-hotkey.exe (engine/foldy-hotkey): Foldy가 꺼져 있어도 단축키로 무엇이든 찾기 창을 연다 (1.11.0).
/// Foldy는 키를 확인한 뒤 설정 파일에 적고 도우미를 (다시) 띄운다. 도우미가 키를 쥐고 있는 동안 Foldy는 키를 등록하지 않는다
/// (둘이 같은 키를 등록할 수 없다). 도우미는 메시지 전용 창(클래스 <see cref="WindowClass"/>)을 하나 두고, 그 창으로 켜졌는지 보고 끈다.
/// 로그인 때 시작: MSI는 HKLM Run에 넣고, 그 밖의 설치(압축 파일 설치 등)는 Foldy가 사용자 Run에 넣는다.
/// </summary>
public static partial class QuickSearchKeyAgent
{
    public const string ExeName = "foldy-hotkey.exe";
    public const string WindowClass = "FoldyQuickSearchKey";
    public const string ConfigFileName = "quick-search-key.txt";
    public const string RunValueName = "Foldy Quick Search";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>설정 파일 한 줄 (도우미 parse_config와 같은 형식): RegisterHotKey 수식 키 비트와 가상 키 코드.</summary>
    public static string FormatKey(uint mods, uint vk) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{mods} {vk}");

    public const string Off = "off";

    /// <summary>Run 값: 경로에 빈칸이 있어도 되게 따옴표로 감싼다.</summary>
    public static string RunCommand(string exePath) => "\"" + exePath + "\"";

    /// <summary>지금 도우미가 켜져 단축키를 쥐고 있나 (같은 로그온 세션).</summary>
    public static bool IsRunning => Find() != IntPtr.Zero;

    /// <summary>도우미를 끈다 (있으면). 키를 바꾸거나 Foldy가 직접 키를 확인하기 전에 부른다.</summary>
    public static async Task StopAsync(TimeSpan? wait = null)
    {
        var h = Find();
        if (h == IntPtr.Zero)
        {
            return;
        }

        _ = PostMessageW(h, WmClose, IntPtr.Zero, IntPtr.Zero);
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(1.5));
        while (Find() != IntPtr.Zero && DateTime.UtcNow < until)
        {
            await Task.Delay(30).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 키를 설정 파일에 적고 도우미를 띄운다. 도우미가 키를 쥐면(창이 생기면) true.
    /// 다른 프로그램이 이미 그 키를 쓰면 도우미는 창 없이 끝나므로 false (그때는 Foldy가 직접 받는다).
    /// </summary>
    public static async Task<bool> StartAsync(string exePath, string configPath, uint mods, uint vk, TimeSpan? wait = null)
    {
        WriteConfig(configPath, FormatKey(mods, vk));
        if (IsRunning)
        {
            return true;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath) ?? "" });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }

        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(3));
        while (DateTime.UtcNow < until)
        {
            if (IsRunning)
            {
                return true;
            }

            await Task.Delay(30).ConfigureAwait(true);
        }

        return IsRunning;
    }

    public static void WriteConfig(string configPath, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 못 쓰면 도우미는 지난 값(없으면 Ctrl+Space)을 쓴다
        }
    }

    /// <summary>
    /// 로그인할 때 도우미 시작. MSI가 모든 사용자용(HKLM)으로 넣어 두었으면 사용자 쪽은 지운다(두 번 뜨지 않게, 떠도 하나만 남지만).
    /// 끌 때는 사용자 쪽만 지운다: HKLM 쪽은 남아도 도우미가 설정 파일의 off를 보고 바로 끝난다.
    /// </summary>
    public static void SetAutostart(string exePath, bool on)
    {
        try
        {
            using var user = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (on && !MachineAutostart())
            {
                user.SetValue(RunValueName, RunCommand(exePath));
            }
            else if (user.GetValue(RunValueName) is not null)
            {
                user.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }
    }

    private static bool MachineAutostart()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(RunKey);
            return k?.GetValue(RunValueName) is string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private const uint WmClose = 0x0010;
    private static readonly IntPtr HwndMessage = new(-3);

    private static IntPtr Find() => FindWindowExW(HwndMessage, IntPtr.Zero, WindowClass, null);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
