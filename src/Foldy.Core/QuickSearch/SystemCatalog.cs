namespace Foldy.Core.QuickSearch;

/// <summary>Windows 설정 한 화면 (ms-settings: 주소). <see cref="Keywords"/>로도 찾는다.</summary>
public sealed record SettingsPage(string Title, string Uri, string Keywords);

/// <summary>시작 메뉴의 앱 (바로 가기 파일).</summary>
public sealed record StartApp(string Name, string ShortcutPath);

/// <summary>빠른 검색이 찾는 Windows 것들: 설정 화면 목록과 시작 메뉴 앱.</summary>
public static class SystemCatalog
{
    /// <summary>자주 찾는 Windows 설정 화면 (Microsoft 문서의 ms-settings URI).</summary>
    public static IReadOnlyList<SettingsPage> SettingsPages { get; } =
    [
        new(Foldy.Core.Localization.Loc.T("ws.display"), "ms-settings:display", Foldy.Core.Localization.Loc.T("ws.display.k")),
        new(Foldy.Core.Localization.Loc.T("ws.sound"), "ms-settings:sound", Foldy.Core.Localization.Loc.T("ws.sound.k")),
        new(Foldy.Core.Localization.Loc.T("ws.notifications"), "ms-settings:notifications", Foldy.Core.Localization.Loc.T("ws.notifications.k")),
        new(Foldy.Core.Localization.Loc.T("ws.power"), "ms-settings:powersleep", Foldy.Core.Localization.Loc.T("ws.power.k")),
        new(Foldy.Core.Localization.Loc.T("ws.storage"), "ms-settings:storagesense", Foldy.Core.Localization.Loc.T("ws.storage.k")),
        new(Foldy.Core.Localization.Loc.T("ws.multitasking"), "ms-settings:multitasking", Foldy.Core.Localization.Loc.T("ws.multitasking.k")),
        new(Foldy.Core.Localization.Loc.T("ws.about"), "ms-settings:about", Foldy.Core.Localization.Loc.T("ws.about.k")),
        new(Foldy.Core.Localization.Loc.T("ws.bluetooth"), "ms-settings:bluetooth", Foldy.Core.Localization.Loc.T("ws.bluetooth.k")),
        new(Foldy.Core.Localization.Loc.T("ws.printers"), "ms-settings:printers", Foldy.Core.Localization.Loc.T("ws.printers.k")),
        new(Foldy.Core.Localization.Loc.T("ws.mouse"), "ms-settings:mousetouchpad", Foldy.Core.Localization.Loc.T("ws.mouse.k")),
        new("Wi-Fi", "ms-settings:network-wifi", Foldy.Core.Localization.Loc.T("ws.wifi.k")),
        new(Foldy.Core.Localization.Loc.T("ws.network"), "ms-settings:network", Foldy.Core.Localization.Loc.T("ws.network.k")),
        new(Foldy.Core.Localization.Loc.T("ws.proxy"), "ms-settings:network-proxy", Foldy.Core.Localization.Loc.T("ws.proxy.k")),
        new("VPN", "ms-settings:network-vpn", "vpn"),
        new(Foldy.Core.Localization.Loc.T("ws.background"), "ms-settings:personalization-background", Foldy.Core.Localization.Loc.T("ws.background.k")),
        new(Foldy.Core.Localization.Loc.T("ws.colors"), "ms-settings:colors", Foldy.Core.Localization.Loc.T("ws.colors.k")),
        new(Foldy.Core.Localization.Loc.T("ws.taskbar"), "ms-settings:taskbar", Foldy.Core.Localization.Loc.T("ws.taskbar.k")),
        new(Foldy.Core.Localization.Loc.T("ws.fonts"), "ms-settings:fonts", Foldy.Core.Localization.Loc.T("ws.fonts.k")),
        new(Foldy.Core.Localization.Loc.T("ws.apps"), "ms-settings:appsfeatures", Foldy.Core.Localization.Loc.T("ws.apps.k")),
        new(Foldy.Core.Localization.Loc.T("ws.defaultApps"), "ms-settings:defaultapps", Foldy.Core.Localization.Loc.T("ws.defaultApps.k")),
        new(Foldy.Core.Localization.Loc.T("ws.startup"), "ms-settings:startupapps", Foldy.Core.Localization.Loc.T("ws.startup.k")),
        new(Foldy.Core.Localization.Loc.T("ws.account"), "ms-settings:yourinfo", Foldy.Core.Localization.Loc.T("ws.account.k")),
        new(Foldy.Core.Localization.Loc.T("ws.signin"), "ms-settings:signinoptions", Foldy.Core.Localization.Loc.T("ws.signin.k")),
        new(Foldy.Core.Localization.Loc.T("ws.time"), "ms-settings:dateandtime", Foldy.Core.Localization.Loc.T("ws.time.k")),
        new(Foldy.Core.Localization.Loc.T("ws.language"), "ms-settings:regionlanguage", Foldy.Core.Localization.Loc.T("ws.language.k")),
        new(Foldy.Core.Localization.Loc.T("ws.typing"), "ms-settings:typing", Foldy.Core.Localization.Loc.T("ws.typing.k")),
        new(Foldy.Core.Localization.Loc.T("ws.textSize"), "ms-settings:easeofaccess-display", Foldy.Core.Localization.Loc.T("ws.textSize.k")),
        new(Foldy.Core.Localization.Loc.T("ws.magnifier"), "ms-settings:easeofaccess-magnifier", Foldy.Core.Localization.Loc.T("ws.magnifier.k")),
        new(Foldy.Core.Localization.Loc.T("ws.pointer"), "ms-settings:easeofaccess-mousepointer", Foldy.Core.Localization.Loc.T("ws.pointer.k")),
        new(Foldy.Core.Localization.Loc.T("ws.update"), "ms-settings:windowsupdate", Foldy.Core.Localization.Loc.T("ws.update.k")),
        new(Foldy.Core.Localization.Loc.T("ws.security"), "ms-settings:windowsdefender", Foldy.Core.Localization.Loc.T("ws.security.k")),
        new(Foldy.Core.Localization.Loc.T("ws.privacy"), "ms-settings:privacy", Foldy.Core.Localization.Loc.T("ws.privacy.k")),
        new(Foldy.Core.Localization.Loc.T("ws.recovery"), "ms-settings:recovery", Foldy.Core.Localization.Loc.T("ws.recovery.k")),
        new(Foldy.Core.Localization.Loc.T("ws.backup"), "ms-settings:backup", Foldy.Core.Localization.Loc.T("ws.backup.k")),
    ];

    /// <summary>
    /// 시작 메뉴(모든 사용자 + 나)의 앱 바로 가기. 제거·도움말·웹 링크는 뺀다. 같은 이름은 하나만.
    /// </summary>
    public static IReadOnlyList<StartApp> LoadStartApps()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<StartApp>();
        foreach (var root in roots.Where(r => r.Length > 0 && Directory.Exists(r)))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (IsNoise(name) || !seen.Add(name))
                {
                    continue;
                }

                list.Add(new StartApp(name, f));
            }
        }

        return [.. list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public static bool IsNoise(string name)
    {
        string[] noise = ["uninstall", "제거", "readme", "help", "도움말", "website", "웹 사이트", "홈페이지", "license", "라이선스"]; // i18n:skip (바로 가기 이름에서 찾는 말: 번역하지 않는다)
        return noise.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));
    }
}
