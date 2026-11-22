using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Foldy.Git;
using Foldy.Presentation.Git;

namespace Foldy.UI.Git;

/// <summary>
/// Fork식 Git 뷰 화면. DataContext = <see cref="GitTabViewModel"/>.
/// 논리는 ViewModel 에 있고 여기에는 팝업 열기, 목록 선택 전달, 확인 창 연결 같은 화면 일만 둔다.
/// 스레드 소유권: UI 스레드 전용.
/// </summary>
public partial class GitView : UserControl
{
    private Window? _window;

    public GitView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachConfirm();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private GitTabViewModel? Vm => DataContext as GitTabViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachConfirm();
        _window = Window.GetWindow(this);
        if (_window is not null) _window.Activated += OnWindowActivated;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null) _window.Activated -= OnWindowActivated;
        _window = null;
    }

    /// <summary>다른 프로그램에서 파일을 바꾸고 돌아오면 파일 상태만 가볍게 다시 읽는다 (설계서: 창 포커스 시 갱신).</summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (IsVisible && Vm is { IsBusy: false } vm) _ = vm.RefreshStatusOnlyAsync();
    }

    /// <summary>강제 Push 같은 위험한 동작의 확인 창을 연결한다 (호스트가 이미 지정했으면 건드리지 않음).</summary>
    private void AttachConfirm()
    {
        if (Vm is { Confirm: null } vm)
        {
            vm.Confirm = message => Task.FromResult(
                Foldy.UI.Dialogs.FoldyDialog.Confirm(Window.GetWindow(this), "Git", message, Foldy.Core.Localization.Loc.T("gv.continue"), danger: true));
        }
    }

    // ───────── 팝업 ─────────

    private static void Toggle(Popup p) => p.IsOpen = !p.IsOpen;

    private void OnPullMenu(object sender, RoutedEventArgs e) => Toggle(PullPopup);

    private void OnPushMenu(object sender, RoutedEventArgs e) => Toggle(PushPopup);

    private void OnStashMenu(object sender, RoutedEventArgs e) => Toggle(StashPopup);

    private void OnBranchMenu(object sender, RoutedEventArgs e) => Toggle(BranchPopup);

    private void OnClosePopups(object sender, RoutedEventArgs e)
    {
        PullPopup.IsOpen = false;
        PushPopup.IsOpen = false;
        StashPopup.IsOpen = false;
        BranchPopup.IsOpen = false;
    }

    private void OnNewBranchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is { } vm)
        {
            vm.CreateBranchCommand.Execute(null);
            BranchPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    // ───────── 목록 ─────────

    private void OnBranchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: GitBranchItem b } && Vm is { } vm)
        {
            BranchPopup.IsOpen = false;
            vm.CheckoutCommand.Execute(b);
            e.Handled = true;
        }
    }

    // 체크박스: 눌렀을 때 바로 Stage/Unstage 하고, 화면 상태는 git 결과로 다시 그린다
    private void OnRowCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: GitChangeRow { Item: { } item } } cb && Vm is { } vm)
        {
            cb.IsChecked = item.IsStaged;   // 결과가 나올 때까지 원래 모양 유지
            _ = vm.ToggleStageAsync(item);
        }
        e.Handled = true;
    }

    private void OnMenuDiff(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not MenuItem { DataContext: { } ctx }) return;
        _ = ctx switch
        {
            GitChangeRow { Item: { } item } => vm.ShowDiffForChangeAsync(item),
            GitCommitFile file => vm.ShowDiffForCommitFileAsync(file),
            _ => Task.CompletedTask,
        };
    }

    private void OnMenuBlame(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not MenuItem { DataContext: { } ctx }) return;
        _ = ctx switch
        {
            GitChangeRow { Item: { } item } => vm.ShowBlameForChangeAsync(item),
            GitCommitFile file => vm.ShowBlameForCommitFileAsync(file),
            _ => Task.CompletedTask,
        };
    }

    private void OnInstallGit(object sender, RoutedEventArgs e)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://git-scm.com/download/win") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (Vm is { } vm) vm.ErrorText = Foldy.Core.Localization.Loc.T("gv.browserFailed");
        }
    }
    private void OnGraphReachedEnd(object? sender, EventArgs e)
    {
        if (Vm is { HasMoreHistory: true } vm) vm.LoadMoreHistoryCommand.Execute(null);
    }
}

/// <summary>BooleanToVisibilityConverter 의 반대 (false 면 보임).</summary>
public sealed class InverseBoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}