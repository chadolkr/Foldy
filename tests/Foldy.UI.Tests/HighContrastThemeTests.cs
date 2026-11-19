using System.IO;
using System.Windows;
using System.Windows.Media;
using Foldy.UI.Theming;

namespace Foldy.UI.Tests;

public class HighContrastThemeTests
{
    private static string LightKeys()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foldy.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", "Foldy.UI", "Themes", "Modern.Light.xaml");
    }

    [Fact]
    public void Overrides_cover_every_color_token_of_the_modern_theme()
    {
        var doc = System.Xml.Linq.XDocument.Load(LightKeys());
        var keys = doc.Descendants().Where(e => e.Name.LocalName == "SolidColorBrush")
            .Select(e => (string?)e.Attribute(System.Xml.Linq.XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")))
            .Where(k => k is not null).Select(k => k!).ToList();
        keys.Should().NotBeEmpty();

        Sta.Run(() =>
        {
            var rd = HighContrastTheme.CreateOverrides();
            foreach (var k in keys)
            {
                rd.Contains(k).Should().BeTrue($"고대비 사전에 {k} 가 있어야 한다");
                rd[k].Should().BeOfType<SolidColorBrush>();
            }
        });
    }

    [Fact]
    public void Text_pairs_use_system_color_pairs()
    {
        Sta.Run(() =>
        {
            var rd = HighContrastTheme.CreateOverrides();
            ((SolidColorBrush)rd["Brush.Text"]).Color.Should().Be(SystemColors.WindowTextColor);
            ((SolidColorBrush)rd["Brush.Window"]).Color.Should().Be(SystemColors.WindowColor);
            ((SolidColorBrush)rd["Brush.Selection"]).Color.Should().Be(SystemColors.HighlightColor);
            ((SolidColorBrush)rd["Brush.SelectionText"]).Color.Should().Be(SystemColors.HighlightTextColor);
            ((SolidColorBrush)rd["Brush.TextDisabled"]).Color.Should().Be(SystemColors.GrayTextColor);
        });
    }

    [Fact]
    public void Accent_focus_ring_is_corrected_to_3_to_1()
    {
        Sta.Run(() =>
        {
            // 연한 노랑 강조색은 흰 배경에서 포커스 링으로 쓸 수 없다 -> 어둡게 보정
            var yellow = Color.FromRgb(0xFF, 0xD2, 0x00);
            var rd = AccentColor.CreateOverrides(yellow, dark: false);
            var focus = ((SolidColorBrush)rd["Brush.Focus"]).Color;
            AccentColor.Contrast(focus, Colors.White).Should().BeGreaterThanOrEqualTo(3.0);
            var onAccent = ((SolidColorBrush)rd["Brush.AccentText"]).Color;
            AccentColor.Contrast(onAccent, yellow).Should().BeGreaterThanOrEqualTo(4.5);
        });
    }
}