using System.Globalization;

namespace Foldy.Git;

/// <summary>`git log -z --format=...` 파서와 포맷 상수.</summary>
public static class GitLogParser
{
    /// <summary>필드 구분은 0x1F, 레코드 구분은 -z 의 NUL. hash, parents, 이름, 이메일, 작성 UNIX 시각, 장식, 제목.</summary>
    public const string Format = "%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s";

    public static IReadOnlyList<CommitNode> Parse(string output)
    {
        var list = new List<CommitNode>();
        foreach (var rec in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var node = ParseRecord(rec.TrimStart('\n', '\r'));
            if (node is not null) list.Add(node);
        }
        return list;
    }

    internal static CommitNode? ParseRecord(string rec)
    {
        var f = rec.Split('\u001f');
        if (f.Length < 7 || f[0].Length < 7) return null;
        var parents = f[1].Length == 0 ? [] : f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var date = long.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs)
            ? DateTimeOffset.FromUnixTimeSeconds(secs)
            : default;
        // 제목에 0x1F 가 들어올 일은 없지만 방어적으로 나머지를 합친다
        var subject = f.Length == 7 ? f[6] : string.Join('\u001f', f.Skip(6));
        return new CommitNode(f[0], parents, f[2], f[3], date, subject, ParseRefs(f[5]));
    }

    /// <summary>%D 장식 파싱: "HEAD -> main, origin/main, tag: v1.0".</summary>
    internal static IReadOnlyList<GitRef> ParseRefs(string decoration)
    {
        if (string.IsNullOrWhiteSpace(decoration)) return [];
        var refs = new List<GitRef>();
        foreach (var raw in decoration.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var d = raw.Trim();
            if (d.StartsWith("HEAD -> ", StringComparison.Ordinal))
                refs.Add(new GitRef(d["HEAD -> ".Length..], GitRefKind.LocalBranch, IsCurrentHead: true));
            else if (d == "HEAD")
                refs.Add(new GitRef("HEAD", GitRefKind.Head, IsCurrentHead: true));
            else if (d.StartsWith("tag: ", StringComparison.Ordinal))
                refs.Add(new GitRef(d["tag: ".Length..], GitRefKind.Tag));
            else if (d.Contains('/') && !d.EndsWith("/HEAD", StringComparison.Ordinal))
                refs.Add(new GitRef(d, GitRefKind.RemoteBranch));
            else if (!d.EndsWith("/HEAD", StringComparison.Ordinal))
                refs.Add(new GitRef(d, GitRefKind.LocalBranch));
        }
        return refs;
    }

    /// <summary>log 인수 구성. Skip/Count 페이징, 토폴로지 순서.</summary>
    internal static List<string> BuildArgs(LogFilter f)
    {
        var args = new List<string> { "log", "--topo-order", "-z", "--decorate=short", "--format=" + Format };
        if (f.Skip > 0) args.Add("--skip=" + f.Skip.ToString(CultureInfo.InvariantCulture));
        if (f.Count > 0) args.Add("-n" + f.Count.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(f.Author)) { args.Add("--author=" + f.Author); }
        if (!string.IsNullOrEmpty(f.MessageContains)) { args.Add("--grep=" + f.MessageContains); }
        if (!string.IsNullOrEmpty(f.Author) || !string.IsNullOrEmpty(f.MessageContains)) args.Add("-i");
        if (!string.IsNullOrEmpty(f.Revision)) args.Add(f.Revision);
        else if (f.AllRefs) args.Add("--all");
        if (!string.IsNullOrEmpty(f.Path)) { args.Add("--"); args.Add(f.Path); }
        return args;
    }
}
