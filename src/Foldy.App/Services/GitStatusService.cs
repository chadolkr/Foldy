using Foldy.Git;
using Foldy.Presentation.Tabs;

namespace Foldy.App.Services;

/// <summary>폴더 탭용 Git 상태: 저장소 찾기와 status는 백그라운드에서, git.exe가 없으면 아무것도 표시하지 않는다.</summary>
public sealed class GitStatusService(GitService git) : IGitStatusProvider
{
    public async Task<GitFolderInfo?> GetAsync(string folder, CancellationToken ct)
    {
        if (!git.IsGitAvailable)
        {
            return null;
        }

        var repo = await Task.Run(() => git.FindRepo(folder), ct);
        if (repo is null)
        {
            return null;
        }

        var st = await git.GetStatusAsync(repo.Root, ct: ct);
        if (st.Value is not { } status)
        {
            return st.Result.Failure == GitFailure.DubiousOwnership
                ? new GitFolderInfo(repo.Root, Foldy.Core.Localization.Loc.T("git.untrusted"), _ => Core.FileSystem.GitFileStatus.None, 0)
                : null;
        }

        var badges = new GitBadgeMap(repo.Root, status);
        var branch = status.IsDetached ? $"({status.HeadOid?[..Math.Min(7, status.HeadOid.Length)]})" : status.BranchName ?? "?";
        if (status.Ahead > 0)
        {
            branch += $" ↑{status.Ahead}";
        }

        if (status.Behind > 0)
        {
            branch += $" ↓{status.Behind}";
        }

        var changes = status.Files.Count(f => f.ToBadge() is not (Core.FileSystem.GitFileStatus.Ignored or Core.FileSystem.GitFileStatus.None));
        return new GitFolderInfo(repo.Root, branch, badges.Get, changes);
    }
}
