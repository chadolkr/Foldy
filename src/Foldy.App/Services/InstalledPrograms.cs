using Microsoft.Win32;

namespace Foldy.App.Services;

/// <summary>프로그램 추가/제거에 보이는 설치된 프로그램 이름 (HKLM 64/32비트 + HKCU).</summary>
public static class InstalledPrograms
{
    public static List<string> Names()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, names);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, names);
        Read(RegistryHive.CurrentUser, RegistryView.Default, names);
        return [.. names];
    }

    private static void Read(RegistryHive hive, RegistryView view, HashSet<string> names)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key is null)
            {
                return;
            }

            foreach (var sub in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is string n && !string.IsNullOrWhiteSpace(n) && k.GetValue("SystemComponent") is not 1)
                {
                    names.Add(n.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // 읽을 수 있는 것만
        }
    }
}
