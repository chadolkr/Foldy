using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Foldy.Presentation.Analysis;

namespace Foldy.UI.Analysis;

/// <summary>
/// 폴더 분석 화면. DataContext = <see cref="AnalysisTabViewModel"/>. 논리는 ViewModel에 있고 여기에는 두 번 클릭 전달,
/// 키 입력 같은 화면 일만 둔다. 휴지통으로 보내기는 호스트가 <see cref="AnalysisTabViewModel.RecycleRequested"/>를 받아 한다.
/// 스레드 소유권: UI 스레드 전용.
/// </summary>
public partial class AnalysisView : UserControl
{
    public AnalysisView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseUp += OnPreviewMouseUp;
    }

    private AnalysisTabViewModel? Vm => DataContext as AnalysisTabViewModel;

    /// <summary>시스템 파일 정리는 Windows 도구에 맡긴다 (Windows 11: 정리 권장 사항, 없으면 디스크 정리).</summary>
    private void OnOpenWindowsCleanup(object sender, RoutedEventArgs e)
    {
        foreach (var target in new[] { "ms-settings:storagerecommendations", "cleanmgr.exe" })
        {
            try
            {
                using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
                return;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // 다음 방법으로
            }
        }
    }

    /// <summary>목록 항목 두 번 클릭 → 호스트가 패널에서 연다 (OpenRequested).</summary>
    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: { } item } && Vm is { } vm)
        {
            // 폴더 행은 트리맵에서도 보여 준다
            if (item is FolderListItem f)
            {
                vm.ShowInTreemapCommand.Execute(f);
            }

            vm.OpenCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        if (e.Key == Key.F5 && vm.AnalyzeCommand.CanExecute(null))
        {
            vm.AnalyzeCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.IsBusy)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>마우스 뒤로 단추 → 트리맵 한 단계 위로.</summary>
    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && Vm is { } vm && vm.UpCommand.CanExecute(null))
        {
            vm.UpCommand.Execute(null);
            e.Handled = true;
        }
    }
}

/// <summary>null이면 Collapsed.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>UTC DateTime → 현지 "yyyy-MM-dd HH:mm".</summary>
public sealed class LocalDateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
