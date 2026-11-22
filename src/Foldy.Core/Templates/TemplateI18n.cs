using System.Text.Json;
using System.Text.Json.Nodes;
using Foldy.Core.Localization;

namespace Foldy.Core.Templates;

/// <summary>
/// 내장 템플릿과 위저드 질문을 앱 언어로 (DECISIONS 2026-11-22 "폴더 이름은 만드는 그 시점의 앱 언어로").
/// 번역은 <c>assets/templates/i18n/&lt;언어&gt;.json</c> (한국어 글 → 번역, 원본은 build/i18n/templates.py).
/// 폴더 이름(tree 안의 name, target, folder)은 '/'·'\' 조각마다 바꿔 트리와 정리 규칙이 늘 같다.
/// {{변수}} 자리와 id·key·when·file·regex·extensions·nameEn은 그대로. keywords는 바꾸지 않고 번역을 덧붙인다.
/// 이미 만든 폴더는 건드리지 않는다: 바뀌는 것은 새로 만들 때 쓰는 이름뿐이다.
/// </summary>
public static class TemplateI18n
{
    private static readonly HashSet<string> Skip = ["groups", "id", "key", "when", "file", "regex", "extensions", "nameEn", "weights", "schemaVersion", "value", "templates", "template"];
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Cache = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    /// <summary>그 언어의 번역 표 (한국어·없는 언어는 빈 표).</summary>
    public static IReadOnlyDictionary<string, string> Table(string language)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(language, out var t))
            {
                return t;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (language != "ko")
            {
                using var s = typeof(TemplateI18n).Assembly.GetManifestResourceStream(TemplateLibrary.ResourcePrefix + "i18n." + language + ".json");
                if (s is not null)
                {
                    map = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? map;
                }
            }

            Cache[language] = map;
            return map;
        }
    }

    /// <summary>글 하나 (표에 없으면 그대로).</summary>
    public static string Text(string text, string? language = null) =>
        Table(language ?? Loc.Current).TryGetValue(text, out var v) ? v : text;

    /// <summary>폴더 이름·경로: '/'·'\'로 나눈 조각마다.</summary>
    public static string PathText(string path, string? language = null)
    {
        var map = Table(language ?? Loc.Current);
        return map.Count == 0 ? path : string.Concat(SplitKeep(path).Select(p => p is "/" or "\\" ? p : map.TryGetValue(p, out var v) ? v : p));
    }

    /// <summary>템플릿·위저드 JSON 글 전체를 그 언어로.</summary>
    public static string Localize(string json, string? language = null)
    {
        var map = Table(language ?? Loc.Current);
        if (map.Count == 0)
        {
            return json;
        }

        var root = JsonNode.Parse(json);
        if (root is null)
        {
            return json;
        }

        Walk(root, null, false, map);
        return root.ToJsonString();
    }

    private static void Walk(JsonNode node, string? key, bool inTree, IReadOnlyDictionary<string, string> map)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o.ToList())
                {
                    if (v is null || Skip.Contains(k))
                    {
                        continue;
                    }

                    if (k == "keywords" && v is JsonArray words)
                    {
                        var extra = words.Select(w => w?.GetValueKind() == JsonValueKind.String ? w.GetValue<string>() : null)
                            .Where(w => w is not null && map.ContainsKey(w)).Select(w => map[w!]).ToList();
                        foreach (var e in extra.Where(e => !words.Any(w => w?.GetValueKind() == JsonValueKind.String && string.Equals(w.GetValue<string>(), e, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            words.Add(e);
                        }

                        continue;
                    }

                    var childInTree = inTree || k == "tree";
                    if (v is JsonValue sv && sv.GetValueKind() == JsonValueKind.String)
                    {
                        o[k] = Translate(sv.GetValue<string>(), k, childInTree, map);
                    }
                    else
                    {
                        Walk(v, k, childInTree, map);
                    }
                }

                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue sv && sv.GetValueKind() == JsonValueKind.String)
                    {
                        a[i] = Translate(sv.GetValue<string>(), key, inTree, map);
                    }
                    else if (a[i] is { } child)
                    {
                        Walk(child, key, inTree, map);
                    }
                }

                break;
        }
    }

    private static string Translate(string s, string? key, bool inTree, IReadOnlyDictionary<string, string> map)
    {
        var isPath = key is "target" or "folder" || (key == "name" && inTree);
        if (isPath)
        {
            return string.Concat(SplitKeep(s).Select(p => p is "/" or "\\" ? p : map.TryGetValue(p, out var v) ? v : p));
        }

        return map.TryGetValue(s, out var t) ? t : s;
    }

    private static IEnumerable<string> SplitKeep(string s)
    {
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is '/' or '\\')
            {
                yield return s[start..i];
                yield return s[i].ToString();
                start = i + 1;
            }
        }

        yield return s[start..];
    }
}
