using System.Text.Json;
using Foldy.Presentation.Commands;
using Foldy.Presentation.Persistence;
using Foldy.Presentation.Settings;
using Foldy.Presentation.Update;

namespace Foldy.Settings.Tests;

public class KeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Shift+P", "Ctrl+Shift+P")]
    [InlineData("shift+ctrl+p", "Ctrl+Shift+P")]
    [InlineData("F5", "F5")]
    [InlineData("f12", "F12")]
    [InlineData("Ctrl+`", "Ctrl+`")]
    [InlineData("Control+Alt+Del", "Ctrl+Alt+Delete")]
    [InlineData("Esc", "Escape")]
    [InlineData("Win+E", "Win+E")]
    [InlineData("Ctrl+,", "Ctrl+,")]
    [InlineData("  Alt + Left ", "Alt+Left")]
    public void Parse_and_format_normalize(string input, string expected) =>
        KeyGesture.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Foo+A")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Shift")]
    [InlineData("A+B")]
    public void Invalid_gestures_are_rejected(string input) =>
        KeyGesture.TryParse(input, out _).Should().BeFalse();

    [Fact]
    public void Parsed_gesture_has_modifiers_and_key()
    {
        KeyGesture.TryParse("Ctrl+Alt+Shift+Win+Tab", out var g).Should().BeTrue();
        g.Modifiers.Should().Be(GestureModifiers.Ctrl | GestureModifiers.Alt | GestureModifiers.Shift | GestureModifiers.Win);
        g.Key.Should().Be("Tab");
    }
}

public class KeyMapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-keymap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    private static readonly CommandDefinition[] Cmds =
    [
        new("a", "A", "Ctrl+A"),
        new("b", "B", "F5"),
        new("c", "C", null),
    ];

    [Fact]
    public void Defaults_have_no_conflicts_and_resolve()
    {
        new KeyMap().FindConflicts().Should().BeEmpty();
        var map = new KeyMap();
        map.GetGesture("terminal.open").Should().Be("Ctrl+`");
        map.GetGesture("file.copy").Should().Be("F5");
        KeyGesture.TryParse("Ctrl+Shift+P", out var g).Should().BeTrue();
        map.Resolve(g).Should().Be("app.palette");
    }

    [Fact]
    public void Override_changes_gesture_and_default_value_removes_override()
    {
        var map = new KeyMap(Cmds);
        map.SetGesture("c", "ctrl+k").Should().BeTrue();
        map.GetGesture("c").Should().Be("Ctrl+K");
        map.IsOverridden("c").Should().BeTrue();

        map.SetGesture("b", "F5").Should().BeTrue(); // 기본값과 같음
        map.IsOverridden("b").Should().BeFalse();

        map.SetGesture("a", null).Should().BeTrue(); // 단축키 없음
        map.GetGesture("a").Should().BeNull();
        map.Overrides["a"].Should().BeEmpty();

        map.Reset("a");
        map.GetGesture("a").Should().Be("Ctrl+A");
    }

    [Fact]
    public void Invalid_gesture_or_unknown_command_is_rejected_without_change()
    {
        var map = new KeyMap(Cmds);
        map.SetGesture("a", "Ctrl+").Should().BeFalse();
        map.SetGesture("zzz", "F1").Should().BeFalse();
        map.Overrides.Should().BeEmpty();
    }

    [Fact]
    public void Conflicts_are_detected()
    {
        var map = new KeyMap(Cmds);
        map.ConflictsFor("c", "F5").Should().Equal("b");
        map.SetGesture("c", "F5");
        var conflicts = map.FindConflicts();
        conflicts.Should().HaveCount(1);
        conflicts[0].Gesture.ToString().Should().Be("F5");
        conflicts[0].CommandIds.Should().Equal("b", "c");
        map.ConflictsFor("c", "Ctrl+Q").Should().BeEmpty();
    }

    [Fact]
    public void Save_writes_only_overrides_and_Load_roundtrips()
    {
        var path = Path.Combine(_dir, "keymap.json");
        var map = new KeyMap(Cmds);
        map.SetGesture("b", "F6");
        map.SetGesture("a", "");
        map.Save(path);

        var json = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        json.Should().BeEquivalentTo(new Dictionary<string, string> { ["b"] = "F6", ["a"] = "" });

        var loaded = KeyMap.Load(path, Cmds);
        loaded.GetGesture("b").Should().Be("F6");
        loaded.GetGesture("a").Should().BeNull();
        loaded.GetGesture("c").Should().BeNull();
    }

    [Fact]
    public void Save_with_no_overrides_removes_file()
    {
        var path = Path.Combine(_dir, "keymap.json");
        var map = new KeyMap(Cmds);
        map.SetGesture("b", "F6");
        map.Save(path);
        map.Reset("b");
        map.Save(path);
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Load_ignores_missing_broken_and_bad_entries()
    {
        KeyMap.Load(Path.Combine(_dir, "none.json"), Cmds).Overrides.Should().BeEmpty();

        Directory.CreateDirectory(_dir);
        var broken = Path.Combine(_dir, "broken.json");
        File.WriteAllText(broken, "{ not json");
        KeyMap.Load(broken, Cmds).Overrides.Should().BeEmpty();

        var mixed = Path.Combine(_dir, "mixed.json");
        File.WriteAllText(mixed, """{ "unknown": "F1", "a": "Garbage+", "b": "shift+f6" }""");
        var map = KeyMap.Load(mixed, Cmds);
        map.Overrides.Should().BeEquivalentTo(new Dictionary<string, string> { ["b"] = "Shift+F6" });
    }
}

public class AppSettingsCompatTests
{
    [Fact]
    public void Old_settings_json_without_new_fields_loads_with_defaults()
    {
        const string old = """
            { "schemaVersion": 1, "theme": "dark", "density": "compact", "showHidden": true,
              "markdownOpen": "alwaysFoldy", "terminal": { "host": "console", "shell": "cmd" }, "animations": false }
            """;
        var s = JsonSerializer.Deserialize<AppSettings>(old, JsonFileStore.Options)!;
        s.Theme.Should().Be("dark");
        s.Animations.Should().BeFalse();
        s.Terminal.Shell.Should().Be("cmd");
        s.Accent.Should().Be("foldy");
        s.CopyThreads.Should().Be(0);
        s.Update.Should().Be(UpdateSettings.Default);
        s.SendCrashReports.Should().BeFalse();
    }

    [Fact]
    public void New_fields_roundtrip()
    {
        var s = new AppSettings
        {
            Accent = "windows",
            CopyThreads = 6,
            Update = new UpdateSettings(UpdateMode.NotifyOnly, UpdateChannel.Beta),
            SendCrashReports = true,
        };
        var json = JsonSerializer.Serialize(s, JsonFileStore.Options);
        var back = JsonSerializer.Deserialize<AppSettings>(json, JsonFileStore.Options)!;
        back.Should().Be(s);
    }
}

public class SettingsViewModelTests
{
    [Fact]
    public void Starts_clean_and_reflects_settings()
    {
        var vm = new SettingsViewModel(new AppSettings { Theme = "retro", CopyThreads = 8 });
        vm.Theme.Should().Be("retro");
        vm.CopyThreadsAuto.Should().BeFalse();
        vm.CopyThreadsManual.Should().Be(8);
        vm.IsDirty.Should().BeFalse();
        vm.Build().Should().Be(new AppSettings { Theme = "retro", CopyThreads = 8 });
    }

    [Fact]
    public void Unknown_values_fall_back_to_defaults()
    {
        var vm = new SettingsViewModel(new AppSettings { Theme = "neon", Accent = "pink", Density = "huge" });
        vm.Theme.Should().Be("system");
        vm.Accent.Should().Be("foldy");
        vm.Density.Should().Be("normal");
    }

    [Fact]
    public void Apply_raises_event_with_edited_copy_and_leaves_input_untouched()
    {
        var original = new AppSettings();
        var vm = new SettingsViewModel(original, windowsTerminalProfiles: ["PowerShell", "Ubuntu"]);
        SettingsAppliedEventArgs? got = null;
        vm.Applied += (_, e) => got = e;

        vm.Theme = "dark";
        vm.Accent = "windows";
        vm.Density = "comfortable";
        vm.ShowHidden = true;
        vm.TerminalHost = "windowsTerminal";
        vm.WtProfile = "Ubuntu";
        vm.WtAdminProfile = " ";
        vm.CopyThreadsAuto = false;
        vm.CopyThreadsManual = 12;
        vm.Animations = false;
        vm.UpdateModeChoice = "notify";
        vm.UpdateChannelChoice = "beta";
        vm.SendCrashReports = true;
        vm.IsDirty.Should().BeTrue();
        vm.Apply();

        got.Should().NotBeNull();
        var s = got!.Settings;
        s.Theme.Should().Be("dark");
        s.Accent.Should().Be("windows");
        s.Density.Should().Be("comfortable");
        s.ShowHidden.Should().BeTrue();
        s.Terminal.Host.Should().Be("windowsTerminal");
        s.Terminal.WtProfile.Should().Be("Ubuntu");
        s.Terminal.WtAdminProfile.Should().BeNull();
        s.CopyThreads.Should().Be(12);
        s.Animations.Should().BeFalse();
        s.Update.Should().Be(new UpdateSettings(UpdateMode.NotifyOnly, UpdateChannel.Beta));
        s.SendCrashReports.Should().BeTrue();
        original.Should().Be(new AppSettings());
        vm.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Auto_copy_threads_means_zero_and_manual_is_clamped()
    {
        var vm = new SettingsViewModel(new AppSettings());
        vm.Build().CopyThreads.Should().Be(0);
        vm.CopyThreadsAuto = false;
        vm.CopyThreadsManual = 999;
        vm.CopyThreadsManual.Should().Be(SettingsViewModel.MaxCopyThreads);
        vm.CopyThreadsManual = 0;
        vm.CopyThreadsManual.Should().Be(1);
    }

    [Fact]
    public void Cancel_reverts_edits_and_raises_event()
    {
        var vm = new SettingsViewModel(new AppSettings());
        var cancelled = false;
        vm.Cancelled += (_, _) => cancelled = true;
        vm.Theme = "dark";
        vm.TrySetGesture("file.copy", "F9");
        vm.Cancel();
        vm.Theme.Should().Be("system");
        vm.KeyBindings.Single(k => k.Id == "file.copy").Gesture.Should().Be("F5");
        vm.IsDirty.Should().BeFalse();
        cancelled.Should().BeTrue();
    }

    [Fact]
    public void Cancel_after_apply_returns_to_applied_values()
    {
        var vm = new SettingsViewModel(new AppSettings());
        vm.Theme = "light";
        vm.Apply();
        vm.Theme = "dark";
        vm.Cancel();
        vm.Theme.Should().Be("light");
    }

    [Fact]
    public void Keybinding_edit_shows_conflict_and_reset_clears_it()
    {
        var vm = new SettingsViewModel(new AppSettings());
        vm.TrySetGesture("file.move", "F5").Should().BeTrue();
        var move = vm.KeyBindings.Single(k => k.Id == "file.move");
        var copy = vm.KeyBindings.Single(k => k.Id == "file.copy");
        move.IsOverridden.Should().BeTrue();
        move.HasConflict.Should().BeTrue();
        copy.HasConflict.Should().BeTrue();
        vm.HasKeyConflicts.Should().BeTrue();
        vm.IsDirty.Should().BeTrue();

        move.ResetCommand.Execute(null);
        move.Gesture.Should().Be("F6");
        vm.HasKeyConflicts.Should().BeFalse();
        vm.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Invalid_gesture_is_rejected_and_apply_hands_over_keymap_copy()
    {
        var vm = new SettingsViewModel(new AppSettings());
        vm.TrySetGesture("file.copy", "Ctrl+").Should().BeFalse();
        vm.TrySetGesture("file.copy", "ctrl+shift+c");
        SettingsAppliedEventArgs? got = null;
        vm.Applied += (_, e) => got = e;
        vm.Apply();
        got!.KeyMap.GetGesture("file.copy").Should().Be("Ctrl+Shift+C");
        got.KeyMap.Overrides.Should().ContainKey("file.copy");
    }

    [Fact]
    public void Crawl_interval_setting_is_kept_and_unknown_values_fall_back_to_one_hour()
    {
        var vm = new SettingsViewModel(new AppSettings { CrawlEvery = "24h" });
        Assert.Equal("24h", vm.CrawlEvery);
        Assert.False(vm.IsDirty);
        vm.CrawlEvery = "manual";
        Assert.True(vm.IsDirty);
        Assert.Equal("manual", vm.Build().CrawlEvery);

        Assert.Equal("1h", new SettingsViewModel(new AppSettings { CrawlEvery = "이상한 값" }).CrawlEvery);
        Assert.Equal(TimeSpan.FromHours(1), AppSettings.CrawlInterval(new AppSettings().CrawlEvery));
        Assert.Equal(TimeSpan.FromHours(6), AppSettings.CrawlInterval("6h"));
        Assert.Equal(TimeSpan.FromHours(24), AppSettings.CrawlInterval("24h"));
        Assert.Null(AppSettings.CrawlInterval("manual"));
        Assert.Equal(TimeSpan.FromHours(1), AppSettings.CrawlInterval(null));
    }
}
