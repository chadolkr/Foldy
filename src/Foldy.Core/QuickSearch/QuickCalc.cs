using System.Globalization;
using System.Text.RegularExpressions;

namespace Foldy.Core.QuickSearch;

/// <summary>빠른 검색에서 바로 보이는 답 (계산·단위 변환). <see cref="Copy"/>는 Enter로 복사할 글자.</summary>
public sealed record QuickAnswer(string Display, string Copy, string Kind);

/// <summary>
/// 빠른 검색의 간단 계산과 단위 변환 (Spotlight처럼 입력하면 바로 답). 밖으로 아무것도 보내지 않는다.
/// 계산: + - * / ^ % 괄호, ×·÷, 1,000 같은 쉼표. 연산자가 하나도 없으면(그냥 숫자) 답하지 않는다.
/// 단위: "5 km mi", "5km to mi", "3평", "100F", "2 GB MB" (대상이 없으면 흔히 쓰는 짝으로).
/// </summary>
public static partial class QuickCalc
{
    public static QuickAnswer? Answer(string input, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var q = input.Trim();
        if (q.Length == 0 || q.Length > 200)
        {
            return null;
        }

        return Convert(q, culture) ?? Calculate(q, culture);
    }

    // ───────── 계산 ─────────

    public static QuickAnswer? Calculate(string q, CultureInfo culture)
    {
        var s = q.Replace('×', '*').Replace('÷', '/').Replace(" ", "", StringComparison.Ordinal).TrimEnd('=');
        s = ThousandsRegex().Replace(s, m => m.Value.Replace(",", "", StringComparison.Ordinal));
        if (s.Length == 0 || !s.Any(char.IsDigit) || !s.Any(c => c is '+' or '*' or '/' or '^' or '%' or '(' || (c == '-' && s.IndexOf('-') > 0)))
        {
            return null;
        }

        if (s.Any(c => !(char.IsDigit(c) || c is '.' or '+' or '-' or '*' or '/' or '^' or '%' or '(' or ')')))
        {
            return null;
        }

        var p = new Parser(s);
        double v;
        try
        {
            v = p.Expr();
            if (p.Pos != s.Length || double.IsNaN(v) || double.IsInfinity(v))
            {
                return null;
            }
        }
        catch (FormatException)
        {
            return null;
        }

        var shown = Format(v, culture);
        return new QuickAnswer($"{q.TrimEnd('=', ' ')} = {shown}", Format(v, CultureInfo.InvariantCulture, grouping: false), "계산");
    }

    private sealed class Parser(string s)
    {
        public int Pos;

        public double Expr()
        {
            var v = Term();
            while (Pos < s.Length && s[Pos] is '+' or '-')
            {
                var op = s[Pos++];
                var r = Term();
                v = op == '+' ? v + r : v - r;
            }

            return v;
        }

        private double Term()
        {
            var v = Power();
            while (Pos < s.Length && s[Pos] is '*' or '/')
            {
                var op = s[Pos++];
                var r = Power();
                v = op == '*' ? v * r : v / r;
            }

            return v;
        }

        private double Power()
        {
            var b = Unary();
            if (Pos < s.Length && s[Pos] == '^')
            {
                Pos++;
                return Math.Pow(b, Power()); // 오른쪽부터 (2^3^2 = 2^9)
            }

            return b;
        }

        private double Unary()
        {
            if (Pos < s.Length && s[Pos] is '-' or '+')
            {
                var neg = s[Pos++] == '-';
                var v = Unary();
                return neg ? -v : v;
            }

            return Postfix();
        }

        private double Postfix()
        {
            var v = Atom();
            while (Pos < s.Length && s[Pos] == '%')
            {
                Pos++;
                v /= 100;
            }

            return v;
        }

        private double Atom()
        {
            if (Pos < s.Length && s[Pos] == '(')
            {
                Pos++;
                var v = Expr();
                if (Pos >= s.Length || s[Pos] != ')')
                {
                    throw new FormatException();
                }

                Pos++;
                return v;
            }

            var start = Pos;
            while (Pos < s.Length && (char.IsDigit(s[Pos]) || s[Pos] == '.'))
            {
                Pos++;
            }

            if (start == Pos)
            {
                throw new FormatException();
            }

            return double.Parse(s.AsSpan(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }

    // ───────── 단위 ─────────

    private enum Dim { Length, Area, Mass, Volume, Temp, Data }

    private sealed record Unit(string Name, Dim Dim, double Factor, string[] Aliases, string? Partner = null);

    // Factor: 기준 단위(m, ㎡, g, L, B)로 바꾸는 곱. 온도는 따로.
    private static readonly Unit[] Units =
    [
        new("mm", Dim.Length, 0.001, ["mm", "밀리미터", "밀리"], "in"),
        new("cm", Dim.Length, 0.01, ["cm", "센티미터", "센티"], "in"),
        new("m", Dim.Length, 1, ["m", "미터"], "ft"),
        new("km", Dim.Length, 1000, ["km", "킬로미터", "키로"], "mi"),
        new("in", Dim.Length, 0.0254, ["in", "inch", "inches", "인치", "\""], "cm"),
        new("ft", Dim.Length, 0.3048, ["ft", "feet", "foot", "피트", "'"], "m"),
        new("yd", Dim.Length, 0.9144, ["yd", "yard", "yards", "야드"], "m"),
        new("mi", Dim.Length, 1609.344, ["mi", "mile", "miles", "마일"], "km"),
        new("㎡", Dim.Area, 1, ["m2", "㎡", "m^2", "제곱미터", "sqm"], "평"),
        new("평", Dim.Area, 400.0 / 121.0, ["평"], "㎡"),
        new("ft²", Dim.Area, 0.09290304, ["ft2", "ft^2", "sqft", "제곱피트"], "㎡"),
        new("acre", Dim.Area, 4046.8564224, ["acre", "acres", "에이커"], "㎡"),
        new("g", Dim.Mass, 1, ["g", "그램"], "oz"),
        new("kg", Dim.Mass, 1000, ["kg", "킬로그램", "킬로"], "lb"),
        new("lb", Dim.Mass, 453.59237, ["lb", "lbs", "pound", "pounds", "파운드"], "kg"),
        new("oz", Dim.Mass, 28.349523125, ["oz", "ounce", "ounces", "온스"], "g"),
        new("근", Dim.Mass, 600, ["근"], "kg"),
        new("mL", Dim.Volume, 0.001, ["ml", "밀리리터"], "fl oz"),
        new("L", Dim.Volume, 1, ["l", "리터"], "gal"),
        new("gal", Dim.Volume, 3.785411784, ["gal", "gallon", "gallons", "갤런"], "L"),
        new("fl oz", Dim.Volume, 0.0295735295625, ["floz", "fl oz", "fl.oz"], "mL"),
        new("°C", Dim.Temp, 0, ["c", "°c", "℃", "섭씨", "도"], "°F"),
        new("°F", Dim.Temp, 0, ["f", "°f", "℉", "화씨"], "°C"),
        new("K", Dim.Temp, 0, ["k", "kelvin", "켈빈"], "°C"),
        new("B", Dim.Data, 1, ["b", "byte", "bytes", "바이트"], "KB"),
        new("KB", Dim.Data, 1024, ["kb", "kib"], "MB"),
        new("MB", Dim.Data, 1024d * 1024, ["mb", "mib", "메가"], "GB"),
        new("GB", Dim.Data, 1024d * 1024 * 1024, ["gb", "gib", "기가"], "MB"),
        new("TB", Dim.Data, 1024d * 1024 * 1024 * 1024, ["tb", "tib", "테라"], "GB"),
    ];

    private static Unit? Find(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        return n.Length == 0 ? null : Units.FirstOrDefault(u => u.Aliases.Any(a => a.Equals(n, StringComparison.OrdinalIgnoreCase)));
    }

    public static QuickAnswer? Convert(string q, CultureInfo culture)
    {
        var m = NumberRegex().Match(q);
        if (!m.Success || !double.TryParse(m.Groups["n"].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            return null;
        }

        // 나머지를 낱말로: "km to mi", "km는 몇 mi로?", "kmmi"
        var rest = m.Groups["rest"].Value.Replace("fl oz", "floz", StringComparison.OrdinalIgnoreCase).Replace("→", " ", StringComparison.Ordinal).Replace("->", " ", StringComparison.Ordinal).Replace("=", " ", StringComparison.Ordinal).Replace("?", " ", StringComparison.Ordinal);
        string[] connectors = ["to", "in", "into", "몇", "는", "은", "를", "을", "로", "으로"];
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => StripParticle(w))
            .Where((w, i) => w.Length > 0 && (i == 0 || !connectors.Contains(w, StringComparer.OrdinalIgnoreCase))) // 첫 낱말은 단위 (in = 인치)
            .ToList();

        Unit? from = null, to = null;
        if (words.Count == 1)
        {
            from = Find(words[0]);
            if (from is null)
            {
                // 붙여 쓴 "kmmi": 앞뒤로 나눠 둘 다 단위이고 같은 종류인 곳
                for (var i = 1; i < words[0].Length && from is null; i++)
                {
                    if (Find(words[0][..i]) is { } f && Find(words[0][i..]) is { } t && f.Dim == t.Dim)
                    {
                        (from, to) = (f, t);
                    }
                }
            }
        }
        else if (words.Count == 2)
        {
            from = Find(words[0]);
            to = Find(words[1]);
            if (to is null)
            {
                return null;
            }
        }

        if (from is null)
        {
            return null;
        }

        to ??= from.Partner is { } p ? Units.First(u => u.Name == p) : null;
        if (to is null || to.Dim != from.Dim || to == from)
        {
            return null;
        }

        var v = from.Dim == Dim.Temp ? Temp(n, from.Name, to.Name) : n * from.Factor / to.Factor;
        var shown = $"{Format(n, culture)} {from.Name} = {Format(Math.Abs(v) >= 1 ? Math.Round(v, 2) : v, culture)} {to.Name}"; // 보기는 소수 둘째 자리까지, 복사는 자세히
        return new QuickAnswer(shown, Format(v, CultureInfo.InvariantCulture, grouping: false), "단위 변환");
    }

    /// <summary>"km는" → "km", "mi로" → "mi" (단위 뒤에 붙은 조사).</summary>
    private static string StripParticle(string w)
    {
        foreach (var p in new[] { "으로", "는", "은", "를", "을", "로" })
        {
            if (w.Length > p.Length && w.EndsWith(p, StringComparison.Ordinal) && Find(w[..^p.Length]) is not null)
            {
                return w[..^p.Length];
            }
        }

        return w;
    }

    private static double Temp(double v, string from, string to)
    {
        var c = from switch { "°F" => (v - 32) * 5 / 9, "K" => v - 273.15, _ => v };
        return to switch { "°F" => c * 9 / 5 + 32, "K" => c + 273.15, _ => c };
    }

    private static string Format(double v, CultureInfo culture, bool grouping = true)
    {
        var r = Math.Round(v, 6);
        if (Math.Abs(r) >= 1e15 || (r != 0 && Math.Abs(r) < 1e-6))
        {
            return v.ToString("G10", culture);
        }

        return r.ToString(grouping ? "#,0.######" : "0.######", culture);
    }

    [GeneratedRegex(@"\d{1,3}(,\d{3})+(?!\d)")]
    private static partial Regex ThousandsRegex();

    [GeneratedRegex(@"^\s*(?<n>-?(?:\d{1,3}(?:,\d{3})+|\d+)?(?:\.\d+)?)\s*(?<rest>[^\d].*)$")]
    private static partial Regex NumberRegex();
}
