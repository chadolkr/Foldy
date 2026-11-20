using System.Runtime.InteropServices;
using FlaUI.Core.Definitions;

namespace Foldy.UiTests;

/// <summary>디자인 원칙: 누르는 곳은 넉넉하게 (마우스를 여러 번 맞추지 않게). 보이는 버튼은 모두 28×28(96dpi 기준) 이상.</summary>
[Collection("ui")]
public sealed partial class HitTargetTests
{
    private const double Min = 28;

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [Fact]
    public void Every_visible_button_is_big_enough_to_hit()
    {
        using var app = FoldyApp.Start();
        Thread.Sleep(1500);
        var scale = GetDpiForSystem() / 96.0; // UI Automation 좌표는 화면 픽셀
        var small = app.Main.FindAllDescendants(app.By.ByControlType(ControlType.Button))
            .Where(b => !b.IsOffscreen && b.Properties.ClassName.ValueOrDefault is not ("RepeatButton" or "Thumb") && b.Parent?.ControlType != ControlType.ScrollBar)
            .Select(b => (b.Name, R: b.BoundingRectangle, Cls: b.Properties.ClassName.ValueOrDefault, Id: b.Properties.AutomationId.ValueOrDefault))
            .Where(x => x.R.Width > 0 && x.R.Height > 0 && (x.R.Width / scale < Min - 0.5 || x.R.Height / scale < Min - 0.5))
            .Select(x => $"'{x.Name}' [{x.Cls}/{x.Id}] ({x.R.Width / scale:0}×{x.R.Height / scale:0})")
            .Distinct()
            .ToList();
        small.Should().BeEmpty("작은 버튼: " + string.Join(", ", small));
    }
}
