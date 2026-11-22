using Foldy.Git;

namespace Foldy.Presentation.Git;

/// <summary>커밋이 바꾼 파일 한 줄 (git show --name-status).</summary>
/// <param name="Status">A, M, D, R, C, T 중 하나 (이름 변경·복사의 유사도 숫자는 뗀다).</param>
/// <param name="OrigPath">이름 변경·복사의 원본 경로.</param>
public sealed record GitCommitFile(string Status, string Path, string? OrigPath = null)
{
    /// <summary>목록 배지 글자 (Status 와 같다).</summary>
    public string Letter => Status;

    /// <summary>목록 배지 글자 (한국어).</summary>
    public string Label => Status switch
    {
        "A" => Foldy.Core.Localization.Loc.T("pane.git.added"),
        "M" => Foldy.Core.Localization.Loc.T("pane.col.modified"),
        "D" => Foldy.Core.Localization.Loc.T("git.kind.deleted"),
        "R" => Foldy.Core.Localization.Loc.T("git.kind.renamed"),
        "C" => Foldy.Core.Localization.Loc.T("ops.copy"),
        "T" => Foldy.Core.Localization.Loc.T("git.kind.typeChanged"),
        _ => Status,
    };

    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];

    public string Folder => Path.LastIndexOf('/') is var i and > 0 ? Path[..i] : "";
}

/// <summary>선택한 커밋의 변경 파일 목록을 돌려주는 원본. 테스트에서 가짜로 바꿀 수 있다.</summary>
public interface ICommitFileSource
{
    Task<GitResult<IReadOnlyList<GitCommitFile>>> GetChangedFilesAsync(string repo, string hash, CancellationToken ct = default);
}

/// <summary>
/// git show --name-status 로 변경 파일을 읽는다. IGitService 에 해당 메서드가 없어
/// Foldy.Git 을 건드리지 않고 GitCli 를 직접 쓴다 (읽기 전용이라 --no-optional-locks).
/// </summary>
public sealed class GitCliCommitFiles(GitCli cli) : ICommitFileSource
{
    public async Task<GitResult<IReadOnlyList<GitCommitFile>>> GetChangedFilesAsync(string repo, string hash, CancellationToken ct = default)
    {
        // 병합 커밋은 첫 부모 기준 차이를 보여 준다 (-m --first-parent). 첫 커밋은 --root 로 전부 추가로 나온다.
        string[] args = ["show", "--name-status", "-z", "--format=", "-M", "-m", "--first-parent", "--root", hash];
        var r = await cli.RunAsync(repo, args, new GitRunOptions { NoOptionalLocks = true }, null, ct);
        return new GitResult<IReadOnlyList<GitCommitFile>>(r, r.Success ? ParseNameStatus(r.Output) : null);
    }

    /// <summary>"M\0path\0R100\0old\0new\0" 형태 (-z) 를 파싱한다.</summary>
    public static IReadOnlyList<GitCommitFile> ParseNameStatus(string output)
    {
        var list = new List<GitCommitFile>();
        var tok = output.Split('\0');
        for (var i = 0; i < tok.Length; i++)
        {
            var s = tok[i].Trim('\n', '\r');
            if (s.Length == 0) continue;
            var code = s[0];
            if (code is 'R' or 'C')
            {
                if (i + 2 >= tok.Length) break;
                var from = tok[i + 1];
                var to = tok[i + 2];
                list.Add(new GitCommitFile(code.ToString(), to, from));
                i += 2;
            }
            else
            {
                if (i + 1 >= tok.Length) break;
                list.Add(new GitCommitFile(code.ToString(), tok[i + 1]));
                i += 1;
            }
        }
        return list;
    }
}

/// <summary>git 을 쓸 수 없을 때 쓰는 빈 원본.</summary>
internal sealed class NoCommitFiles : ICommitFileSource
{
    public Task<GitResult<IReadOnlyList<GitCommitFile>>> GetChangedFilesAsync(string repo, string hash, CancellationToken ct = default)
        => Task.FromResult(new GitResult<IReadOnlyList<GitCommitFile>>(GitResult.Ok(), []));
}
