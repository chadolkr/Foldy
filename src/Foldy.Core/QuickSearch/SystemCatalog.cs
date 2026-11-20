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
        new("디스플레이", "ms-settings:display", "화면 해상도 배율 밝기 모니터 display"),
        new("소리", "ms-settings:sound", "스피커 마이크 볼륨 음량 sound"),
        new("알림", "ms-settings:notifications", "알림 방해 금지 notifications"),
        new("전원 및 배터리", "ms-settings:powersleep", "절전 배터리 전원 잠자기 power battery"),
        new("저장소", "ms-settings:storagesense", "디스크 공간 용량 저장 공간 센스 storage"),
        new("멀티태스킹", "ms-settings:multitasking", "창 스냅 multitasking"),
        new("정보 (PC 이름·사양)", "ms-settings:about", "컴퓨터 이름 사양 버전 about"),
        new("블루투스 및 장치", "ms-settings:bluetooth", "블루투스 장치 마우스 키보드 bluetooth"),
        new("프린터 및 스캐너", "ms-settings:printers", "프린터 인쇄 스캐너 printer"),
        new("마우스", "ms-settings:mousetouchpad", "마우스 포인터 휠 mouse"),
        new("Wi-Fi", "ms-settings:network-wifi", "와이파이 무선 인터넷 wifi"),
        new("네트워크 및 인터넷", "ms-settings:network", "인터넷 랜 이더넷 network"),
        new("프록시", "ms-settings:network-proxy", "프록시 proxy"),
        new("VPN", "ms-settings:network-vpn", "vpn"),
        new("배경", "ms-settings:personalization-background", "바탕화면 배경 사진 wallpaper"),
        new("색 (밝게·어둡게)", "ms-settings:colors", "다크 모드 테마 색 colors dark"),
        new("작업 표시줄", "ms-settings:taskbar", "작업표시줄 taskbar"),
        new("글꼴", "ms-settings:fonts", "폰트 글꼴 fonts"),
        new("설치된 앱", "ms-settings:appsfeatures", "프로그램 제거 삭제 앱 apps uninstall"),
        new("기본 앱", "ms-settings:defaultapps", "연결 프로그램 기본 default apps"),
        new("시작 프로그램", "ms-settings:startupapps", "시작 앱 부팅 startup"),
        new("계정 정보", "ms-settings:yourinfo", "계정 사용자 account"),
        new("로그인 옵션", "ms-settings:signinoptions", "암호 PIN 지문 로그인 sign-in"),
        new("날짜 및 시간", "ms-settings:dateandtime", "시간 날짜 시계 time"),
        new("언어 및 지역", "ms-settings:regionlanguage", "언어 한국어 영어 지역 language"),
        new("입력 (키보드)", "ms-settings:typing", "키보드 입력기 한영 typing"),
        new("텍스트 크기", "ms-settings:easeofaccess-display", "글자 크기 크게 접근성 text size"),
        new("돋보기", "ms-settings:easeofaccess-magnifier", "확대 돋보기 magnifier"),
        new("마우스 포인터 크기", "ms-settings:easeofaccess-mousepointer", "커서 크게 pointer"),
        new("Windows 업데이트", "ms-settings:windowsupdate", "업데이트 update"),
        new("Windows 보안", "ms-settings:windowsdefender", "백신 바이러스 보안 defender security"),
        new("개인 정보", "ms-settings:privacy", "권한 위치 카메라 privacy"),
        new("복구", "ms-settings:recovery", "초기화 복구 recovery"),
        new("백업", "ms-settings:backup", "백업 backup"),
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
        string[] noise = ["uninstall", "제거", "readme", "help", "도움말", "website", "웹 사이트", "홈페이지", "license", "라이선스"];
        return noise.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));
    }
}
