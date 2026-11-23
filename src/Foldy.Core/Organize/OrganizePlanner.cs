using System.Globalization;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using Foldy.Core.Templates;

namespace Foldy.Core.Organize;

/// <summary>
/// 정리 계획 (설계서 5.11, 순수 함수): 파일마다 "어디로, 왜"를 계산한다.
/// 아무것도 지우지 않고, 맞는 규칙이 없으면 _분류 필요, 같은 파일은 _중복 후보로 모은다.
/// 이름 충돌은 "이름 (2).확장자"로 피한다.
/// </summary>
public static partial class OrganizePlanner
{
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}|\d{8})", RegexOptions.CultureInvariant)]
    private static partial Regex StartsWithDate();

    /// <summary>"보고서 (2).pdf", "보고서 - 복사본.pdf", "보고서(1).pdf" → "보고서.pdf".</summary>
    [GeneratedRegex(@"(\s*\(\d+\)|\s*-\s*복사본(\s*\(\d+\))?|\s*-\s*Copy(\s*\(\d+\))?|\s+copy(\s+\d+)?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CopySuffix();

    /// <param name="exists">디스크에 이미 그 경로가 있는지 (충돌 판단).</param>
    public static OrganizePlan Plan(
        OrganizeScan scan, OrganizeOptions options, Func<string, bool> exists,
        IMediaDateProvider? media = null, IContentHasher? hasher = null, OrganizePlanProgress? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(exists);
        var plan = new OrganizePlan { Root = scan.Root };
        plan.Skipped.AddRange(scan.Skipped);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputs = OutputFolders(options);
        var targets = new TargetCheck();

        // 1) 폴더: 규칙(ApplyToFolders)에 맞을 때만 통째로 옮긴다
        var movingFolders = new List<string>();
        foreach (var e in scan.Entries.Where(e => e.IsFolder))
        {
            if (IsDirectChild(scan.Root, e.FullPath) && outputs.Contains(e.Name))
            {
                continue; // 정리 결과 폴더 자체
            }

            var rule = options.Mode == OrganizeMode.Custom ? options.Rules.FirstOrDefault(r => r.ApplyToFolders && Matches(r, e)) : null;
            if (rule is null)
            {
                plan.Skipped.Add(new(e.FullPath, e.IsProjectFolder ? Foldy.Core.Localization.Loc.T("op.projectFolder") : Foldy.Core.Localization.Loc.T("op.folderRuleOnly")));
                continue;
            }

            if (Add(plan, scan.Root, e, rule.Target, Foldy.Core.Localization.Loc.F("op.customRule", RuleLabel(rule)), exists, claimed, options, targets))
            {
                movingFolders.Add(e.FullPath);
            }
        }

        // 2) 파일 (통째로 옮기는 폴더 안의 파일은 빼고: 위 폴더를 거슬러 올라가며 확인)
        var moving = new HashSet<string>(movingFolders, StringComparer.OrdinalIgnoreCase);
        var files = scan.Entries.Where(e => !e.IsFolder && (moving.Count == 0 || !InsideAny(e.FullPath, scan.Root, moving))).ToList();
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath));
        var duplicateGroups = options.DetectDuplicates ? FindDuplicates(files, hasher ?? Sha256ContentHasher.Instance, progress, ct) : [];
        var unclassified = Foldy.Core.Localization.FolderNames.Pick(scan.Root, OrganizeOptions.UnclassifiedFolderKo, OrganizeOptions.UnclassifiedFolder);
        var duplicatesFolder = Foldy.Core.Localization.FolderNames.Pick(scan.Root, OrganizeOptions.DuplicatesFolderKo, OrganizeOptions.DuplicatesFolder);
        var noRule = Foldy.Core.Localization.Loc.T("op.noRule");

        // 같은 파일 묶음: 맨 앞(가장 오래된 것)을 남기고 나머지는 _중복 후보로. 묶음마다 어느 것을 남길 때 어디로 갈지도 적어 둔다.
        var memberOf = new Dictionary<string, (DuplicateSet Set, DuplicateMember Member)>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in duplicateGroups)
        {
            var members = g.Select(f => new DuplicateMember { FullPath = f.FullPath, Size = f.Size, LastWriteUtc = f.LastWriteUtc }).ToList();
            var set = new DuplicateSet { Keeper = members[0], TargetFolder = NormalizeRel(duplicatesFolder) };
            set.Members.AddRange(members);
            plan.DuplicateSets.Add(set);
            foreach (var m in members)
            {
                memberOf[m.FullPath] = (set, m);
            }
        }

        progress?.Start(OrganizePlanStage.Planning, files.Count);
        foreach (var e in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Step();
            var (target, reason) = Classify(e, options, media, scan.Root);
            target ??= unclassified;
            reason ??= noRule;
            var isMember = memberOf.TryGetValue(e.FullPath, out var dup);
            if (isMember)
            {
                dup.Member.KeepTarget = NormalizeRel(target);
                dup.Member.KeepReason = reason;
                dup.Member.NewName = NewName(e, options);
                if (dup.Set.Keeper != dup.Member)
                {
                    if (Add(plan, scan.Root, e, duplicatesFolder, DuplicateReason(scan.Root, dup.Set), exists, claimed, options, targets))
                    {
                        plan.Moves[^1].DuplicateSet = dup.Set;
                    }

                    continue;
                }
            }

            if (Add(plan, scan.Root, e, target, reason, exists, claimed, options, targets) && isMember)
            {
                plan.Moves[^1].DuplicateSet = dup.Set;
            }
        }

        return plan;
    }

    private static bool InsideAny(string path, string root, HashSet<string> folders)
    {
        for (var dir = Path.GetDirectoryName(path); dir is not null && dir.Length >= root.Length; dir = Path.GetDirectoryName(dir))
        {
            if (folders.Contains(dir))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>대상 폴더 이름 검사 결과를 한 번만 계산해 둔다 (파일 80만 개가 같은 몇 개 폴더로 간다).</summary>
    private sealed class TargetCheck
    {
        private readonly Dictionary<string, string?> _issue = new(StringComparer.Ordinal);

        /// <summary>같은 이름이 수천 개일 때 "(2)"부터 다시 세지 않도록, 이름마다 다음에 볼 번호.</summary>
        public Dictionary<string, int> NextIndex { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? IssueOf(string rel)
        {
            if (rel.Length == 0)
            {
                return null;
            }

            if (!_issue.TryGetValue(rel, out var issue))
            {
                var issues = new List<TemplateIssue>();
                TemplateValidator.ValidateRelativePath(rel.Replace('\\', '/'), raw: false, rel, issues);
                issue = issues.Count > 0 ? issues[0].Message : null;
                _issue[rel] = issue;
            }

            return issue;
        }
    }

    /// <summary>미리보기에서 대상 폴더를 바꿨을 때 다시 계산 (충돌 이름 포함).</summary>
    public static void Retarget(OrganizePlan plan, PlannedMove move, string targetFolder, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(move);
        ArgumentNullException.ThrowIfNull(exists);
        var claimed = new HashSet<string>(plan.Moves.Where(m => m != move && m.Included).Select(m => m.Destination), StringComparer.OrdinalIgnoreCase);
        var rel = NormalizeRel(targetFolder);
        var dir = rel.Length == 0 ? plan.Root : Path.Combine(plan.Root, rel);
        move.TargetFolder = rel;
        move.Destination = Unique(Path.Combine(dir, Path.GetFileName(move.Source)), exists, claimed, move.Source);
        move.Reason = Foldy.Core.Localization.Loc.T("op.manual");
    }

    private static bool Add(OrganizePlan plan, string root, OrganizeEntry e, string targetRel, string reason,
        Func<string, bool> exists, HashSet<string> claimed, OrganizeOptions options, TargetCheck targets)
    {
        var rel = NormalizeRel(targetRel);
        if (targets.IssueOf(rel) is { } issue)
        {
            plan.Skipped.Add(new(e.FullPath, Foldy.Core.Localization.Loc.F("op.badTarget", issue)));
            return false;
        }

        var dir = rel.Length == 0 ? root : Path.Combine(root, rel);
        var name = NewName(e, options);

        var currentDir = Path.GetDirectoryName(e.FullPath)!;
        if (string.Equals(Path.TrimEndingDirectorySeparator(currentDir), Path.TrimEndingDirectorySeparator(dir), StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, e.Name, StringComparison.Ordinal))
        {
            return false; // 이미 제자리
        }

        // 폴더를 자기 안으로 옮기는 경우 방지
        if (e.IsFolder && (dir + Path.DirectorySeparatorChar).StartsWith(e.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            plan.Skipped.Add(new(e.FullPath, Foldy.Core.Localization.Loc.T("op.intoItself")));
            return false;
        }

        var dest = Unique(Path.Combine(dir, name), exists, claimed, e.FullPath, targets.NextIndex);
        claimed.Add(dest);
        plan.Moves.Add(new PlannedMove
        {
            Source = e.FullPath,
            Destination = dest,
            TargetFolder = rel,
            IsFolder = e.IsFolder,
            Reason = reason,
            Size = e.Size,
        });
        return true;
    }

    /// <summary>옮긴 뒤 이름: 이름 규칙 맞추기를 켜면 날짜로 시작하지 않는 파일 앞에 수정 날짜를 붙인다.</summary>
    private static string NewName(OrganizeEntry e, OrganizeOptions options) =>
        options.PrefixDate && !e.IsFolder && !StartsWithDate().IsMatch(e.Name)
            ? e.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_" + e.Name
            : e.Name;

    /// <summary>"중복 후보 · 남길 파일: 폴더\이름" (어느 파일과 같은지 정리할 폴더 기준 경로로).</summary>
    public static string DuplicateReason(string root, DuplicateSet set)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(set);
        return Foldy.Core.Localization.Loc.F("op.duplicate", Path.GetRelativePath(root, set.Keeper.FullPath));
    }

    /// <summary>
    /// 같은 파일 묶음에서 남길 파일을 바꾼다 (미리보기에서 사용자가 고름). 새로 남길 파일은 원래 규칙대로 옮기거나 제자리에 두고,
    /// 지금까지 남기던 파일을 포함한 나머지는 _중복 후보로 옮긴다. 아무것도 지우지 않는다.
    /// </summary>
    public static KeeperChange ChooseKeeper(OrganizePlan plan, DuplicateSet set, DuplicateMember keeper, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(keeper);
        ArgumentNullException.ThrowIfNull(exists);
        if (set.Keeper == keeper || !set.Members.Contains(keeper))
        {
            return new([], [], []);
        }

        var previous = set.Keeper;
        set.Keeper = keeper;
        var paths = new HashSet<string>(set.Members.Select(m => m.FullPath), StringComparer.OrdinalIgnoreCase);
        var bySource = new Dictionary<string, PlannedMove>(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in plan.Moves)
        {
            if (paths.Contains(m.Source))
            {
                bySource[m.Source] = m;
            }
            else if (m.Included)
            {
                claimed.Add(m.Destination);
            }
        }

        var reason = DuplicateReason(plan.Root, set);
        var added = new List<PlannedMove>();
        var removed = new List<PlannedMove>();
        var changed = new List<PlannedMove>();

        // 남길 파일 먼저 (원래 규칙대로 갈 자리를 먼저 잡는다), 나머지는 _중복 후보로
        foreach (var m in set.Members.OrderBy(x => x == keeper ? 0 : 1))
        {
            bySource.TryGetValue(m.FullPath, out var move);
            var keep = m == keeper;
            var rel = keep ? m.KeepTarget : set.TargetFolder;
            var dir = rel.Length == 0 ? plan.Root : Path.Combine(plan.Root, rel);
            var name = m.NewName.Length > 0 ? m.NewName : Path.GetFileName(m.FullPath);
            if (keep && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(m.FullPath)!), Path.TrimEndingDirectorySeparator(dir), StringComparison.OrdinalIgnoreCase)
                && string.Equals(name, Path.GetFileName(m.FullPath), StringComparison.Ordinal))
            {
                // 이미 제자리: 옮기지 않는다
                if (move is not null)
                {
                    plan.Moves.Remove(move);
                    removed.Add(move);
                }

                continue;
            }

            if (!keep && move is null && m != previous)
            {
                continue; // 처음부터 옮기지 못한 파일 (건너뛴 이유가 있다). 제자리에 있던 원래 남길 파일만 새로 옮긴다
            }

            var dest = Unique(Path.Combine(dir, name), exists, claimed, m.FullPath);
            claimed.Add(dest);
            if (move is null)
            {
                move = new PlannedMove
                {
                    Source = m.FullPath, Destination = dest, TargetFolder = rel, Reason = keep ? m.KeepReason : reason, Size = m.Size, DuplicateSet = set,
                };
                plan.Moves.Add(move);
                added.Add(move);
            }
            else
            {
                move.Destination = dest;
                move.TargetFolder = rel;
                move.Reason = keep ? m.KeepReason : reason;
                move.Included = true;
                changed.Add(move);
            }
        }

        return new(added, removed, changed);
    }

    /// <summary>"이름 (2).확장자", "(3)" ... 로 비어 있는 이름을 찾는다.</summary>
    public static string Unique(string path, Func<string, bool> exists, ISet<string> claimed, string? self = null) =>
        Unique(path, exists, claimed, self, null);

    private static string Unique(string path, Func<string, bool> exists, ISet<string> claimed, string? self, Dictionary<string, int>? next)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(claimed);
        bool Taken(string p) => !string.Equals(p, self, StringComparison.OrdinalIgnoreCase) && (claimed.Contains(p) || exists(p));
        if (!Taken(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        // 계획을 만드는 동안 이미 찬 번호는 계속 차 있다 (claimed는 늘기만 하고 디스크는 그대로)
        var start = next is not null && next.TryGetValue(path, out var n) ? n : 2;
        for (var i = start; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i.ToString(CultureInfo.InvariantCulture)}){ext}");
            if (!Taken(p))
            {
                if (next is not null)
                {
                    next[path] = i + 1;
                }

                return p;
            }
        }
    }

    private static (string? Target, string? Reason) Classify(OrganizeEntry e, OrganizeOptions o, IMediaDateProvider? media, string root)
    {
        switch (o.Mode)
        {
            case OrganizeMode.ByType:
                {
                    var g = FileTypeGroups.FolderName(FileTypeGroups.GroupOf(e.Name), root);
                    var ext = Path.GetExtension(e.Name);
                    return (g, ext.Length > 0 ? Foldy.Core.Localization.Loc.F("op.typeExt", g, ext) : Foldy.Core.Localization.Loc.F("op.type", g));
                }

            case OrganizeMode.ByDate:
                {
                    DateTime local;
                    string source;
                    var taken = !e.IsCloudOnly && FileTypeGroups.IsMedia(e.Name) ? media?.GetTakenDate(e.FullPath) : null;
                    if (taken is { } t)
                    {
                        local = t;
                        source = Foldy.Core.Localization.Loc.T("op.taken");
                    }
                    else
                    {
                        local = e.LastWriteUtc.ToLocalTime();
                        source = Foldy.Core.Localization.Loc.T("op.modified");
                    }

                    var folder = local.ToString(o.DateFormat, CultureInfo.InvariantCulture).Replace('/', '\\');
                    return (folder, $"{source}: {local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
                }

            case OrganizeMode.ByTemplate:
                {
                    if (o.Template is null)
                    {
                        return (null, null);
                    }

                    foreach (var r in o.Template.Organize)
                    {
                        if (Matches(r, e, o.NowUtc))
                        {
                            return (r.Target, r.Reason ?? Foldy.Core.Localization.Loc.F("op.templateRule", o.Template.Name, r.Target));
                        }
                    }

                    return (null, null);
                }

            case OrganizeMode.Custom:
                {
                    var r = o.Rules.FirstOrDefault(x => Matches(x, e));
                    return r is null ? (null, null) : (r.Target, Foldy.Core.Localization.Loc.F("op.customRule", RuleLabel(r)));
                }

            default:
                return (null, null);
        }
    }

    public static bool Matches(CustomRule r, OrganizeEntry e)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(e);
        if (!string.IsNullOrWhiteSpace(r.Pattern)
            && !r.Pattern.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(p => FileSystemName.MatchesSimpleExpression(p, e.Name, ignoreCase: true)))
        {
            return false;
        }

        if (r.Keywords.Count > 0 && !r.Keywords.Any(k => e.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (r.Groups.Count > 0 && (e.IsFolder || !r.Groups.Contains(FileTypeGroups.GroupOf(e.Name), StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(r.Pattern) || r.Keywords.Count > 0 || r.Groups.Count > 0;
    }

    public static bool Matches(TemplateOrganizeRule r, OrganizeEntry e, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(e);
        var any = false;
        if (r.Keywords.Count > 0)
        {
            any = true;
            if (!r.Keywords.Any(k => e.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (r.Extensions.Count > 0)
        {
            any = true;
            var ext = Path.GetExtension(e.Name);
            if (!r.Extensions.Any(x => string.Equals(x.StartsWith('.') ? x : "." + x, ext, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (r.Groups.Count > 0)
        {
            any = true;
            if (!r.Groups.Contains(FileTypeGroups.GroupOf(e.Name), StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (r.NotOpenedForDays is { } days)
        {
            any = true;
            var last = e.LastAccessUtc > e.LastWriteUtc ? e.LastAccessUtc : e.LastWriteUtc;
            if ((nowUtc - last).TotalDays < days)
            {
                return false;
            }
        }

        return any;
    }

    /// <summary>
    /// 중복 후보: 정규화한 이름(복사본 표시 제거) + 크기가 같은 묶음에서만 내용을 비교한다.
    /// 먼저 앞·뒤 조각(64KB씩)만 읽어 다른 것을 걸러 내고, 남은 것만 끝까지 해시한다 (큰 파일을 통째로 읽는 일을 줄인다).
    /// 가장 오래된 파일은 원래 규칙대로, 나머지는 _중복 후보로. 클라우드 전용 파일은 내용을 읽지 않으므로 제외.
    /// </summary>
    private static List<List<OrganizeEntry>> FindDuplicates(List<OrganizeEntry> files, IContentHasher hasher, OrganizePlanProgress? progress, CancellationToken ct)
    {
        var result = new List<List<OrganizeEntry>>();
        var groups = files.Where(f => !f.IsCloudOnly && f.Size > 0)
            .GroupBy(f => (NormalizeName(f.Name), f.Size))
            .Where(g => g.Skip(1).Any())
            .Select(g => g.ToList())
            .ToList();
        if (groups.Count == 0)
        {
            return result;
        }

        progress?.Start(OrganizePlanStage.Duplicates, groups.Sum(g => g.Count));
        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4) };
        var found = new System.Collections.Concurrent.ConcurrentBag<List<OrganizeEntry>>();
        Parallel.ForEach(groups, parallel, g =>
        {
            // 1) 앞·뒤 조각이 같은 것끼리만 (해시를 지원하지 않으면 모두 한 묶음)
            var candidates = g.Count > 1 && hasher is IPartialContentHasher partial
                ? g.Select(f => (f, h: partial.HashEnds(f.FullPath))).Where(x => x.h is not null).GroupBy(x => x.h!, StringComparer.Ordinal)
                    .Where(x => x.Skip(1).Any()).Select(x => x.Select(y => y.f).ToList()).ToList()
                : [g];
            foreach (var c in candidates)
            {
                if (ct.IsCancellationRequested)
                {
                    return; // Parallel.ForEach가 OperationCanceledException으로 끝낸다
                }

                var byHash = c.Select(f => (f, h: hasher.Hash(f.FullPath))).Where(x => x.h is not null).GroupBy(x => x.h!, StringComparer.Ordinal);
                foreach (var same in byHash.Where(h => h.Skip(1).Any()))
                {
                    found.Add(same.Select(x => x.f).OrderBy(f => f.LastWriteUtc).ThenBy(f => f.FullPath.Length).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList());
                }
            }

            progress?.Step(g.Count);
        });
        ct.ThrowIfCancellationRequested();
        // 늘 같은 순서로 (남길 파일 경로 순)
        result.AddRange(found.OrderBy(g => g[0].FullPath, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    public static string NormalizeName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        return (CopySuffix().Replace(stem, "").Trim() + ext).ToUpperInvariant();
    }

    private static HashSet<string> OutputFolders(OrganizeOptions o)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OrganizeOptions.UnclassifiedFolder, OrganizeOptions.DuplicatesFolder, OrganizeOptions.UnclassifiedFolderKo, OrganizeOptions.DuplicatesFolderKo };
        switch (o.Mode)
        {
            case OrganizeMode.ByType:
                set.UnionWith(FileTypeGroups.All);
                set.UnionWith(FileTypeGroups.All.Select(g => FileTypeGroups.FolderName(g, null)));
                break;
            case OrganizeMode.ByTemplate when o.Template is not null:
                set.UnionWith(o.Template.Organize.Select(r => FirstSegment(r.Target)));
                break;
            case OrganizeMode.Custom:
                set.UnionWith(o.Rules.Select(r => FirstSegment(r.Target)));
                break;
            case OrganizeMode.ByDate:
                break;
        }

        return set;
    }

    private static string FirstSegment(string rel)
    {
        var n = NormalizeRel(rel);
        var i = n.IndexOf('\\', StringComparison.Ordinal);
        return i < 0 ? n : n[..i];
    }

    private static bool IsDirectChild(string root, string path) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? ""), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRel(string rel) =>
        string.Join('\\', (rel ?? "").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string RuleLabel(CustomRule r) => string.IsNullOrWhiteSpace(r.Name) ? r.Target : r.Name;
}
