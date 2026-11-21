using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Foldy.Core.Localization;

/// <summary>지원하는 언어 하나.</summary>
/// <param name="Code">언어 코드 (en, ko, es, ja, zh-Hans).</param>
/// <param name="NativeName">그 언어로 쓴 이름 (설정 목록에 보인다).</param>
public sealed record LanguageInfo(string Code, string NativeName);

/// <summary>
/// 다국어 글 (2026-11-21 사용자 결정: 한국어·영어·스페인어·일본어·중국어 간체).
/// 언어마다 JSON 파일 하나(assets/i18n/코드.json, 키 → 글)이고, 파일 하나를 더하면 언어가 늘어난다.
/// 기본은 Windows 표시 언어, 지원하지 않으면 영어. 없는 키는 영어, 그것도 없으면 키 그대로(시험이 막는다).
/// 문장 조각을 잇지 않고 "{0}개"처럼 문장 하나로 쓴다 (디자인 원칙 2-1).
/// </summary>
public static class Loc
{
    public const string Fallback = "en";

    /// <summary>지원 언어 (설정 목록 순서).</summary>
    public static readonly IReadOnlyList<LanguageInfo> Languages =
    [
        new("ko", "한국어"),
        new("en", "English"),
        new("es", "Español"),
        new("ja", "日本語"),
        new("zh-Hans", "简体中文"),
    ];

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>지금 쓰는 언어 코드.</summary>
    public static string Current { get; private set; } = Fallback;

    /// <summary>언어를 바꾼다. "auto"·빈 값이면 Windows 표시 언어(<paramref name="ui"/>)를 따른다. 실제로 고른 코드를 돌려준다.</summary>
    public static string Use(string? setting, CultureInfo? ui = null)
    {
        Current = Resolve(setting, ui ?? CultureInfo.CurrentUICulture);
        return Current;
    }

    /// <summary>설정 값과 Windows 언어로 쓸 언어를 고른다 (시험에서도 쓴다).</summary>
    public static string Resolve(string? setting, CultureInfo ui)
    {
        if (!string.IsNullOrWhiteSpace(setting) && !string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase)
            && Languages.Any(l => string.Equals(l.Code, setting, StringComparison.OrdinalIgnoreCase)))
        {
            return Languages.First(l => string.Equals(l.Code, setting, StringComparison.OrdinalIgnoreCase)).Code;
        }

        for (var c = ui; c is not null && c.Name.Length > 0; c = c.Parent)
        {
            var name = c.Name;
            // 중국어: 간체 문화권(zh-CN, zh-SG, zh-Hans)은 간체로. 번체는 아직 없어 영어로.
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return name is "zh-CN" or "zh-SG" or "zh-Hans" or "zh" || name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : Fallback;
            }

            if (Languages.FirstOrDefault(l => string.Equals(l.Code, name, StringComparison.OrdinalIgnoreCase)) is { } hit)
            {
                return hit.Code;
            }

            if (ReferenceEquals(c, c.Parent))
            {
                break;
            }
        }

        return Fallback;
    }

    /// <summary>글 하나.</summary>
    public static string T(string key)
    {
        if (Table(Current).TryGetValue(key, out var s) || Table(Fallback).TryGetValue(key, out s))
        {
            return s;
        }

        return key;
    }

    /// <summary>값을 넣은 글 ("파일 {0}개"). 숫자·날짜는 지금 지역 설정으로.</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>언어 하나의 글 전부 (시험·검수용).</summary>
    public static IReadOnlyDictionary<string, string> Table(string code)
    {
        lock (Gate)
        {
            if (!Tables.TryGetValue(code, out var t))
            {
                Tables[code] = t = Load(code);
            }

            return t;
        }
    }

    private static Dictionary<string, string> Load(string code)
    {
        var asm = typeof(Loc).Assembly;
        using var s = asm.GetManifestResourceStream("Foldy.I18n." + code + ".json");
        if (s is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
