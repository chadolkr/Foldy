using System.Text.Json;
using Foldy.Presentation.Persistence;
using Foldy.Presentation.Settings;

namespace Foldy.Settings.Tests;

public class GitSettingsTests
{
    [Fact]
    public void Defaults_bar_on_auto_open_off()
    {
        var s = new AppSettings();
        s.ShowGitBar.Should().BeTrue();
        s.AutoOpenGitView.Should().BeFalse();
        var vm = new SettingsViewModel(s);
        vm.ShowGitBar.Should().BeTrue();
        vm.AutoOpenGitView.Should().BeFalse();
        vm.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Editing_marks_dirty_and_builds_values()
    {
        var vm = new SettingsViewModel(new AppSettings());
        vm.ShowGitBar = false;
        vm.AutoOpenGitView = true;
        vm.IsDirty.Should().BeTrue();
        var b = vm.Build();
        b.ShowGitBar.Should().BeFalse();
        b.AutoOpenGitView.Should().BeTrue();
        vm.Cancel();
        vm.ShowGitBar.Should().BeTrue();
        vm.AutoOpenGitView.Should().BeFalse();
    }

    [Fact]
    public void Old_settings_json_without_git_fields_loads_with_defaults()
    {
        var s = JsonSerializer.Deserialize<AppSettings>("""{"theme":"dark"}""", JsonFileStore.Options)!;
        s.ShowGitBar.Should().BeTrue();
        s.AutoOpenGitView.Should().BeFalse();
        var back = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(new AppSettings { ShowGitBar = false, AutoOpenGitView = true }, JsonFileStore.Options), JsonFileStore.Options)!;
        back.ShowGitBar.Should().BeFalse();
        back.AutoOpenGitView.Should().BeTrue();
    }
}
