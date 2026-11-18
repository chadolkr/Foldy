using System.Text;

namespace Foldy.Core.Templates;

/// <summary>위저드 비교 화면용 문자열 도우미 (예시 트리, 사용법 요약, 복제).</summary>
public static class TemplateText
{
    /// <summary>계획 트리를 ├─ 모양 문자열로.</summary>
    public static string RenderTree(IEnumerable<PlannedNode> roots, int maxLines = 40)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var sb = new StringBuilder();
        var lines = 0;
        void Walk(IReadOnlyList<PlannedNode> nodes, string indent)
        {
            for (var i = 0; i < nodes.Count && lines < maxLines; i++)
            {
                var n = nodes[i];
                if (n.State == PlannedState.Excluded)
                {
                    continue;
                }

                var last = i == nodes.Count - 1;
                sb.Append(indent).Append(last ? "└─ " : "├─ ").Append(n.Name).Append(n.IsFile ? "" : "/").Append('\n');
                lines++;
                Walk(n.Children, indent + (last ? "   " : "│  "));
            }
        }

        Walk(roots.ToList(), "");
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>안내문의 한 줄 요약: 첫 인용(&gt;) 줄, 없으면 제목 다음 첫 문단.</summary>
    public static string GuideSummary(string? guide)
    {
        if (string.IsNullOrWhiteSpace(guide))
        {
            return "";
        }

        var lines = guide.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (t.StartsWith('>'))
            {
                return t.TrimStart('>').Trim();
            }
        }

        return lines.Select(l => l.Trim()).FirstOrDefault(t => t.Length > 0 && !t.StartsWith('#')) ?? "";
    }

    /// <summary>파일명 규칙 한 덩어리 문자열 (패턴 + 예시).</summary>
    public static string NamingSummary(FolderTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var sb = new StringBuilder();
        foreach (var n in t.Naming)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            if (n.Folder is not null)
            {
                sb.Append('[').Append(n.Folder).Append("] ");
            }

            sb.Append(n.Pattern);
            if (n.Examples.Count > 0)
            {
                sb.Append("\n예: ").Append(string.Join(", ", n.Examples));
            }
        }

        return sb.ToString();
    }

    /// <summary>깊은 복사 (JSON 왕복). 내장 템플릿을 "내 템플릿으로 저장"할 때 쓴다.</summary>
    public static FolderTemplate Clone(FolderTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var c = TemplateJson.Parse(TemplateJson.Serialize(t));
        c.Guide = t.Guide;
        return c;
    }

    /// <summary>체크 해제한 노드(키)를 뺀 트리로 바꾼다.</summary>
    public static void Prune(FolderTemplate t, IReadOnlySet<string> excludedKeys)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(excludedKeys);
        void Walk(IList<TemplateNode> nodes, string prefix)
        {
            var keep = new List<TemplateNode>();
            for (var i = 0; i < nodes.Count; i++)
            {
                var key = prefix.Length == 0 ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : prefix + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (excludedKeys.Contains(key))
                {
                    continue;
                }

                Walk(nodes[i].Children, key);
                keep.Add(nodes[i]);
            }

            nodes.Clear();
            foreach (var n in keep)
            {
                nodes.Add(n);
            }
        }

        Walk(t.Tree, "");
    }
}
