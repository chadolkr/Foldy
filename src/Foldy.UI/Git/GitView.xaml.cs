using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
                MessageBox.Show(Window.GetWindow(this), message, "Git", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
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

    private void OnUnstagedSelection(object sender, SelectionChangedEventArgs e) =>
        Vm?.SetSelectedUnstaged(UnstagedList.SelectedItems.OfType<GitChangeItem>());

    private void OnStagedSelection(object sender, SelectionChangedEventArgs e) =>
        Vm?.SetSelectedStaged(StagedList.SelectedItems.OfType<GitChangeItem>());

    // 더블클릭: 한 줄을 반대쪽으로 옮긴다
    private void OnUnstagedDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm && UnstagedList.SelectedItems.Count > 0) vm.StageSelectedCommand.Execute(null);
    }

    private void OnStagedDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm && StagedList.SelectedItems.Count > 0) vm.UnstageSelectedCommand.Execute(null);
    }

    private void OnGraphReachedEnd(object? sender, EventArgs e)
    {
        if (Vm is { HasMoreHistory: true } vm) vm.LoadMoreHistoryCommand.Execute(null);
    }
}
