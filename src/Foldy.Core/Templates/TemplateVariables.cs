using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Foldy.Core.Templates;

/// <summary>
/// 변수 치환. <c>{{key}}</c>는 입력값, <c>{{date:yyyy-MM-dd}}</c>는 만드는 날짜.
/// 설계서 표의 줄임 표기(<c>{{yyyy}}</c>, <c>{{yyyy-MM-dd}}</c>)처럼 날짜 형식 문자만으로 된 키도 날짜로 본다.
/// </summary>
public static partial class TemplateVariables
{
    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[yMdHhms_\-.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DateOnlyFormat();

    /// <summary>문자열에 쓰인 입력 변수 키 (날짜 제외, 순서 유지·중복 제거).</summary>
    public static IReadOnlyList<string> ExtractKeys(string? text)
    {
        var keys = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return keys;
        }

        foreach (Match m in Placeholder().Matches(text))
        {
            var key = m.Groups[1].Value;
            if (DateFormatOf(key) is null && !keys.Contains(key, StringComparer.Ordinal))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>템플릿 전체(트리 이름·파일 내용)에 쓰인 입력 변수 키.</summary>
    public static IReadOnlyList<string> ExtractKeys(FolderTemplate template)
    {
        var keys = new List<string>();
        void Add(string? s)
        {
            foreach (var k in ExtractKeys(s))
            {
                if (!keys.Contains(k, StringComparer.Ordinal))
                {
                    keys.Add(k);
                }
            }
        }

        void Walk(IEnumerable<TemplateNode> nodes)
        {
            foreach (var n in nodes)
            {
                Add(n.Name);
                Add(n.NameEn);
                Add(n.File);
                Walk(n.Children);
            }
        }

        Walk(template.Tree);
        return keys;
    }

    /// <summary>
    /// 치환한다. 값이 없는(또는 빈) 변수는 <paramref name="missing"/>에 모으고 자리는 <c>{key}</c> 그대로 남긴다
    /// (미리보기에서 무엇을 채워야 하는지 보이게).
    /// </summary>
    public static string Substitute(string text, IReadOnlyDictionary<string, string> values, DateTime now, ICollection<string>? missing = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);
        if (!text.Contains("{{", StringComparison.Ordinal))
        {
            return text;
        }

        return Placeholder().Replace(text, m =>
        {
            var key = m.Groups[1].Value;
            var fmt = DateFormatOf(key);
            if (fmt is not null)
            {
                return now.ToString(fmt, CultureInfo.InvariantCulture);
            }

            if (values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }

            missing?.Add(key);
            return "{" + key + "}";
        });
    }

    /// <summary>날짜 자리면 .NET 형식 문자열, 아니면 null.</summary>
    public static string? DateFormatOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.StartsWith("date:", StringComparison.OrdinalIgnoreCase))
        {
            var f = key[5..].Trim();
            return f.Length == 0 ? "yyyy-MM-dd" : f;
        }

        if (key.Equals("date", StringComparison.OrdinalIgnoreCase))
        {
            return "yyyy-MM-dd";
        }

        return key.Contains('y', StringComparison.Ordinal) && DateOnlyFormat().IsMatch(key) ? key : null;
    }

    /// <summary>입력값으로 쓸 수 없는 문자(경로 구분자 등)를 검사한다. 문제가 없으면 null.</summary>
    public static string? ValidateValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var v = value.Trim();
        if (v is "." or "..")
        {
            return Foldy.Core.Localization.Loc.T("tv.dots");
        }

        var bad = new StringBuilder();
        foreach (var c in v)
        {
            if (c is '/' or '\\' || TemplateValidator.IsInvalidNameChar(c))
            {
                bad.Append(c);
            }
        }

        return bad.Length == 0 ? null : Foldy.Core.Localization.Loc.F("tvar.badChar", bad);
    }
}
