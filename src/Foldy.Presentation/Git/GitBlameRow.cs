using Foldy.Git;

namespace Foldy.Presentation.Git;

/// <summary>blame 화면의 한 줄. 같은 커밋 덩어리는 첫 줄에만 해시·작성자·날짜를 보이고, 덩어리마다 음영이 번갈아 바뀐다.</summary>
public sealed record GitBlameRow(
    int LineNo, string Hash, string ShortHash, string Author, string DateText, string Summary, string Text,
    bool IsBlockStart, bool Shaded, bool IsUncommitted)
{
    public string ShaShown => IsBlockStart ? (IsUncommitted ? "·······" : ShortHash) : "";

    public string AuthorShown => IsBlockStart ? (IsUncommitted ? Foldy.Core.Localization.Loc.T("git.uncommitted") : Author) : "";

    public string DateShown => IsBlockStart ? (IsUncommitted ? "" : DateText) : "";

    /// <summary>커밋 해시를 눌러 이동할 수 있는 줄인가.</summary>
    public bool CanNavigate => IsBlockStart && !IsUncommitted;

    public string Tooltip => IsUncommitted ? Foldy.Core.Localization.Loc.T("git.uncommittedChanges") : $"{ShortHash} · {Author} · {DateText}\n{Summary}";

    public string Display => Text.Contains('\t') ? Text.Replace("\t", "    ") : Text;

    public static GitBlameRow From(BlameLine l, DateTimeOffset now) => new(
        l.LineNo, l.Hash, l.ShortHash, l.Author, GitTime.Relative(l.Date, now), l.Summary, l.Text,
        l.IsBlockStart, l.BlockIndex % 2 == 1, l.IsUncommitted);
}

/// <summary>"3일 전" 같은 상대 시각 (한국어).</summary>
public static class GitTime
{
    public static string Relative(DateTimeOffset date, DateTimeOffset now)
    {
        var d = now - date;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d < TimeSpan.FromMinutes(1)) return Foldy.Core.Localization.Loc.T("git.justNow");
        if (d < TimeSpan.FromHours(1)) return Foldy.Core.Localization.Loc.F("git.minAgo", (int)d.TotalMinutes);
        if (d < TimeSpan.FromDays(1)) return Foldy.Core.Localization.Loc.F("git.hoursAgo", (int)d.TotalHours);
        if (d < TimeSpan.FromDays(30)) return Foldy.Core.Localization.Loc.F("git.daysAgo", (int)d.TotalDays);
        if (d < TimeSpan.FromDays(365)) return Foldy.Core.Localization.Loc.F("git.monthsAgo", (int)(d.TotalDays / 30));
        return Foldy.Core.Localization.Loc.F("git.yearsAgo", (int)(d.TotalDays / 365));
    }
}
