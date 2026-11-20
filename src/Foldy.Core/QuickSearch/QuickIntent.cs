namespace Foldy.Core.QuickSearch;

/// <summary>
/// 빠른 검색에서 말하듯 쳐도 알아듣기 ("다운로드 정리해줘", "메모 쓸래", "휴지통 비워 줘").
/// 밖으로 보내지 않고, 끝말을 다듬고 기능마다 붙인 쉬운 낱말(<see cref="Keywords"/>)로 맞춘다.
/// 새 기능은 명령 목록에 등록하면 저절로 찾히고, 여기에는 사람들이 부를 법한 말만 더한다.
/// </summary>
public static class QuickIntent
{
    // 긴 것부터: 끝말 → 바꿀 말
    private static readonly (string End, string To)[] Endings =
    [
        ("하고 싶어요", ""), ("하고 싶어", ""), ("하고싶어", ""), ("해 주세요", ""), ("해주세요", ""), ("해 줄래", ""), ("해줄래", ""),
        ("해 줘", ""), ("해줘", ""), ("할래요", ""), ("할래", ""), ("하자", ""), ("해요", ""), ("하기", ""),
        ("쓰고 싶어", " 쓰기"), ("쓸래요", " 쓰기"), ("쓸래", " 쓰기"), ("써 줘", " 쓰기"), ("써줘", " 쓰기"),
        ("열어 줘", " 열기"), ("열어줘", " 열기"), ("열래", " 열기"), ("보여 줘", " 보기"), ("보여줘", " 보기"), ("볼래", " 보기"),
        ("비워 줘", " 비우기"), ("비워줘", " 비우기"), ("비울래", " 비우기"), ("지워 줘", " 지우기"), ("지워줘", " 지우기"),
        ("찾아 줘", " 찾기"), ("찾아줘", " 찾기"), ("켜 줘", " 켜기"), ("켜줘", " 켜기"), ("꺼 줘", " 끄기"), ("꺼줘", " 끄기"),
        ("주세요", ""), ("줘", ""),
    ];

    /// <summary>"다운로드 정리해줘" → "다운로드 정리", "메모 쓸래" → "메모 쓰기". 말끝이 없으면 그대로.</summary>
    public static string Normalize(string q)
    {
        var s = q.Trim().TrimEnd('.', '!', '?', '~', ' ');
        s = s.Replace(" 좀 ", " ", StringComparison.Ordinal);
        if (s.StartsWith("좀 ", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        foreach (var (end, to) in Endings)
        {
            if (s.Length > end.Length && s.EndsWith(end, StringComparison.Ordinal))
            {
                s = (s[..^end.Length].TrimEnd() + to).Trim();
                break;
            }
        }

        return s;
    }

    /// <summary>말하듯 쓴 문장인가 (끝말을 다듬었나).</summary>
    public static bool IsSentence(string q) => !string.Equals(Normalize(q), q.Trim(), StringComparison.Ordinal);

    /// <summary>기능(명령 id)마다 사람들이 부를 법한 쉬운 말.</summary>
    public static IReadOnlyDictionary<string, string> Keywords { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["memo.new"] = "메모 쓰기 메모 적기 메모장 기록 노트 글쓰기",
        ["secure.newMemo"] = "비밀 메모 비밀번호 적기 잠긴 메모 쓰기 암호 메모",
        ["secure.lock"] = "암호 걸기 잠그기 비밀번호 걸기 파일 잠그기",
        ["secure.vault"] = "금고 폴더 잠그기 숨기기 비밀 폴더",
        ["cleanup.downloads"] = "다운로드 정리 다운로드 치우기 받은 파일 정리",
        ["cleanup.desktop"] = "바탕화면 정리 바탕화면 치우기 바탕 화면 청소",
        ["cleanup.logs"] = "로그 정리 로그 지우기 용량 확보",
        ["analysis.open"] = "용량 보기 공간 확보 디스크 정리 무엇이 큰지 폴더 크기",
        ["app.emptyRecycleBin"] = "휴지통 비우기",
        ["compare.panes"] = "폴더 비교 두 폴더 맞추기 동기화 같게 만들기 최신 폴더",
        ["folder.organize"] = "폴더 정리 이 폴더 치우기 청소",
        ["folder.fromTemplate"] = "새 프로젝트 폴더 만들기 템플릿",
        ["folder.manage"] = "관리 방식 PARA 정리 방법",
        ["file.newFolder"] = "새 폴더 만들기 폴더 만들기",
        ["view.zoomIn"] = "글씨 크게 글자 크게 화면 크게 잘 안 보여",
        ["view.zoomOut"] = "글씨 작게 글자 작게",
        ["view.themeDark"] = "어둡게 다크 모드 눈부셔",
        ["view.themeLight"] = "밝게 라이트 모드",
        ["view.hidden"] = "숨김 파일 보기 숨긴 파일",
        ["view.images"] = "사진만 보기 이미지만 사진 보기",
        ["sftp.connect"] = "서버 연결 sftp ssh 원격",
        ["app.checkUpdate"] = "업데이트 새 버전",
        ["app.help"] = "도움말 사용법 설명서 모르겠어",
        ["app.tour"] = "둘러보기 처음 안내 사용법 알려 줘",
        ["app.reportBug"] = "버그 신고 오류 알리기 고장",
        ["tag.done"] = "작업 완료 표시 다 했어",
        ["terminal.open"] = "터미널 명령창 cmd 파워셸",
    };

    /// <summary>기능 하나의 점수: 이름 또는 쉬운 말에 맞으면. 말하듯 쓴 문장은 다듬은 뒤 맞춘다.</summary>
    public static int Score(string id, string title, string query)
    {
        var n = Normalize(query);
        var best = Math.Max(QuickSearchMatch.Score(title, query), QuickSearchMatch.Score(title, n));
        if (Keywords.TryGetValue(id, out var k))
        {
            var ks = Math.Max(QuickSearchMatch.Score(k, n), QuickSearchMatch.Score(k, query));
            if (ks > 0)
            {
                best = Math.Max(best, Math.Max(ks, 50)); // 쉬운 말에 맞으면 이름 중간에 맞은 것보다 위
            }
        }

        return best;
    }
}
