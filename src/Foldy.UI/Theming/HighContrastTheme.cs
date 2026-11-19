using System.Windows;
using System.Windows.Media;

namespace Foldy.UI.Theming;

/// <summary>
/// Windows 고대비 모드(설정 &gt; 접근성 &gt; 대비 테마)용 대체 색. 켜져 있으면 모든 Foldy 색 토큰을
/// 시스템 색(SystemColors)으로 덮어써서 사용자가 고른 대비 테마를 그대로 따른다.
/// 병합 사전 맨 뒤에 두면 DynamicResource로 토큰을 쓰는 화면이 바로 따라온다 (AccentColor와 같은 방식).
/// </summary>
public static class HighContrastTheme
{
    /// <summary>Windows 고대비가 켜져 있는가.</summary>
    public static bool IsActive => SystemParameters.HighContrast;

    /// <summary>
    /// 모든 Brush.* 토큰을 시스템 색으로 대응시킨다.
    /// 글자는 WindowText/Window 쌍, 선택은 Highlight/HighlightText 쌍, 비활성은 GrayText, 포커스·강조는 HotTrack.
    /// 오류·완료 색은 고대비에서 색에 의미를 싣지 않으므로 WindowText로 두고 글자(문구)로 구분한다.
    /// </summary>
    public static ResourceDictionary CreateOverrides()
    {
        var window = SystemColors.WindowColor;
        var text = SystemColors.WindowTextColor;
        var face = SystemColors.ControlColor;
        var faceText = SystemColors.ControlTextColor;
        var highlight = SystemColors.HighlightColor;
        var highlightText = SystemColors.HighlightTextColor;
        var hot = SystemColors.HotTrackColor;
        var gray = SystemColors.GrayTextColor;

        var map = new Dictionary<string, Color>
        {
            ["Brush.Window"] = window,
            ["Brush.Surface"] = face,
            ["Brush.SurfaceAlt"] = window,
            ["Brush.Border"] = text,
            ["Brush.BorderStrong"] = text,
            ["Brush.Text"] = text,
            ["Brush.TextSecondary"] = text,
            ["Brush.TextDisabled"] = gray,
            ["Brush.Hover"] = face,
            ["Brush.Pressed"] = face,
            ["Brush.Selection"] = highlight,
            ["Brush.SelectionText"] = highlightText,
            ["Brush.Accent"] = highlight,
            ["Brush.AccentText"] = highlightText,
            ["Brush.Focus"] = hot,
            ["Brush.Progress"] = highlight,
            ["Brush.ScrollThumb"] = faceText,
            ["Brush.ScrollThumbHover"] = hot,
            ["Brush.Error"] = text,
            ["Brush.Done"] = text,
        };
        for (var i = 0; i < 8; i++)
        {
            map["Brush.Tab" + i] = face;
        }

        var rd = new ResourceDictionary();
        foreach (var (key, color) in map)
        {
            var b = new SolidColorBrush(color);
            b.Freeze();
            rd[key] = b;
        }

        return rd;
    }
}
