using System.Text.Json;

namespace Foldy.Core.Templates;

/// <summary>
/// 폴더 위저드 추천 규칙 데이터 (assets/templates/wizard.json): 용도, 질문, 선택지, 템플릿별 가중치.
/// 규칙 기반이라 인터넷 없이 동작한다.
/// </summary>
public sealed class WizardDefinition
{
    public int SchemaVersion { get; set; } = 1;

    public string PurposeQuestion { get; set; } = "이 폴더를 어디에 쓰실 건가요?";

    public IList<WizardPurpose> Purposes { get; set; } = [];

    public IList<WizardQuestion> Questions { get; set; } = [];

    public static WizardDefinition Parse(string json)
    {
        var d = JsonSerializer.Deserialize<WizardDefinition>(json, TemplateJson.Options) ?? new WizardDefinition();
        d.Purposes ??= [];
        d.Questions ??= [];
        foreach (var p in d.Purposes)
        {
            p.Weights ??= new Dictionary<string, double>();
        }

        foreach (var q in d.Questions)
        {
            q.Purposes ??= [];
            q.Choices ??= [];
            foreach (var c in q.Choices)
            {
                c.Weights ??= new Dictionary<string, double>();
                c.Variables ??= new Dictionary<string, string>();
            }
        }

        return d;
    }

    /// <summary>용도에 맞는 질문 (순서 유지).</summary>
    public IReadOnlyList<WizardQuestion> QuestionsFor(string purposeId) =>
        Questions.Where(q => q.Purposes.Count == 0 || q.Purposes.Contains(purposeId, StringComparer.OrdinalIgnoreCase)).ToList();
}

public sealed class WizardPurpose
{
    public string Id { get; set; } = "";

    public string Label { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>"organize"면 5.11 폴더 정리로 넘어간다.</summary>
    public string? Action { get; set; }

    public IDictionary<string, double> Weights { get; set; } = new Dictionary<string, double>();
}

public sealed class WizardQuestion
{
    public string Id { get; set; } = "";

    public string Text { get; set; } = "";

    public string? Hint { get; set; }

    /// <summary>이 질문을 보여줄 용도 id (비면 모두).</summary>
    public IList<string> Purposes { get; set; } = [];

    public IList<WizardChoice> Choices { get; set; } = [];
}

public sealed class WizardChoice
{
    public string Id { get; set; } = "";

    public string Label { get; set; } = "";

    /// <summary>템플릿 id → 점수.</summary>
    public IDictionary<string, double> Weights { get; set; } = new Dictionary<string, double>();

    /// <summary>추천 이유 문장 (가중치가 양수인 템플릿에 붙는다).</summary>
    public string? Reason { get; set; }

    /// <summary>골랐을 때 채울 템플릿 변수 (예: language=dotnet).</summary>
    public IDictionary<string, string> Variables { get; set; } = new Dictionary<string, string>();
}

public sealed record WizardRecommendation(FolderTemplate Template, double Score, IReadOnlyList<string> Reasons);

/// <summary>답 → 템플릿 점수.</summary>
public static class WizardScorer
{
    /// <param name="answers">질문 id → 선택지 id.</param>
    public static IReadOnlyList<WizardRecommendation> Rank(
        WizardDefinition def, string purposeId, IReadOnlyDictionary<string, string> answers, IEnumerable<FolderTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(templates);
        var purpose = def.Purposes.FirstOrDefault(p => string.Equals(p.Id, purposeId, StringComparison.OrdinalIgnoreCase));
        var list = new List<WizardRecommendation>();
        foreach (var t in templates)
        {
            double score = 0;
            var reasons = new List<string>();
            if (purpose is not null && TryWeight(purpose.Weights, t.Id, out var pw))
            {
                score += pw;
                if (pw > 0 && purpose.Description is { Length: > 0 })
                {
                    reasons.Add(purpose.Description);
                }
            }

            // 내 템플릿도 용도 태그를 달면 후보에 든다
            if (t.Purposes.Contains(purposeId, StringComparer.OrdinalIgnoreCase))
            {
                score += 1;
            }

            foreach (var q in def.QuestionsFor(purposeId))
            {
                if (!answers.TryGetValue(q.Id, out var choiceId))
                {
                    continue;
                }

                var c = q.Choices.FirstOrDefault(x => string.Equals(x.Id, choiceId, StringComparison.OrdinalIgnoreCase));
                if (c is not null && TryWeight(c.Weights, t.Id, out var w))
                {
                    score += w;
                    if (w > 0 && c.Reason is { Length: > 0 } && !reasons.Contains(c.Reason))
                    {
                        reasons.Add(c.Reason);
                    }
                }
            }

            list.Add(new WizardRecommendation(t, score, reasons));
        }

        return list.OrderByDescending(r => r.Score).ThenBy(r => r.Template.IsBuiltIn ? 0 : 1).ThenBy(r => r.Template.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>추천 1~2개: 1위, 그리고 1위 점수의 70% 이상인 2위.</summary>
    public static IReadOnlyList<WizardRecommendation> Top(IReadOnlyList<WizardRecommendation> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        if (ranked.Count == 0 || ranked[0].Score <= 0)
        {
            return ranked.Take(1).ToList();
        }

        var top = new List<WizardRecommendation> { ranked[0] };
        if (ranked.Count > 1 && ranked[1].Score > 0 && ranked[1].Score >= ranked[0].Score * 0.7)
        {
            top.Add(ranked[1]);
        }

        return top;
    }

    /// <summary>답에서 나온 템플릿 변수 (개발 언어 등).</summary>
    public static Dictionary<string, string> VariablesFrom(WizardDefinition def, string purposeId, IReadOnlyDictionary<string, string> answers)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(answers);
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var q in def.QuestionsFor(purposeId))
        {
            if (answers.TryGetValue(q.Id, out var cid)
                && q.Choices.FirstOrDefault(x => string.Equals(x.Id, cid, StringComparison.OrdinalIgnoreCase)) is { } c)
            {
                foreach (var (k, v) in c.Variables)
                {
                    vars[k] = v;
                }
            }
        }

        return vars;
    }

    private static bool TryWeight(IDictionary<string, double> weights, string id, out double w)
    {
        foreach (var (k, v) in weights)
        {
            if (string.Equals(k, id, StringComparison.OrdinalIgnoreCase))
            {
                w = v;
                return true;
            }
        }

        w = 0;
        return false;
    }
}
