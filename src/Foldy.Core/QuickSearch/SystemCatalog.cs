namespace Foldy.Core.QuickSearch;

/// <summary>빠른 검색에 나오는 Windows 항목의 종류: 설정 앱 화면, 예전 제어판, 관리 도구.</summary>
public enum SystemItemKind
{
    Settings,
    ControlPanel,
    Tool,
}

/// <summary>
/// Windows 설정 한 화면(ms-settings: 주소) 또는 제어판·관리 도구 하나. <see cref="Keywords"/>(쉬운 말)로도 찾는다.
/// 제어판·도구의 <see cref="Uri"/>는 System32 안의 파일 이름(appwiz.cpl, devmgmt.msc 등)이고 <see cref="Arguments"/>가 붙을 수 있다.
/// </summary>
public sealed record SettingsPage(string Title, string Uri, string Keywords, string Arguments = "", SystemItemKind Kind = SystemItemKind.Settings)
{
    /// <summary>
    /// 실행할 것: ms-settings는 주소 그대로, 나머지는 System32의 전체 경로 (다른 폴더의 같은 이름 파일이 대신 실행되지 않게).
    /// </summary>
    public (string File, string Arguments) LaunchTarget(string? systemDirectory = null) =>
        Uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
            ? (Uri, "")
            : (Path.Combine(systemDirectory ?? Environment.SystemDirectory, Uri), Arguments);
}

/// <summary>시작 메뉴의 앱 (바로 가기 파일).</summary>
public sealed record StartApp(string Name, string ShortcutPath);

/// <summary>빠른 검색이 찾는 Windows 것들: 설정 화면·제어판·관리 도구 목록과 시작 메뉴 앱.</summary>
public static class SystemCatalog
{
    private static SettingsPage S(string key, string uri) => new(Foldy.Core.Localization.Loc.T("ws." + key), uri, Foldy.Core.Localization.Loc.T("ws." + key + ".k"));

    private static SettingsPage C(string key, string file, string args = "") =>
        new(Foldy.Core.Localization.Loc.T("cp." + key), file, Foldy.Core.Localization.Loc.T("cp." + key + ".k"), args, SystemItemKind.ControlPanel);

    private static SettingsPage T(string key, string file, string args = "") =>
        new(Foldy.Core.Localization.Loc.T("cp." + key), file, Foldy.Core.Localization.Loc.T("cp." + key + ".k"), args, SystemItemKind.Tool);

    /// <summary>
    /// 자주 찾는 Windows 설정 화면(Microsoft 문서의 ms-settings URI)과, 아직 사람들이 제어판 이름으로 찾는 것들
    /// (2026-11-22 사용자: "제어판 프로그램 추가/삭제 등 검색이 안 보인다").
    /// </summary>
    public static IReadOnlyList<SettingsPage> SettingsPages { get; } =
    [
        S("display", "ms-settings:display"),
        S("sound", "ms-settings:sound"),
        S("notifications", "ms-settings:notifications"),
        S("power", "ms-settings:powersleep"),
        S("storage", "ms-settings:storagesense"),
        S("multitasking", "ms-settings:multitasking"),
        S("about", "ms-settings:about"),
        S("bluetooth", "ms-settings:bluetooth"),
        S("printers", "ms-settings:printers"),
        S("mouse", "ms-settings:mousetouchpad"),
        new("Wi-Fi", "ms-settings:network-wifi", Foldy.Core.Localization.Loc.T("ws.wifi.k")),
        S("network", "ms-settings:network"),
        S("proxy", "ms-settings:network-proxy"),
        new("VPN", "ms-settings:network-vpn", "vpn"),
        S("background", "ms-settings:personalization-background"),
        S("colors", "ms-settings:colors"),
        S("taskbar", "ms-settings:taskbar"),
        S("fonts", "ms-settings:fonts"),
        S("apps", "ms-settings:appsfeatures"),
        S("defaultApps", "ms-settings:defaultapps"),
        S("startup", "ms-settings:startupapps"),
        S("account", "ms-settings:yourinfo"),
        S("signin", "ms-settings:signinoptions"),
        S("time", "ms-settings:dateandtime"),
        S("language", "ms-settings:regionlanguage"),
        S("typing", "ms-settings:typing"),
        S("textSize", "ms-settings:easeofaccess-display"),
        S("magnifier", "ms-settings:easeofaccess-magnifier"),
        S("pointer", "ms-settings:easeofaccess-mousepointer"),
        S("update", "ms-settings:windowsupdate"),
        S("security", "ms-settings:windowsdefender"),
        S("privacy", "ms-settings:privacy"),
        S("recovery", "ms-settings:recovery"),
        S("backup", "ms-settings:backup"),
        S("clipboard", "ms-settings:clipboard"),
        S("nightlight", "ms-settings:nightlight"),
        S("themes", "ms-settings:themes"),
        S("lockscreen", "ms-settings:lockscreen"),
        S("start", "ms-settings:personalization-start"),
        S("batterysaver", "ms-settings:batterysaver"),
        S("hotspot", "ms-settings:network-mobilehotspot"),
        S("microphone", "ms-settings:privacy-microphone"),
        S("camera", "ms-settings:privacy-webcam"),
        S("location", "ms-settings:privacy-location"),
        S("narrator", "ms-settings:easeofaccess-narrator"),

        // 제어판 (예전 이름으로 찾는 사람이 많다)
        C("programs", "appwiz.cpl"),
        C("control", "control.exe"),
        C("features", "optionalfeatures.exe"),
        C("network", "ncpa.cpl"),
        C("users", "control.exe", "/name Microsoft.UserAccounts"),
        C("credentials", "control.exe", "/name Microsoft.CredentialManager"),
        C("printers", "control.exe", "printers"),
        C("sound", "mmsys.cpl"),
        C("mouse", "main.cpl"),
        C("power", "powercfg.cpl"),
        C("internet", "inetcpl.cpl"),
        C("firewall", "firewall.cpl"),
        C("region", "intl.cpl"),
        C("system", "SystemPropertiesAdvanced.exe"),
        C("envvars", "rundll32.exe", "sysdm.cpl,EditEnvironmentVariables"),
        C("protection", "SystemPropertiesProtection.exe"),

        // 관리 도구
        T("taskmgr", "taskmgr.exe"),
        T("devices", "devmgmt.msc"),
        T("disk", "diskmgmt.msc"),
        T("cleanup", "cleanmgr.exe"),
        T("services", "services.msc"),
        T("events", "eventvwr.msc"),
        T("tasks", "taskschd.msc"),
        T("compmgmt", "compmgmt.msc"),
        T("sysinfo", "msinfo32.exe"),
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
