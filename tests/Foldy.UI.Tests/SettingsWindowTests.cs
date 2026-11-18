using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Foldy.Presentation.Persistence;
using Foldy.Presentation.Settings;
using Foldy.UI.Settings;
using Foldy.UI.Theming;

namespace Foldy.UI.Tests;

public class SettingsWindowTests
{
    private static readonly object AppLock = new();
    private static bool _appStarted;

    /// <summary>pack://application 주소가 풀리려면 Application이 하나 있어야 한다. 전용 STA 스레드에 한 번만 만든다.</summary>
    private static void EnsureApplication()
    {
        lock (AppLock)
        {
            if (_appStarted)
            {
                return;
            }

            using var ready = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ready.Set();
                System.Windows.Threading.Dispatcher.Run();
            })
            { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            ready.Wait();
            _appStarted = true;
        }
    }

    private static void AddTheme(ResourceDictionary target, string theme)
    {
        EnsureApplication();
        target.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Foldy.UI;component/Themes/{theme}.xaml") });
        target.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Foldy.UI;component/Themes/Modern.Controls.xaml") });
    }

    [Fact]
    public void Pretendard_is_embedded_and_resolves_for_Korean()
    {
        Sta.Run(() =>
        {
            var rd = new ResourceDictionary();
            AddTheme(rd, "Modern.Light");
            var family = (FontFamily)rd["Font.UI"];

            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            typeface.TryGetGlyphTypeface(out var glyphs).Should().BeTrue();
            glyphs!.Win32FamilyNames.Values.Should().Contain(n => n.Contains("Pretendard", StringComparison.Ordinal));
            foreach (var ch in "가나다힣설정")
            {
                glyphs.CharacterToGlyphMap.ContainsKey(ch).Should().BeTrue($"'{ch}' 글리프");
            }
        });
    }

    [Fact]
    public void Settings_window_builds_binds_and_renders()
    {
        Sta.Run(() =>
        {
            var vm = new SettingsViewModel(new AppSettings(), windowsTerminalProfiles: ["PowerShell"]);
            var w = new SettingsWindow(vm);
            AddTheme(w.Resources, "Modern.Light");
            w.Width = 880;
            w.Height = 620;
            var content = (FrameworkElement)w.Content;
            void Layout()
            {
                content.Measure(new Size(880, 620));
                content.Arrange(new Rect(0, 0, 880, 620));
                content.UpdateLayout();
            }

            Layout();

            if (Environment.GetEnvironmentVariable("FOLDY_SHOT_DIR") is { Length: > 0 } dir)
            {
                for (var page = 0; page < 7; page++)
                {
                    w.SelectCategory(page);
                    Layout();
                    var bmp = new RenderTargetBitmap(880, 620, 96, 96, PixelFormats.Pbgra32);
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle((Brush)w.FindResource("Brush.Surface"), null, new Rect(0, 0, 880, 620));
                    }

                    bmp.Render(dv);
                    bmp.Render(content);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    Directory.CreateDirectory(dir);
                    using var fs = File.Create(Path.Combine(dir, $"settings-{page}.png"));
                    enc.Save(fs);
                }
            }

            w.SelectCategory(6);
            Layout();
            ((FrameworkElement)w.FindName("PageKeys")).Visibility.Should().Be(Visibility.Visible);
            ((FrameworkElement)w.FindName("PageGeneral")).Visibility.Should().Be(Visibility.Collapsed);
            w.DataContext.Should().BeSameAs(vm);
        });
    }

    [Theory]
    [InlineData("FFB16300", 0x00, 0x63, 0xB1)] // 0xAABBGGRR
    public void Accent_abgr_converts_to_rgb(string hex, int r, int g, int b)
    {
        var c = AccentColor.FromAbgr(uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        (c.R, c.G, c.B).Should().Be(((byte)r, (byte)g, (byte)b));
    }

    [Fact]
    public void Accent_overrides_replace_the_four_tokens()
    {
        Sta.Run(() =>
        {
            var rd = AccentColor.CreateOverrides(Color.FromRgb(0, 0x63, 0xB1), dark: false);
            foreach (var key in new[] { "Brush.Accent", "Brush.AccentText", "Brush.Focus", "Brush.Progress", "Brush.Selection" })
            {
                rd[key].Should().BeOfType<SolidColorBrush>();
            }

            ((SolidColorBrush)rd["Brush.Accent"]).Color.Should().Be(Color.FromRgb(0, 0x63, 0xB1));
            ((SolidColorBrush)rd["Brush.AccentText"]).Color.Should().Be(Colors.White);
        });
    }
}
