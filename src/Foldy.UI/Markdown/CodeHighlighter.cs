namespace Foldy.UI.Markdown;

public enum CodeTokenKind { Text, Keyword, StringLiteral, Comment, Number }

public readonly record struct CodeToken(CodeTokenKind Kind, string Text);

/// <summary>
/// 코드 블록용 간단한 문법 강조 (설계서 5.9: 주요 언어 몇 개만). 정규식 없이 한 번 훑는 토큰기라
/// 큰 블록도 빠르다. 모르는 언어는 강조 없이 글자 그대로.
/// </summary>
public static class CodeHighlighter
{
    private sealed record Lang(
        HashSet<string> Keywords,
        string[] LineComments,
        (string Open, string Close)? BlockComment,
        string Quotes,
        bool CaseInsensitive = false,
        bool Markup = false);

    private static readonly Dictionary<string, Lang> Languages = Build();

    public static bool IsSupported(string? language) => language is not null && Languages.ContainsKey(language);

    public static IReadOnlyList<CodeToken> Tokenize(string code, string? language)
    {
        if (language is null || !Languages.TryGetValue(language, out var lang))
        {
            return [new CodeToken(CodeTokenKind.Text, code)];
        }

        return lang.Markup ? TokenizeMarkup(code) : TokenizeCode(code, lang);
    }

    private static List<CodeToken> TokenizeCode(string s, Lang lang)
    {
        var tokens = new List<CodeToken>();
        var textStart = 0;
        var i = 0;

        void Flush(int upTo)
        {
            if (upTo > textStart)
            {
                tokens.Add(new CodeToken(CodeTokenKind.Text, s[textStart..upTo]));
            }
        }

        void Emit(CodeTokenKind kind, int start, int end)
        {
            Flush(start);
            tokens.Add(new CodeToken(kind, s[start..end]));
            textStart = end;
        }

        while (i < s.Length)
        {
            var c = s[i];

            // 블록 주석
            if (lang.BlockComment is { } bc && string.CompareOrdinal(s, i, bc.Open, 0, bc.Open.Length) == 0)
            {
                var end = s.IndexOf(bc.Close, i + bc.Open.Length, StringComparison.Ordinal);
                end = end < 0 ? s.Length : end + bc.Close.Length;
                Emit(CodeTokenKind.Comment, i, end);
                i = end;
                continue;
            }

            // 줄 주석
            var isLineComment = false;
            foreach (var lc in lang.LineComments)
            {
                if (string.CompareOrdinal(s, i, lc, 0, lc.Length) == 0)
                {
                    isLineComment = true;
                    break;
                }
            }

            if (isLineComment)
            {
                var end = s.IndexOf('\n', i);
                end = end < 0 ? s.Length : end;
                Emit(CodeTokenKind.Comment, i, end);
                i = end;
                continue;
            }

            // 문자열 (같은 따옴표로 닫힘, 백슬래시 이스케이프)
            if (lang.Quotes.Contains(c, StringComparison.Ordinal))
            {
                var j = i + 1;
                while (j < s.Length && s[j] != c && s[j] != '\n')
                {
                    j += s[j] == '\\' ? 2 : 1;
                }

                var end = Math.Min(s.Length, j + 1);
                Emit(CodeTokenKind.StringLiteral, i, end);
                i = end;
                continue;
            }

            // 숫자 (식별자 중간이 아닐 때)
            if (char.IsAsciiDigit(c) && (i == 0 || !IsIdent(s[i - 1])))
            {
                var j = i + 1;
                while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] is '.' or '_'))
                {
                    j++;
                }

                Emit(CodeTokenKind.Number, i, j);
                i = j;
                continue;
            }

            // 식별자 → 키워드 여부
            if (IsIdentStart(c))
            {
                var j = i + 1;
                while (j < s.Length && IsIdent(s[j]))
                {
                    j++;
                }

                var word = s[i..j];
                if (lang.Keywords.Contains(lang.CaseInsensitive ? word.ToLowerInvariant() : word))
                {
                    Emit(CodeTokenKind.Keyword, i, j);
                }

                i = j;
                continue;
            }

            i++;
        }

        Flush(s.Length);
        return tokens;
    }

    /// <summary>XML/HTML: 태그 이름은 Keyword, 속성값은 String, 주석은 Comment.</summary>
    private static List<CodeToken> TokenizeMarkup(string s)
    {
        var tokens = new List<CodeToken>();
        var textStart = 0;
        var i = 0;

        void Emit(CodeTokenKind kind, int start, int end)
        {
            if (start > textStart)
            {
                tokens.Add(new CodeToken(CodeTokenKind.Text, s[textStart..start]));
            }

            tokens.Add(new CodeToken(kind, s[start..end]));
            textStart = end;
        }

        while (i < s.Length)
        {
            if (string.CompareOrdinal(s, i, "<!--", 0, 4) == 0)
            {
                var end = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
                end = end < 0 ? s.Length : end + 3;
                Emit(CodeTokenKind.Comment, i, end);
                i = end;
                continue;
            }

            if (s[i] == '<')
            {
                var j = i + 1;
                if (j < s.Length && s[j] is '/' or '?' or '!')
                {
                    j++;
                }

                while (j < s.Length && (IsIdent(s[j]) || s[j] is ':' or '-' or '.'))
                {
                    j++;
                }

                Emit(CodeTokenKind.Keyword, i, j);
                i = j;

                // 태그 안: 속성값 문자열
                while (i < s.Length && s[i] != '>')
                {
                    if (s[i] is '"' or '\'')
                    {
                        var q = s[i];
                        var e = s.IndexOf(q, i + 1);
                        e = e < 0 ? s.Length : e + 1;
                        Emit(CodeTokenKind.StringLiteral, i, e);
                        i = e;
                    }
                    else
                    {
                        i++;
                    }
                }

                continue;
            }

            i++;
        }

        if (s.Length > textStart)
        {
            tokens.Add(new CodeToken(CodeTokenKind.Text, s[textStart..]));
        }

        return tokens;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c is '_' or '$' or '@';

    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    private static Dictionary<string, Lang> Build()
    {
        static HashSet<string> K(string words, bool ci = false) =>
            new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), ci ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var csharp = new Lang(
            K("abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach get goto if implicit in init int interface internal is lock long namespace new null object operator out override params partial private protected public readonly record ref required return sbyte sealed set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile when where while with yield"),
            ["//"], ("/*", "*/"), "\"'");
        var js = new Lang(
            K("async await break case catch class const continue debugger default delete do else enum export extends false finally for from function if implements import in instanceof interface let new null of private protected public return static super switch this throw true try type typeof undefined var void while with yield readonly"),
            ["//"], ("/*", "*/"), "\"'`");
        var python = new Lang(
            K("and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield match case self"),
            ["#"], null, "\"'");
        var rust = new Lang(
            K("as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while i8 i16 i32 i64 u8 u16 u32 u64 usize isize f32 f64 bool str String Option Some None Result Ok Err Vec"),
            ["//"], ("/*", "*/"), "\"");
        var json = new Lang(K("true false null"), [], null, "\"");
        var shell = new Lang(
            K("if then else elif fi for while do done case esac in function return export local echo exit"),
            ["#"], null, "\"'");
        var powershell = new Lang(
            K("begin break catch class continue data do dynamicparam else elseif end enum exit filter finally for foreach function if in param process return switch throw trap try until using while write-host write-output", ci: true),
            ["#"], ("<#", "#>"), "\"'", CaseInsensitive: true);
        var sql = new Lang(
            K("select from where and or not insert into values update set delete create table index view drop alter join left right inner outer on group by order having limit as distinct null is in like between case when then else end primary key foreign references", ci: true),
            ["--"], ("/*", "*/"), "'\"", CaseInsensitive: true);
        var markup = new Lang(K(""), [], null, "", Markup: true);

        var map = new Dictionary<string, Lang>(StringComparer.OrdinalIgnoreCase);
        void Add(Lang l, params string[] names)
        {
            foreach (var n in names)
            {
                map[n] = l;
            }
        }

        Add(csharp, "cs", "csharp", "c#");
        Add(js, "js", "javascript", "ts", "typescript", "jsx", "tsx");
        Add(python, "py", "python");
        Add(rust, "rs", "rust");
        Add(json, "json", "jsonc");
        Add(shell, "sh", "bash", "shell", "zsh");
        Add(powershell, "ps", "ps1", "powershell", "pwsh");
        Add(sql, "sql");
        Add(markup, "xml", "html", "xaml", "csproj", "svg");
        return map;
    }
}
