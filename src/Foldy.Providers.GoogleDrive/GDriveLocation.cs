namespace Foldy.Providers.GoogleDrive;

/// <summary>
/// <c>gdrive://계정/폴더/파일</c> 형태의 위치. 계정은 Google 계정 이메일(소문자), 경로 조각은 Drive에 보이는 이름이다.
/// 루트(<c>gdrive://계정/</c>)는 "내 드라이브"다.
/// <para>
/// Drive 이름에는 '/'가 들어갈 수 있어 화면·경로에서는 '∕'(U+2215)로 바꿔 쓴다(<see cref="EscapeName"/>).
/// 같은 폴더에 같은 이름이 여러 개면 목록에서 "이름 (2).확장자"처럼 구분한다(<see cref="GoogleDriveProvider"/>).
/// </para>
/// </summary>
public sealed record GDriveLocation(string Account, IReadOnlyList<string> Segments)
{
    public const string Scheme = "gdrive://";

    /// <summary>경로 조각 안에서 '/' 대신 쓰는 문자.</summary>
    public const char SlashSubstitute = '∕';

    public bool IsRoot => Segments.Count == 0;

    public string Name => IsRoot ? Account : Segments[^1];

    public GDriveLocation? Parent => IsRoot ? null : new GDriveLocation(Account, Segments.Take(Segments.Count - 1).ToArray());

    public GDriveLocation Combine(string name) => new(Account, [.. Segments, name]);

    /// <summary>경로 문자열(계정 소문자, 끝 / 없음, 루트는 끝 /).</summary>
    public override string ToString() => Scheme + Account + "/" + string.Join('/', Segments);

    /// <summary>캐시 키(대소문자 구분: Drive 이름은 대소문자를 구분한다).</summary>
    public string Key => ToString();

    public static bool IsGDrive(string? text) => text?.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) == true;

    public static GDriveLocation Root(string account) => new(NormalizeAccount(account), []);

    public static bool TryParse(string? text, out GDriveLocation location)
    {
        location = null!;
        if (!IsGDrive(text))
        {
            return false;
        }

        var rest = text![Scheme.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var account = slash < 0 ? rest : rest[..slash];
        if (account.Length == 0 || account.IndexOfAny(['\\', ':', '?', '#', ' ']) >= 0)
        {
            return false;
        }

        var parts = slash < 0 ? [] : rest[(slash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (p is "." or "..")
            {
                return false;
            }
        }

        location = new GDriveLocation(NormalizeAccount(account), parts);
        return true;
    }

    public static GDriveLocation Parse(string text) =>
        TryParse(text, out var l) ? l : throw new FormatException($"Google Drive 경로가 아닙니다: {text}");

    public static string NormalizeAccount(string account) => account.Trim().ToLowerInvariant();

    /// <summary>Drive 이름 → 경로 조각('/'를 '∕'로).</summary>
    public static string EscapeName(string driveName) => driveName.Replace('/', SlashSubstitute);

    public bool Equals(GDriveLocation? other) =>
        other is not null && Account == other.Account && Segments.SequenceEqual(other.Segments, StringComparer.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);
}
