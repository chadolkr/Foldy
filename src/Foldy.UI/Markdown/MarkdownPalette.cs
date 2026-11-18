using System.Windows;
using System.Windows.Media;

namespace Foldy.UI.Markdown;

/// <summary>
/// Markdown 전용 보조 색 (코드 배경, 문법 강조). 공용 토큰(Brush.*)에 없는 색만 여기서 다크/라이트 두 벌로 둔다.
/// FlowDocument.Resources에 넣고, 테마가 바뀌면 Apply를 다시 불러 값만 바꾼다 → DynamicResource가 즉시 갱신.
/// 레트로는 밝은 바탕이라 라이트 벌을 쓴다.
/// </summary>
public static class MarkdownPalette
{
    public const string CodeBackground = "Md.Code.Background";
    public const string Keyword = "Md.Syntax.Keyword";
    public const string StringLiteral = "Md.Syntax.String";
    public const string Comment = "Md.Syntax.Comment";
    public const string Number = "Md.Syntax.Number";
    public const string Mark = "Md.Mark";

    // 반투명이라 어떤 바탕(모던 흰색, 레트로 흰색, 다크 회색) 위에서도 살짝 구분된다.
    private static readonly SolidColorBrush DarkCode = Frozen(0x16, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush LightCode = Frozen(0x0F, 0x00, 0x00, 0x00);
    private static readonly SolidColorBrush DarkKeyword = Frozen(0xFF, 0x56, 0x9C, 0xD6);
    private static readonly SolidColorBrush LightKeyword = Frozen(0xFF, 0x00, 0x33, 0xB3);
    private static readonly SolidColorBrush DarkString = Frozen(0xFF, 0xCE, 0x91, 0x78);
    private static readonly SolidColorBrush LightString = Frozen(0xFF, 0xA3, 0x15, 0x15);
    private static readonly SolidColorBrush DarkComment = Frozen(0xFF, 0x6A, 0x99, 0x55);
    private static readonly SolidColorBrush LightComment = Frozen(0xFF, 0x00, 0x80, 0x00);
    private static readonly SolidColorBrush DarkNumber = Frozen(0xFF, 0xB5, 0xCE, 0xA8);
    private static readonly SolidColorBrush LightNumber = Frozen(0xFF, 0x09, 0x86, 0x58);
    private static readonly SolidColorBrush DarkMark = Frozen(0x55, 0xF5, 0x9A, 0x2E);
    private static readonly SolidColorBrush LightMark = Frozen(0x66, 0xFF, 0xE0, 0x66);

    public static void Apply(ResourceDictionary resources, bool dark)
    {
        ArgumentNullException.ThrowIfNull(resources);
        Set(resources, CodeBackground, dark ? DarkCode : LightCode);
        Set(resources, Keyword, dark ? DarkKeyword : LightKeyword);
        Set(resources, StringLiteral, dark ? DarkString : LightString);
        Set(resources, Comment, dark ? DarkComment : LightComment);
        Set(resources, Number, dark ? DarkNumber : LightNumber);
        Set(resources, Mark, dark ? DarkMark : LightMark);
    }

    public static string KeyFor(CodeTokenKind kind) => kind switch
    {
        CodeTokenKind.Keyword => Keyword,
        CodeTokenKind.StringLiteral => StringLiteral,
        CodeTokenKind.Comment => Comment,
        CodeTokenKind.Number => Number,
        _ => "Brush.Text",
    };

    private static void Set(ResourceDictionary resources, string key, object value)
    {
        if (!Equals(resources[key], value))
        {
            resources[key] = value;
        }
    }

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
