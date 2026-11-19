using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Foldy.Platform;
using Microsoft.Win32;

namespace Foldy.UI.Theming;

/// <summary>테마 선택. System은 Windows "앱 모드"(다크/라이트)를 따라가고 바뀌면 즉시 전환한다.</summary>
public enum FoldyTheme { System, Light, Dark, Retro }

/// <summary>
/// 테마 적용기. Application.Resources의 앞쪽 병합 사전(토큰 + 컨트롤 + 레트로 덮어쓰기)만 교체하고,
/// 화면은 DynamicResource로 토큰을 참조하므로 다시 만들지 않아도 바로 바뀐다. UI 스레드 전용.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri LightUri = new("pack://application:,,,/Foldy.UI;component/Themes/Modern.Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/Foldy.UI;component/Themes/Modern.Dark.xaml");
    private static readonly Uri ControlsUri = new("pack://application:,,,/Foldy.UI;component/Themes/Modern.Controls.xaml");
    private static readonly Uri RetroUri = new("pack://application:,,,/Foldy.UI;component/Themes/Retro.xaml");

    private static Application? _app;
    private static Color? _appliedAccent;
    private static readonly List<ResourceDictionary> Applied = [];
    private static readonly List<WeakReference<Window>> Windows = [];

    public static FoldyTheme Mode { get; private set; } = FoldyTheme.System;

    /// <summary>true면 Windows 강조색을 쓰고, false면 Foldy 오렌지 (설정의 Accent = "windows" / "foldy").</summary>
    public static bool UseWindowsAccent { get; private set; }

    /// <summary>실제로 적용된 모양이 어두운지 (System이면 Windows 설정에 따름).</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Windows 고대비 모드가 켜져 있어 시스템 색으로 그리는 중인가 (테마 선택보다 우선).</summary>
    public static bool IsHighContrast { get; private set; }

    public static event Action? Changed;

    public static void Initialize(Application app, FoldyTheme mode)
    {
        _app = app;
        Mode = mode;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        app.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Apply();
    }

    public static void SetMode(FoldyTheme mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        Apply();
    }

    /// <summary>강조색 선택 ("windows" 또는 "foldy"). 바로 반영된다. 레트로 테마는 자체 색을 유지한다.</summary>
    public static void SetAccent(string? accent)
    {
        var windows = string.Equals(accent, "windows", StringComparison.OrdinalIgnoreCase);
        if (UseWindowsAccent == windows)
        {
            return;
        }

        UseWindowsAccent = windows;
        Apply();
    }

    /// <summary>창을 등록해 제목 표시줄 색을 테마에 맞춘다.</summary>
    public static void Attach(Window window)
    {
        Windows.Add(new WeakReference<Window>(window));
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyChrome(window);
        }
        else
        {
            window.SourceInitialized += (_, _) => ApplyChrome(window);
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            _app?.Dispatcher.BeginInvoke(() =>
            {
                var accentChanged = UseWindowsAccent && AccentColor.ReadWindowsAccent() != _appliedAccent;
                if ((Mode == FoldyTheme.System && SystemTheme.AppsUseDarkTheme() != IsDark) || accentChanged || HighContrastTheme.IsActive != IsHighContrast
                    || (IsHighContrast && e.Category == UserPreferenceCategory.Color))
                {
                    Apply();
                }
            });
        }
    }

    private static void Apply()
    {
        if (_app is null)
        {
            return;
        }

        var dark = Mode switch
        {
            FoldyTheme.Dark => true,
            FoldyTheme.Light or FoldyTheme.Retro => false,
            _ => SystemTheme.AppsUseDarkTheme(),
        };
        var highContrast = HighContrastTheme.IsActive;
        IsHighContrast = highContrast;
        if (highContrast)
        {
            dark = (SystemColors.WindowColor.R + SystemColors.WindowColor.G + SystemColors.WindowColor.B) < 3 * 128; // 제목 표시줄 색 방향
        }
        IsDark = dark;

        var dicts = new List<ResourceDictionary>
        {
            new() { Source = dark ? DarkUri : LightUri },
            new() { Source = ControlsUri },
        };
        if (Mode == FoldyTheme.Retro && !highContrast)
        {
            dicts.Add(new ResourceDictionary { Source = RetroUri });
        }

        _appliedAccent = null;
        if (highContrast)
        {
            // 고대비: 모든 색 토큰을 시스템 색으로 덮어쓴다 (테마·강조색 선택보다 우선, WCAG 1.4.3 / 사용자 설정 존중)
            dicts.Add(HighContrastTheme.CreateOverrides());
        }
        else if (UseWindowsAccent && Mode != FoldyTheme.Retro && AccentColor.ReadWindowsAccent() is { } accent)
        {
            _appliedAccent = accent;
            dicts.Add(AccentColor.CreateOverrides(accent, dark));
        }

        var merged = _app.Resources.MergedDictionaries;
        foreach (var d in Applied)
        {
            merged.Remove(d);
        }

        Applied.Clear();
        for (var i = 0; i < dicts.Count; i++)
        {
            merged.Insert(i, dicts[i]);
            Applied.Add(dicts[i]);
        }

        Windows.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in Windows)
        {
            if (w.TryGetTarget(out var win))
            {
                ApplyChrome(win);
            }
        }

        Changed?.Invoke();
    }

    private static void ApplyChrome(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        uint? caption = null;
        if (_app?.TryFindResource("Brush.Surface") is SolidColorBrush b)
        {
            var c = b.Color;
            caption = (uint)(c.R | (c.G << 8) | (c.B << 16)); // COLORREF = 0x00BBGGRR
        }

        SystemTheme.ApplyTitleBar(hwnd, IsDark, Mode == FoldyTheme.Retro ? null : caption);
    }
}
