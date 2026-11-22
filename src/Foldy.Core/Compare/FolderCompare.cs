using System.Text.Json;
using Foldy.Core.History;
using Foldy.Core.Organize;

namespace Foldy.Core.Compare;

public enum DiffKind : byte
{
    LeftOnly,
    RightOnly,
    LeftNewer,
    RightNewer,

    /// <summary>지난번 맞춘 뒤 양쪽 다 바뀜. 사용자가 파일마다 고른다.</summary>
    Conflict,

    /// <summary>크기는 같은데 시각만 다름 (내용 비교로 같다고 확인됨이 아닌 한 "다름"으로 보지 않는다).</summary>
    Same,
}

public sealed record DiffItem(string RelPath, DiffKind Kind, long LeftSize, long RightSize, DateTime LeftUtc, DateTime RightUtc);

public enum MergeDirection : byte { LeftToRight, RightToLeft, Both }

public sealed record CopyStep(string Src, string Dst, string RelPath);

/// <summary>맞추기 한 번의 되돌리기 기록: 새로 만든 파일, 덮어써서 이전 버전으로 보관한 파일.</summary>
public sealed record MergeRecord(string Dst, string? SavedVersion, bool Created);

/// <summary>
/// 두 폴더 비교·맞추기. 크기와 수정 시각으로 비교한다(2초 오차 허용). 한쪽에만 있는 파일을 지우지 않는다(거울 맞추기 없음).
/// Git·개발 프로젝트 폴더는 안을 비교하지 않는다(저장소는 통째로 다른 도구가 맡는다).
/// 덮어쓰는 파일은 이전 버전으로 보관하고, 맞춘 기록으로 한 번에 되돌린다.
/// </summary>
public static class FolderCompare
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<DiffItem> Compare(string left, string right, DateTime? lastSyncUtc = null, CancellationToken ct = default)
    {
        var l = Snapshot(left, ct);
        var r = Snapshot(right, ct);
        var list = new List<DiffItem>();
        foreach (var rel in l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var inL = l.TryGetValue(rel, out var a);
            var inR = r.TryGetValue(rel, out var b);
            if (inL && !inR)
            {
                list.Add(new DiffItem(rel, DiffKind.LeftOnly, a.Size, 0, a.Utc, default));
            }
            else if (!inL && inR)
            {
                list.Add(new DiffItem(rel, DiffKind.RightOnly, 0, b.Size, default, b.Utc));
            }
            else
            {
                var sameTime = (a.Utc - b.Utc).Duration() <= Tolerance;
                if (sameTime && a.Size == b.Size)
                {
                    continue; // 같음 (목록에 넣지 않는다)
                }

                if (lastSyncUtc is { } sync && a.Utc > sync + Tolerance && b.Utc > sync + Tolerance)
                {
                    list.Add(new DiffItem(rel, DiffKind.Conflict, a.Size, b.Size, a.Utc, b.Utc));
                }
                else
                {
                    list.Add(new DiffItem(rel, a.Utc >= b.Utc ? DiffKind.LeftNewer : DiffKind.RightNewer, a.Size, b.Size, a.Utc, b.Utc));
                }
            }
        }

        return list;
    }

    /// <summary>
    /// 고른 방향대로 복사할 목록. 한쪽에만 있는 파일은 반대쪽으로 복사만 하고 지우지 않는다.
    /// 더 오래된 쪽으로 최신 파일을 덮어쓰지 않는다. 충돌은 <paramref name="conflictChoice"/>에 고른 쪽만.
    /// </summary>
    public static IReadOnlyList<CopyStep> Plan(string left, string right, IEnumerable<DiffItem> diff, MergeDirection dir,
        IReadOnlyDictionary<string, bool>? conflictChoice = null)
    {
        var steps = new List<CopyStep>();
        CopyStep L2R(DiffItem d) => new(Path.Combine(left, d.RelPath), Path.Combine(right, d.RelPath), d.RelPath);
        CopyStep R2L(DiffItem d) => new(Path.Combine(right, d.RelPath), Path.Combine(left, d.RelPath), d.RelPath);
        foreach (var d in diff)
        {
            switch (d.Kind)
            {
                case DiffKind.LeftOnly or DiffKind.LeftNewer when dir is MergeDirection.LeftToRight or MergeDirection.Both:
                    steps.Add(L2R(d));
                    break;
                case DiffKind.RightOnly or DiffKind.RightNewer when dir is MergeDirection.RightToLeft or MergeDirection.Both:
                    steps.Add(R2L(d));
                    break;
                case DiffKind.Conflict when conflictChoice?.TryGetValue(d.RelPath, out var keepLeft) == true:
                    steps.Add(keepLeft ? L2R(d) : R2L(d));
                    break;
            }
        }

        return steps;
    }

    /// <summary>복사한다. 덮어쓸 파일은 먼저 이전 버전으로 보관한다. 열려 있는 파일은 건너뛴다.</summary>
    public static (IReadOnlyList<MergeRecord> Done, IReadOnlyList<(string Path, string Reason)> Skipped) Apply(IEnumerable<CopyStep> steps, string journalPath, CancellationToken ct = default)
    {
        var done = new List<MergeRecord>();
        var skipped = new List<(string, string)>();
        foreach (var s in steps)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s.Dst)!);
                string? saved = null;
                var existed = File.Exists(s.Dst);
                if (existed)
                {
                    saved = VersionHistory.Preserve(s.Dst)?.Saved;
                    if (saved is null)
                    {
                        // 이전 버전을 보관할 수 없는 드라이브(이동식·네트워크): 옆에 .foldy-bak으로 남긴다
                        var bak = s.Dst + ".foldy-bak";
                        File.Copy(s.Dst, bak, overwrite: true);
                        saved = bak;
                    }
                }

                File.Copy(s.Src, s.Dst, overwrite: true);
                File.SetLastWriteTimeUtc(s.Dst, File.GetLastWriteTimeUtc(s.Src));
                done.Add(new MergeRecord(s.Dst, saved, !existed));
            }
            catch (IOException)
            {
                skipped.Add((s.RelPath, Foldy.Core.Localization.Loc.T("mf.skipOpen")));
            }
            catch (UnauthorizedAccessException)
            {
                skipped.Add((s.RelPath, Foldy.Core.Localization.Loc.T("mf.skipPermission")));
            }
        }

        if (done.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            File.WriteAllText(journalPath, JsonSerializer.Serialize(done));
        }

        return (done, skipped);
    }

    /// <summary>지난번 맞추기를 되돌린다: 새로 만든 파일은 지우고, 덮어쓴 파일은 보관한 이전 버전으로. 되돌린 개수.</summary>
    public static int Undo(string journalPath)
    {
        if (!File.Exists(journalPath))
        {
            return 0;
        }

        List<MergeRecord> list;
        try
        {
            list = JsonSerializer.Deserialize<List<MergeRecord>>(File.ReadAllText(journalPath)) ?? [];
        }
        catch (JsonException)
        {
            list = [];
        }

        var n = 0;
        foreach (var m in Enumerable.Reverse(list))
        {
            try
            {
                if (m.Created)
                {
                    File.Delete(m.Dst);
                    n++;
                }
                else if (m.SavedVersion is { } v && File.Exists(v))
                {
                    File.Copy(v, m.Dst, overwrite: true);
                    if (v.EndsWith(".foldy-bak", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(v);
                    }

                    n++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        File.Delete(journalPath);
        return n;
    }

    private readonly record struct Info(long Size, DateTime Utc);

    private static Dictionary<string, Info> Snapshot(string root, CancellationToken ct)
    {
        var map = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            DirectoryInfo di = new(dir);
            try
            {
                foreach (var e in di.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    if (e is DirectoryInfo sub)
                    {
                        // 저장소·개발 프로젝트 안은 비교하지 않는다 (맞추면 빌드·기록이 깨질 수 있다). 숨김 .git 같은 것도 뺀다.
                        if ((sub.Attributes & FileAttributes.Hidden) != 0 || sub.Name is ".git" or ".foldy" || OrganizeScanner.IsProjectFolder(sub.FullName))
                        {
                            continue;
                        }

                        stack.Push(sub.FullName);
                    }
                    else if (e is FileInfo f && (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !f.Name.EndsWith(".foldy-bak", StringComparison.OrdinalIgnoreCase))
                    {
                        map[Path.GetRelativePath(root, f.FullName)] = new Info(f.Length, f.LastWriteTimeUtc);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return map;
    }
}
