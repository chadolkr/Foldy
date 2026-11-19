using Foldy.Git;

namespace Foldy.Presentation.Git;

/// <summary>GitResult 의 실패 유형을 사용자에게 보일 문장으로 바꾼다.</summary>
public static class GitErrorText
{
    private const int DetailLimit = 400;

    /// <summary>성공이거나 취소면 빈 문자열.</summary>
    public static string Describe(GitResult r)
    {
        if (r.Success || r.Failure is GitFailure.None or GitFailure.Cancelled) return "";
        var detail = Detail(r);
        return r.Failure switch
        {
            GitFailure.GitNotFound => "Git for Windows가 설치되어 있지 않습니다. git-scm.com에서 설치한 뒤 다시 시도하세요.",
            GitFailure.DubiousOwnership => "이 저장소의 소유자가 현재 사용자와 달라 Git이 접근을 막았습니다. 직접 만든 저장소라면 [이 저장소 신뢰하기]를 누르세요.",
            GitFailure.AuthenticationFailed => Join("원격 저장소 인증(로그인)에 실패했습니다. 아직 로그인하지 않았거나 이 저장소에 올릴 권한이 없을 수 있어요. 터미널에서 git push 를 한 번 실행해 로그인 창을 띄우거나, Git Credential Manager 와 SSH 키 설정을 확인해 보세요.", detail),
            GitFailure.MergeConflict => Join("병합 충돌이 발생했습니다. 충돌한 파일을 고친 뒤 Stage하고 커밋하세요.", detail),
            GitFailure.NothingToCommit => "커밋할 변경이 없습니다.",
            GitFailure.NotARepository => "Git 저장소가 아닙니다.",
            _ => detail.Length > 0 ? detail : $"Git 명령이 실패했습니다 (종료 코드 {r.ExitCode}).",
        };
    }

    private static string Join(string head, string detail) => detail.Length > 0 ? head + "\n" + detail : head;

    private static string Detail(GitResult r)
    {
        var text = string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error;
        text = text.Trim();
        return text.Length > DetailLimit ? text[..DetailLimit] + "…" : text;
    }
}
