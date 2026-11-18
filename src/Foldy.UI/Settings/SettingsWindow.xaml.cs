using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using Foldy.Presentation.Settings;
using Foldy.UI.Theming;

namespace Foldy.UI.Settings;

/// <summary>
/// 설정 창. 호스트는 SettingsViewModel을 만들어 넘기고, vm.Applied 이벤트로 새 설정을 받아 저장·반영한다
/// (사용법: docs/settings.md). [확인]은 적용 후 닫기, [취소]는 편집 버리고 닫기.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private KeyBindingItem? _recording;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ThemeManager.Attach(this);
    }

    /// <summary>분류 이름 → 페이지 이름 (왼쪽 목록 순서와 같다).</summary>
    private FrameworkElement[] Pages => [PageGeneral, PageAppearance, PageList, PageTerminal, PageCopy, PageUpdate, PageKeys];

    /// <summary>시작할 분류를 고른다 (0 일반 … 6 단축키).</summary>
    public void SelectCategory(int index) => Categories.SelectedIndex = Math.Clamp(index, 0, 6);

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageKeys is null)
        {
            return; // InitializeComponent 중 첫 선택 알림
        }

        var pages = Pages;
        for (var i = 0; i < pages.Length; i++)
        {
            pages[i].Visibility = i == Categories.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        }

        StopRecording();
    }

    private void OnApply(object sender, RoutedEventArgs e) => _vm.Apply();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _vm.Apply();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _vm.Cancel();
        DialogResult = false;
    }

    // ── 단축키 기록 ───────────────────────────────────────────────

    private void OnRecordKey(object sender, RoutedEventArgs e)
    {
        StopRecording();
        if (sender is FrameworkElement { Tag: KeyBindingItem item })
        {
            _recording = item;
            item.IsRecording = true;
        }
    }

    private void OnRecordKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is not { } item)
        {
            return; // 기록 중이 아니면 Enter/Space가 버튼 클릭(= 기록 시작)이 되도록 둔다
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None or Key.DeadCharProcessed)
        {
            return; // 수정 키만 눌린 상태: 계속 기다린다
        }

        var mods = Keyboard.Modifiers;
        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            StopRecording();
            return;
        }

        if (key == Key.Back && mods == ModifierKeys.None)
        {
            _vm.TrySetGesture(item.Id, null); // 단축키 없음
            StopRecording();
            return;
        }

        if (GestureFromKey(key, mods) is { } text)
        {
            _vm.TrySetGesture(item.Id, text);
            StopRecording();
        }
    }

    private void OnRecordLostFocus(object sender, RoutedEventArgs e) => StopRecording();

    private void StopRecording()
    {
        if (_recording is { } item)
        {
            item.IsRecording = false;
            _recording = null;
        }
    }

    /// <summary>눌린 키를 KeyMap 표기("Ctrl+Shift+P", "Ctrl+`")로 바꾼다. 지원하지 않는 키는 null.</summary>
    internal static string? GestureFromKey(Key key, ModifierKeys mods)
    {
        var name = key switch
        {
            Key.Oem3 => "`",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.Oem6 => "]",
            Key.Oem1 => ";",
            Key.OemQuotes => "'",
            Key.OemQuestion => "/",
            Key.OemPipe => "\\",
            Key.Return => "Enter",
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            Key.Back => "Backspace",
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            _ => key.ToString(),
        };

        var gesture = new List<string>(4);
        if ((mods & ModifierKeys.Control) != 0)
        {
            gesture.Add("Ctrl");
        }

        if ((mods & ModifierKeys.Alt) != 0)
        {
            gesture.Add("Alt");
        }

        if ((mods & ModifierKeys.Shift) != 0)
        {
            gesture.Add("Shift");
        }

        if ((mods & ModifierKeys.Windows) != 0)
        {
            gesture.Add("Win");
        }

        gesture.Add(name);
        var text = string.Join('+', gesture);
        return Foldy.Presentation.Commands.KeyGesture.Normalize(text);
    }
}
