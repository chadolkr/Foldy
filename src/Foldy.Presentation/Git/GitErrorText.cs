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
            GitFailure.GitNotFound => Foldy.Core.Localization.Loc.T("git.err.noGit"),
            GitFailure.DubiousOwnership => Foldy.Core.Localization.Loc.T("git.err.owner"),
            GitFailure.AuthenticationFailed => Join(Foldy.Core.Localization.Loc.T("git.err.auth"), detail),
            GitFailure.MergeConflict => Join(Foldy.Core.Localization.Loc.T("git.err.conflict"), detail),
            GitFailure.NothingToCommit => Foldy.Core.Localization.Loc.T("git.err.nothing"),
            GitFailure.NotARepository => Foldy.Core.Localization.Loc.T("git.err.notRepo"),
            _ => detail.Length > 0 ? detail : Foldy.Core.Localization.Loc.F("git.err.exit", r.ExitCode),
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
