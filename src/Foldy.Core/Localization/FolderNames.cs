namespace Foldy.Core.Localization;

/// <summary>
/// Foldy가 만드는 폴더의 이름 (DECISIONS 2026-11-22): 만드는 그 시점의 앱 언어로 붙인다.
/// 같은 곳에 예전에 만든 한국어 이름 폴더가 이미 있으면 그 폴더를 계속 쓴다 (언어가 바뀌어도 폴더가 둘로 갈라지지 않게, 이미 만든 폴더는 바꾸지 않는다).
/// </summary>
public static class FolderNames
{
    /// <summary><paramref name="parent"/> 안에 <paramref name="korean"/> 폴더가 이미 있으면 그 이름, 아니면 <paramref name="localized"/>.</summary>
    public static string Pick(string? parent, string korean, string localized)
    {
        ArgumentNullException.ThrowIfNull(korean);
        ArgumentNullException.ThrowIfNull(localized);
        if (parent is null || string.Equals(korean, localized, StringComparison.Ordinal))
        {
            return localized;
        }

        try
        {
            return Directory.Exists(Path.Combine(parent, korean)) && !Directory.Exists(Path.Combine(parent, localized)) ? korean : localized;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return localized;
        }
    }
}
