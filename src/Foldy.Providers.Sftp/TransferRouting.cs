using Foldy.Presentation.Navigation;

namespace Foldy.Providers.Sftp;

/// <summary>복사·이동이 어느 쪽 구현으로 가야 하는지 (경로만 보고 정한다).</summary>
public enum TransferRoute
{
    /// <summary>로컬끼리: 기존 서비스(Rust 병렬 엔진 / 셸)가 한다.</summary>
    Local,

    /// <summary>원본이 모두 로컬이고 대상이 SFTP: 업로드.</summary>
    Upload,

    /// <summary>원본이 모두 SFTP이고 대상이 로컬: 다운로드.</summary>
    Download,

    /// <summary>SFTP 대상 + SFTP 원본(같은 연결이면 서버 쪽 이름 바꾸기).</summary>
    Remote,

    /// <summary>원본에 로컬과 SFTP가 섞여 있다. 하나씩 처리한다.</summary>
    Mixed,
}

public static class TransferRouting
{
    public static TransferRoute Classify(IReadOnlyList<string> sources, string destinationFolder)
    {
        var destRemote = PathUtil.IsSftp(destinationFolder);
        var remote = sources.Count(PathUtil.IsSftp);
        if (remote == 0)
        {
            return destRemote ? TransferRoute.Upload : TransferRoute.Local;
        }

        if (remote < sources.Count)
        {
            return TransferRoute.Mixed;
        }

        return destRemote ? TransferRoute.Remote : TransferRoute.Download;
    }

    public static bool TouchesSftp(IEnumerable<string> paths) => paths.Any(PathUtil.IsSftp);

    /// <summary>두 SFTP 경로가 같은 연결(user@host:port)인가.</summary>
    public static bool SameConnection(string a, string b) =>
        SftpLocation.TryParse(a, out var x) && SftpLocation.TryParse(b, out var y) &&
        string.Equals(x.ConnectionKey, y.ConnectionKey, StringComparison.Ordinal);
}

/// <summary>"이름 (2).확장자" 규칙. 폴더는 확장자를 나누지 않는다.</summary>
public static class ConflictNaming
{
    /// <summary>n번째 후보 (n은 2부터): a.txt → a (2).txt, 폴더 dir → dir (2), .bashrc → .bashrc (2).</summary>
    public static string Candidate(string name, int n, bool isFolder)
    {
        var dot = isFolder ? -1 : name.LastIndexOf('.');
        return dot <= 0 ? $"{name} ({n})" : $"{name[..dot]} ({n}){name[dot..]}";
    }

    /// <summary>비어 있는 첫 후보를 찾는다. exists는 후보 이름이 이미 있는지 묻는다.</summary>
    public static async Task<string> UniqueAsync(string name, bool isFolder, Func<string, Task<bool>> exists)
    {
        for (var n = 2; ; n++)
        {
            var c = Candidate(name, n, isFolder);
            if (!await exists(c).ConfigureAwait(false))
            {
                return c;
            }
        }
    }
}
