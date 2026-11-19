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
            return Make("downloads", $"다운로드에 파일이 {files:N0}개 쌓였어요. 이미 설치한 설치 파일이나 중복 파일을 골라 드릴까요?", "다운로드 정리", "downloads", dl);
        }

        if (Same(folder, CleanupPaths.Desktop) && files >= 50)
        {
            return Make("desktop", $"바탕화면에 파일이 {files:N0}개 있어요. 버리지 않고 종류별로 보관해 드릴까요?", "바탕화면 정리", "desktop", folder);
        }

        var logs = list.Count(i => !i.IsFolder && i.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
        if (logs >= 20)
        {
            return Make("logs", $"이 폴더에 로그 파일이 {logs:N0}개 있어요. 오래된 로그를 달마다 zip으로 묶어 공간을 줄일까요?", "로그 정리", "logs", Path.GetPathRoot(folder)!);
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
