using System.IO;
using Foldy.Presentation.Files;

namespace Foldy.App.Services;

public sealed record ContextHint(string Key, string Button, CleanupFinding Finding);

/// <summary>
/// 지금 폴더에 맞는 기능 제안 (디자인 원칙: 필요할 때 그 자리에서 한 줄). 이미 불러온 목록만 보고 디스크를 더 읽지 않는다.
/// 닫은 제안은 hints-dismissed.txt에 남겨 그 폴더에서는 다시 보이지 않는다.
/// </summary>
public static class ContextHints
{
    private static readonly HashSet<string> Dismissed = Load();

    private static string StorePath => Path.Combine(App.DataDir, "hints-dismissed.txt");

    public static ContextHint? For(string folder, IEnumerable<FileItem> items)
    {
        if (folder.Length < 3 || folder[1] != ':')
        {
            return null;
        }

        var list = items as IReadOnlyCollection<FileItem> ?? items.ToList();
        var files = list.Count(i => !i.IsFolder);
        var norm = folder.TrimEnd('\\');
        ContextHint? Make(string rule, string text, string button, string tool, string target)
        {
            var key = rule + "|" + norm.ToUpperInvariant();
            return Dismissed.Contains(key) ? null : new ContextHint(key, button, new CleanupFinding(text, tool, target));
        }

        if (CleanupPaths.Downloads() is { } dl && Same(folder, dl) && files >= 40)
        {
            return Make("downloads", Foldy.Core.Localization.Loc.F("hint.downloads", files), Foldy.Core.Localization.Loc.T("sw.c.downloads"), "downloads", dl);
        }

        if (Same(folder, CleanupPaths.Desktop) && files >= 50)
        {
            return Make("desktop", Foldy.Core.Localization.Loc.F("hint.desktop", files), Foldy.Core.Localization.Loc.T("sw.c.desktop"), "desktop", folder);
        }

        var logs = list.Count(i => !i.IsFolder && i.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
        if (logs >= 20)
        {
            return Make("logs", Foldy.Core.Localization.Loc.F("hint.logs", logs), Foldy.Core.Localization.Loc.T("sw.logs.title"), "logs", Path.GetPathRoot(folder)!);
        }

        return null;
    }

    public static void Dismiss(string key)
    {
        if (Dismissed.Add(key))
        {
            try
            {
                File.AppendAllLines(StorePath, [key]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> Load()
    {
        try
        {
            return File.Exists(StorePath) ? new HashSet<string>(File.ReadAllLines(StorePath), StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
