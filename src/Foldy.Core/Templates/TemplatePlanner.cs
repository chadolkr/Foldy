using System.Text.RegularExpressions;

namespace Foldy.Core.Templates;

/// <summary>계획 옵션. 값은 변수 키 → 입력값.</summary>
public sealed class TemplatePlanOptions
{
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();

    public DateTime Now { get; init; } = DateTime.Now;

    /// <summary>체크 해제한 노드 키 (<see cref="PlannedNode.Key"/>). 폴더를 빼면 그 아래도 빠진다.</summary>
    public IReadOnlySet<string> Excluded { get; init; } = new HashSet<string>();

    /// <summary>체크한 Optional 노드 키 (Optional은 기본 해제).</summary>
    public IReadOnlySet<string> IncludedOptional { get; init; } = new HashSet<string>();

    /// <summary>위저드 "번호 접두어 끄기": 01_계약 → 계약, 1_Projects → Projects.</summary>
    public bool StripNumberPrefix { get; init; }

    /// <summary>위저드 "폴더 이름 영문": NameEn이 있으면 그것을 쓴다.</summary>
    public bool UseEnglishNames { get; init; }
}

public enum PlannedState
{
    /// <summary>새로 만든다.</summary>
    Create,

    /// <summary>이미 있어 그대로 둔다 (덮어쓰지 않음).</summary>
    Exists,

    /// <summary>체크 해제됨.</summary>
    Excluded,

    /// <summary>같은 이름의 다른 종류(파일↔폴더)가 있거나 이름이 잘못돼 만들 수 없다.</summary>
    Blocked,
}

/// <summary>미리보기 트리의 한 칸 (치환을 마친 이름).</summary>
public sealed class PlannedNode
{
    /// <summary>원본 노드의 위치 키 (예: "0/2"). 여러 단계 이름의 중간 칸도 같은 키를 갖는다.</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    /// <summary>만들 위치 기준 상대 경로 (\ 구분).</summary>
    public required string RelativePath { get; init; }

    public required string FullPath { get; init; }

    public bool IsFile { get; init; }

    public string? Content { get; init; }

    public bool Optional { get; init; }

    public PlannedState State { get; set; }

    public string? Problem { get; set; }

    public List<PlannedNode> Children { get; } = [];
}

public sealed class TemplatePlan
{
    public required string TargetRoot { get; init; }

    public List<PlannedNode> Roots { get; } = [];

    public List<TemplateIssue> Issues { get; } = [];

    /// <summary>값이 비어 있는 변수 키.</summary>
    public List<string> MissingVariables { get; } = [];

    /// <summary>만들 위치에 "최상위 폴더" 하나로 모이면 그 경로, 아니면 TargetRoot.</summary>
    public string TopFolder { get; set; } = "";

    /// <summary>만들 수 있는지 (필수 변수와 안전 검사 통과).</summary>
    public bool CanApply => Issues.Count == 0 && MissingVariables.Count == 0;

    public IEnumerable<PlannedNode> Flatten()
    {
        var stack = new Stack<PlannedNode>(Roots.AsEnumerable().Reverse());
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (var i = n.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(n.Children[i]);
            }
        }
    }

    public int CreateCount => Flatten().Count(n => n.State == PlannedState.Create);

    public int ExistingCount => Flatten().Count(n => n.State == PlannedState.Exists);
}

/// <summary>
/// 템플릿 → 만들 트리 계획 (순수 계산 + 존재 여부 조회). "없는 것만 만들고 절대 덮어쓰지 않는다".
/// </summary>
public static partial class TemplatePlanner
{
    [GeneratedRegex(@"^\d{1,2}_(?=.)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPrefix();

    public static TemplatePlan Plan(FolderTemplate template, string targetRoot, TemplatePlanOptions options, ITemplateFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fs);

        // 빈 값은 변수의 기본값으로 채운다
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in template.Variables)
        {
            if (!string.IsNullOrEmpty(v.Default))
            {
                merged[v.Key] = v.Default!;
            }
        }

        foreach (var (k, v) in options.Values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                merged[k] = v;
            }
        }

        options = new TemplatePlanOptions
        {
            Values = merged,
            Now = options.Now,
            Excluded = options.Excluded,
            IncludedOptional = options.IncludedOptional,
            StripNumberPrefix = options.StripNumberPrefix,
            UseEnglishNames = options.UseEnglishNames,
        };
        var root = Path.GetFullPath(targetRoot);
        var plan = new TemplatePlan { TargetRoot = root, TopFolder = root };
        plan.Issues.AddRange(TemplateValidator.Validate(template));

        // 입력값 자체 검사 (경로 구분자·금지 문자가 들어오면 다른 곳에 만들어질 수 있다)
        var nameKeys = NameKeys(template.Tree);
        foreach (var (key, value) in options.Values)
        {
            if (nameKeys.Contains(key) && TemplateVariables.ValidateValue(value) is { } err)
            {
                plan.Issues.Add(new(TemplateIssueKind.InvalidChar, "{{" + key + "}}", err));
            }
        }

        var missing = new HashSet<string>(StringComparer.Ordinal);
        Build(template.Tree, "", root, "", options, fs, plan.Roots, plan, missing, parentExcluded: false);
        foreach (var v in template.Variables)
        {
            if (v.Required && (!options.Values.TryGetValue(v.Key, out var val) || string.IsNullOrWhiteSpace(val)))
            {
                missing.Add(v.Key);
            }
        }

        plan.MissingVariables.AddRange(missing.Order(StringComparer.Ordinal));

        // 최상위 폴더: 포함된 루트가 폴더 하나뿐이면 그 체인의 끝 (중간 단계는 자식이 하나)
        var included = plan.Roots.Where(r => r.State != PlannedState.Excluded).ToList();
        if (included.Count == 1 && !included[0].IsFile)
        {
            var n = included[0];
            while (n.Children.Count == 1 && !n.Children[0].IsFile && n.Children[0].Key == n.Key)
            {
                n = n.Children[0];
            }

            plan.TopFolder = n.FullPath;
        }

        return plan;
    }

    private static void Build(
        IList<TemplateNode> nodes, string keyPrefix, string root, string relParent, TemplatePlanOptions o,
        ITemplateFileSystem fs, List<PlannedNode> output, TemplatePlan plan, HashSet<string> missing, bool parentExcluded)
    {
        var ctx = new Dictionary<string, string>(o.Values, StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (!WhenMatches(node.When, ctx))
            {
                continue;
            }

            var key = keyPrefix.Length == 0 ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : keyPrefix + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var excluded = parentExcluded || o.Excluded.Contains(key) || (node.Optional && !o.IncludedOptional.Contains(key));
            var rawName = o.UseEnglishNames && !string.IsNullOrWhiteSpace(node.NameEn) ? node.NameEn! : node.Name;
            var nodeMissing = new List<string>();
            var resolved = TemplateVariables.Substitute(rawName, o.Values, o.Now, nodeMissing);
            foreach (var m in nodeMissing)
            {
                missing.Add(m);
            }

            var segments = resolved.Split('/', '\\');
            var list = output;
            var rel = relParent;
            PlannedNode? last = null;
            for (var s = 0; s < segments.Length; s++)
            {
                var seg = segments[s].Trim();
                var isLastSeg = s == segments.Length - 1;
                var isFile = isLastSeg && node.IsFile;
                if (!isFile && o.StripNumberPrefix)
                {
                    seg = NumberPrefix().Replace(seg, "");
                }

                rel = rel.Length == 0 ? seg : rel + "\\" + seg;
                var full = Path.Combine(root, rel);

                // 같은 이름의 중간 단계가 이미 계획돼 있으면 합친다 (두 노드가 {{고객}}/... 를 공유하는 경우)
                var existingPlanned = list.FirstOrDefault(p => !p.IsFile && !isFile && string.Equals(p.Name, seg, StringComparison.OrdinalIgnoreCase));
                PlannedNode pn;
                if (existingPlanned is not null && !isLastSeg)
                {
                    pn = existingPlanned;
                }
                else
                {
                    pn = new PlannedNode
                    {
                        Key = key,
                        Name = seg,
                        RelativePath = rel,
                        FullPath = full,
                        IsFile = isFile,
                        Content = isFile ? TemplateVariables.Substitute(node.File!, o.Values, o.Now) : null,
                        Optional = node.Optional,
                    };
                    Resolve(pn, excluded, fs, plan);
                    list.Add(pn);
                }

                list = pn.Children;
                last = pn;
            }

            if (last is not null && !node.IsFile)
            {
                Build(node.Children, key, root, rel, o, fs, last.Children, plan, missing, excluded);
            }
        }
    }

    private static void Resolve(PlannedNode pn, bool excluded, ITemplateFileSystem fs, TemplatePlan plan)
    {
        var problems = TemplateValidator.ValidateResolvedSegment(pn.Name, pn.RelativePath);
        if (pn.Name.Contains('{', StringComparison.Ordinal))
        {
            // 아직 채우지 않은 변수 자리: 미리보기에는 보이되 만들 수 없다 (MissingVariables로 막힘)
            pn.State = excluded ? PlannedState.Excluded : PlannedState.Create;
            return;
        }

        if (problems.Count > 0)
        {
            pn.State = PlannedState.Blocked;
            pn.Problem = problems[0].Message;
            if (!excluded)
            {
                plan.Issues.AddRange(problems);
            }

            return;
        }

        if (pn.FullPath.Length > TemplateValidator.MaxPathLength)
        {
            pn.State = PlannedState.Blocked;
            pn.Problem = "전체 경로가 너무 깁니다.";
            if (!excluded)
            {
                plan.Issues.Add(new(TemplateIssueKind.PathTooLong, pn.RelativePath, pn.Problem));
            }

            return;
        }

        if (excluded)
        {
            pn.State = PlannedState.Excluded;
            return;
        }

        var dirExists = fs.DirectoryExists(pn.FullPath);
        var fileExists = fs.FileExists(pn.FullPath);
        if (pn.IsFile ? dirExists : fileExists)
        {
            pn.State = PlannedState.Blocked;
            pn.Problem = pn.IsFile ? "같은 이름의 폴더가 있어 파일을 만들 수 없습니다." : "같은 이름의 파일이 있어 폴더를 만들 수 없습니다.";
            return;
        }

        pn.State = (pn.IsFile ? fileExists : dirExists) ? PlannedState.Exists : PlannedState.Create;
    }

    /// <summary>이름(경로)에 쓰인 변수 키. 파일 내용에만 쓰인 값은 경로 문자 검사를 하지 않는다.</summary>
    private static HashSet<string> NameKeys(IEnumerable<TemplateNode> nodes)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Walk(IEnumerable<TemplateNode> ns)
        {
            foreach (var n in ns)
            {
                set.UnionWith(TemplateVariables.ExtractKeys(n.Name));
                set.UnionWith(TemplateVariables.ExtractKeys(n.NameEn));
                Walk(n.Children);
            }
        }

        Walk(nodes);
        return set;
    }

    /// <summary><c>key=a|b</c> 또는 <c>key!=a</c>. 조건이 없으면 참.</summary>
    public static bool WhenMatches(string? when, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (string.IsNullOrWhiteSpace(when))
        {
            return true;
        }

        var neg = when.Contains("!=", StringComparison.Ordinal);
        var parts = when.Split(neg ? "!=" : "=", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return true;
        }

        values.TryGetValue(parts[0], out var actual);
        var hit = parts[1].Split('|', StringSplitOptions.TrimEntries)
            .Any(v => string.Equals(v, actual ?? "", StringComparison.OrdinalIgnoreCase));
        return neg ? !hit : hit;
    }
}
