using System.Globalization;

namespace Foldy.Core.QuickSearch;

/// <summary>
/// 무엇이든 찾기 창을 끌어서 옮긴 자리 (1.14.2, 2026-11-26 사용자: "Ctrl + space 화면이 고정되어 있는데 옮길 수 있게").
/// 옮긴 자리는 파일 한 줄("왼쪽 위")로 남겨 다음에도 그 자리에 뜬다. 모니터를 떼어 내 그 자리가 화면 밖이면 남긴 자리를 쓰지 않고 가운데에 띄운다.
/// 단위는 WPF 화면 단위(DIP)다.
/// </summary>
public static class QuickSearchPlace
{
    /// <summary>%LOCALAPPDATA%\Foldy 안의 파일 이름.</summary>
    public const string FileName = "quick-search-place.txt";

    /// <summary>창 위쪽(입력 칸)이 화면에 이만큼은 보여야 남긴 자리를 쓴다 (끌어서 다시 옮길 수 있게).</summary>
    public const double MinVisibleWidth = 200;

    /// <summary>창 위 끝 아래로 이만큼은 화면 안이어야 한다 (입력 칸 높이쯤).</summary>
    public const double MinVisibleHeight = 80;

    /// <summary>"왼쪽 위" 한 줄. 읽을 수 없으면 null.</summary>
    public static (double Left, double Top)? Parse(string? text)
    {
        var parts = (text ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var top)
            || !double.IsFinite(left) || !double.IsFinite(top))
        {
            return null;
        }

        return (left, top);
    }

    public static string Format(double left, double top) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(left)} {Math.Round(top)}");

    /// <summary>
    /// 남긴 자리가 지금 화면(모든 모니터를 합친 넓이)에 충분히 보이면 그 자리, 아니면 null (가운데에 띄운다).
    /// </summary>
    /// <param name="saved">남긴 왼쪽 위.</param>
    /// <param name="width">창 너비.</param>
    /// <param name="screen">모든 모니터를 합친 넓이 (왼쪽, 위, 너비, 높이).</param>
    public static (double Left, double Top)? Fit((double Left, double Top)? saved, double width, (double Left, double Top, double Width, double Height) screen)
    {
        if (saved is not { } s || width <= 0 || screen.Width <= 0 || screen.Height <= 0)
        {
            return null;
        }

        var seen = Math.Min(s.Left + width, screen.Left + screen.Width) - Math.Max(s.Left, screen.Left);
        var topOk = s.Top >= screen.Top && s.Top <= screen.Top + screen.Height - MinVisibleHeight;
        return seen >= Math.Min(MinVisibleWidth, width) && topOk ? s : null;
    }

    public static (double Left, double Top)? Load(string dataDir)
    {
        try
        {
            var path = Path.Combine(dataDir, FileName);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string dataDir, double left, double top)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, FileName), Format(left, top));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 자리를 못 남겨도 찾기는 된다: 다음에 가운데에 뜰 뿐
        }
    }
}
