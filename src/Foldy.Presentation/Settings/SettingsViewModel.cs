using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foldy.Presentation.Commands;
using Foldy.Presentation.Persistence;
using Foldy.Presentation.Update;

namespace Foldy.Presentation.Settings;

/// <summary>콤보박스 선택지: 저장되는 값과 화면에 보이는 이름.</summary>
public sealed record SettingChoice(string Value, string Label);

/// <summary>[적용] 결과. 호스트가 settings.json과 keymap.json에 저장하고 테마 등에 반영한다.</summary>
public sealed class SettingsAppliedEventArgs(AppSettings settings, KeyMap keyMap) : EventArgs
{
    public AppSettings Settings { get; } = settings;

    public KeyMap KeyMap { get; } = keyMap;
}

/// <summary>단축키 목록의 한 줄.</summary>
public sealed partial class KeyBindingItem : ObservableObject
{
    private readonly SettingsViewModel _owner;

    internal KeyBindingItem(SettingsViewModel owner, CommandDefinition def)
    {
        _owner = owner;
        Id = def.Id;
        Title = def.Title;
        ResetCommand = new RelayCommand(() => _owner.ResetGesture(Id));
        ClearCommand = new RelayCommand(() => _owner.TrySetGesture(Id, null));
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>지금 유효한 제스처 (없으면 빈 문자열).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GestureText))]
    private string _gesture = "";

    [ObservableProperty]
    private string _defaultGesture = "";

    [ObservableProperty]
    private bool _isOverridden;

    /// <summary>충돌 경고 문구. 없으면 빈 문자열.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflict))]
    private string _conflictText = "";

    /// <summary>키 입력을 기다리는 중 (창이 설정/해제).</summary>
    [ObservableProperty]
    private bool _isRecording;

    public bool HasConflict => ConflictText.Length > 0;

    public string GestureText => Gesture.Length > 0 ? Gesture : Foldy.Core.Localization.Loc.T("set.none");

    public IRelayCommand ResetCommand { get; }

    public IRelayCommand ClearCommand { get; }

    internal void Refresh(KeyMap map, Func<string, string> titleOf)
    {
        Gesture = map.GetGesture(Id) ?? "";
        DefaultGesture = map.GetDefaultGesture(Id) ?? "";
        IsOverridden = map.IsOverridden(Id);
        var others = map.ConflictsFor(Id, Gesture);
        ConflictText = others.Count == 0 ? "" : Foldy.Core.Localization.Loc.T("set.keys.same") + string.Join(", ", others.Select(titleOf));
    }
}

/// <summary>
/// 설정 창의 ViewModel. AppSettings와 KeyMap의 *복사본*을 편집하고,
/// [적용]하면 Applied 이벤트로 새 값을 내보낸다. [취소]하면 편집을 버린다. WPF 의존 없음.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const int MaxCopyThreads = 64;

    private AppSettings _baseline;
    private KeyMap _baselineKeys;
    private KeyMap _keys;
    private bool _loading;

    public SettingsViewModel(AppSettings current, KeyMap? keyMap = null, IEnumerable<string>? windowsTerminalProfiles = null)
    {
        _baseline = current;
        _baselineKeys = keyMap?.Clone() ?? new KeyMap();
        _keys = _baselineKeys.Clone();
        WindowsTerminalProfiles = [.. windowsTerminalProfiles ?? []];
        KeyBindings = [.. _keys.Commands.Select(c => new KeyBindingItem(this, c))];
        ApplyCommand = new RelayCommand(Apply);
        CancelCommand = new RelayCommand(Cancel);
        ResetAllKeysCommand = new RelayCommand(ResetAllGestures);
        Load(current);
    }

    /// <summary>[적용]·[확인]을 누르면 발생. 호스트가 저장하고 반영한다.</summary>
    public event EventHandler<SettingsAppliedEventArgs>? Applied;

    /// <summary>[취소]를 누르면 발생. 창을 닫는 신호로 쓴다.</summary>
    public event EventHandler? Cancelled;

    /// <summary>언어 고르기: Windows 따르기 + 지원 언어(그 언어로 쓴 이름).</summary>
    public static IReadOnlyList<SettingChoice> LanguageChoices { get; } =
        [new("auto", Foldy.Core.Localization.Loc.T("settings.language.auto")), .. Foldy.Core.Localization.Loc.Languages.Select(l => new SettingChoice(l.Code, l.NativeName))];

    public static IReadOnlyList<SettingChoice> ThemeChoices { get; } =
    [
        new("system", Foldy.Core.Localization.Loc.T("set.theme.system")),
        new("light", Foldy.Core.Localization.Loc.T("set.theme.light")),
        new("dark", Foldy.Core.Localization.Loc.T("set.theme.dark")),
        new("retro", Foldy.Core.Localization.Loc.T("set.theme.retro")),
    ];

    public static IReadOnlyList<SettingChoice> AccentChoices { get; } =
    [
        new("foldy", Foldy.Core.Localization.Loc.T("set.accent.foldy")),
        new("windows", Foldy.Core.Localization.Loc.T("set.accent.windows")),
    ];

    public static IReadOnlyList<SettingChoice> DensityChoices { get; } =
    [
        new("compact", Foldy.Core.Localization.Loc.T("set.density.compact")),
        new("normal", Foldy.Core.Localization.Loc.T("set.density.normal")),
        new("comfortable", Foldy.Core.Localization.Loc.T("set.density.relaxed")),
    ];

    public static IReadOnlyList<SettingChoice> MarkdownChoices { get; } =
    [
        new("alwaysFoldy", Foldy.Core.Localization.Loc.T("set.md.foldy")),
        new("preferAssociated", Foldy.Core.Localization.Loc.T("set.md.app")),
    ];

    public static IReadOnlyList<SettingChoice> TerminalHostChoices { get; } =
    [
        new("auto", Foldy.Core.Localization.Loc.T("set.term.auto")),
        new("windowsTerminal", "Windows Terminal"),
        new("console", Foldy.Core.Localization.Loc.T("set.term.console")),
    ];

    public static IReadOnlyList<SettingChoice> TerminalShellChoices { get; } =
    [
        new("pwsh", "PowerShell 7 (pwsh)"),
        new("powershell", "Windows PowerShell"),
        new("cmd", Foldy.Core.Localization.Loc.T("set.shell.cmd")),
    ];

    public static IReadOnlyList<SettingChoice> UpdateModeChoices { get; } =
    [
        new("auto", Foldy.Core.Localization.Loc.T("set.update.auto")),
        new("notify", Foldy.Core.Localization.Loc.T("set.update.notify")),
        new("off", Foldy.Core.Localization.Loc.T("set.update.off")),
    ];

    public static IReadOnlyList<SettingChoice> UpdateChannelChoices { get; } =
    [
        new("stable", Foldy.Core.Localization.Loc.T("set.channel.stable")),
        new("beta", Foldy.Core.Localization.Loc.T("set.channel.beta")),
    ];

    public ObservableCollection<string> WindowsTerminalProfiles { get; }

    public IReadOnlyList<KeyBindingItem> KeyBindings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _theme = "system";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _accent = "foldy";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _density = "normal";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _showHidden;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _markdownOpen = "preferAssociated";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _terminalHost = "auto";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _terminalShell = "pwsh";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string? _wtProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string? _wtAdminProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CopyThreadsManualEnabled))]
    private bool _copyThreadsAuto = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private int _copyThreadsManual = 4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _animations = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _updateModeChoice = "auto";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _updateChannelChoice = "stable";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _sendCrashReports;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _showGitBar = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _autoOpenGitView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _cleanupNotices = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _keepPreviousVersions = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _slowCrawl = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _aiSuggest = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _language = "auto";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _desktopSuggest = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _rebuildableExtra = "";

    public bool CopyThreadsManualEnabled => !CopyThreadsAuto;

    public IRelayCommand ApplyCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand ResetAllKeysCommand { get; }

    /// <summary>적용되지 않은 변경이 있는가.</summary>
    public bool IsDirty => Build() != _baseline || !KeysEqual(_keys, _baselineKeys);

    /// <summary>같은 제스처를 쓰는 명령이 있는가 (단축키 페이지 상단 경고).</summary>
    public bool HasKeyConflicts => _keys.FindConflicts().Count > 0;

    /// <summary>지금 편집 중인 값으로 만든 AppSettings (원본에 있는 다른 필드는 그대로).</summary>
    public AppSettings Build() => _baseline with
    {
        Theme = Theme,
        Accent = Accent,
        Density = Density,
        ShowHidden = ShowHidden,
        MarkdownOpen = MarkdownOpen,
        Terminal = _baseline.Terminal with
        {
            Host = TerminalHost,
            Shell = TerminalShell,
            WtProfile = NullIfEmpty(WtProfile),
            WtAdminProfile = NullIfEmpty(WtAdminProfile),
        },
        Animations = Animations,
        CopyThreads = CopyThreadsAuto ? 0 : Math.Clamp(CopyThreadsManual, 1, MaxCopyThreads),
        Update = new UpdateSettings(ParseMode(UpdateModeChoice), ParseChannel(UpdateChannelChoice)),
        SendCrashReports = SendCrashReports,
        ShowGitBar = ShowGitBar,
        AutoOpenGitView = AutoOpenGitView,
        CleanupNotices = CleanupNotices,
        KeepPreviousVersions = KeepPreviousVersions,
        SlowCrawl = SlowCrawl,
        AiSuggest = AiSuggest,
        Language = Language,
        DesktopSuggest = DesktopSuggest,
        RebuildableExtra = RebuildableExtra,
    };

    /// <summary>편집한 단축키 (재지정만 저장하려면 Save를 호출).</summary>
    public KeyMap EditedKeyMap => _keys;

    /// <summary>편집 내용을 확정한다: 이벤트로 새 값을 내보내고 이후 [취소]는 이 값으로 되돌린다.</summary>
    public void Apply()
    {
        var result = Build();
        _baseline = result;
        _baselineKeys = _keys.Clone();
        OnPropertyChanged(nameof(IsDirty));
        Applied?.Invoke(this, new SettingsAppliedEventArgs(result, _baselineKeys.Clone()));
    }

    /// <summary>편집을 버리고 마지막 적용(또는 처음) 값으로 되돌린다.</summary>
    public void Cancel()
    {
        _keys = _baselineKeys.Clone();
        Load(_baseline);
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>제스처를 바꾼다. 해석할 수 없으면 false. 충돌은 막지 않고 경고(ConflictText)만 표시한다.</summary>
    public bool TrySetGesture(string commandId, string? gesture)
    {
        if (!_keys.SetGesture(commandId, gesture))
        {
            return false;
        }

        RefreshKeys();
        return true;
    }

    public void ResetGesture(string commandId)
    {
        _keys.Reset(commandId);
        RefreshKeys();
    }

    public void ResetAllGestures()
    {
        _keys.ResetAll();
        RefreshKeys();
    }

    private void Load(AppSettings s)
    {
        _loading = true;
        Theme = Pick(ThemeChoices, s.Theme, "system");
        Accent = Pick(AccentChoices, s.Accent, "foldy");
        Density = Pick(DensityChoices, s.Density, "normal");
        ShowHidden = s.ShowHidden;
        MarkdownOpen = Pick(MarkdownChoices, s.MarkdownOpen, "preferAssociated");
        TerminalHost = Pick(TerminalHostChoices, s.Terminal.Host, "auto");
        TerminalShell = Pick(TerminalShellChoices, s.Terminal.Shell, "pwsh");
        WtProfile = s.Terminal.WtProfile;
        WtAdminProfile = s.Terminal.WtAdminProfile;
        CopyThreadsAuto = s.CopyThreads <= 0;
        CopyThreadsManual = s.CopyThreads > 0 ? Math.Min(s.CopyThreads, MaxCopyThreads) : 4;
        Animations = s.Animations;
        UpdateModeChoice = FormatMode(s.Update.Mode);
        UpdateChannelChoice = s.Update.Channel == Foldy.Presentation.Update.UpdateChannel.Beta ? "beta" : "stable";
        SendCrashReports = s.SendCrashReports;
        ShowGitBar = s.ShowGitBar;
        CleanupNotices = s.CleanupNotices;
        KeepPreviousVersions = s.KeepPreviousVersions;
        SlowCrawl = s.SlowCrawl;
        AiSuggest = s.AiSuggest;
        Language = s.Language;
        DesktopSuggest = s.DesktopSuggest;
        RebuildableExtra = s.RebuildableExtra;
        AutoOpenGitView = s.AutoOpenGitView;
        _loading = false;
        RefreshKeys();
        OnPropertyChanged(nameof(IsDirty));
    }

    private void RefreshKeys()
    {
        var titles = _keys.Commands.ToDictionary(c => c.Id, c => c.Title);
        string TitleOf(string id) => titles.TryGetValue(id, out var t) ? t : id;
        foreach (var item in KeyBindings)
        {
            item.Refresh(_keys, TitleOf);
        }

        OnPropertyChanged(nameof(HasKeyConflicts));
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnCopyThreadsManualChanged(int value)
    {
        if (!_loading && value is < 1 or > MaxCopyThreads)
        {
            CopyThreadsManual = Math.Clamp(value, 1, MaxCopyThreads);
        }
    }

    private static string Pick(IReadOnlyList<SettingChoice> choices, string? value, string fallback) =>
        choices.Any(c => c.Value == value) ? value! : fallback;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static UpdateMode ParseMode(string s) => s switch
    {
        "notify" => Foldy.Presentation.Update.UpdateMode.NotifyOnly,
        "off" => Foldy.Presentation.Update.UpdateMode.Off,
        _ => Foldy.Presentation.Update.UpdateMode.Auto,
    };

    private static string FormatMode(UpdateMode m) => m switch
    {
        Foldy.Presentation.Update.UpdateMode.NotifyOnly => "notify",
        Foldy.Presentation.Update.UpdateMode.Off => "off",
        _ => "auto",
    };

    private static UpdateChannel ParseChannel(string s) =>
        s == "beta" ? Foldy.Presentation.Update.UpdateChannel.Beta : Foldy.Presentation.Update.UpdateChannel.Stable;

    private static bool KeysEqual(KeyMap a, KeyMap b) =>
        a.Overrides.Count == b.Overrides.Count && a.Overrides.All(kv => b.Overrides.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
