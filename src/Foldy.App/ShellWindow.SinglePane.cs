using System.ComponentModel;
using System.Windows;
using Foldy.UI.Views;

namespace Foldy.App;

/// <summary>
/// 한쪽 창만 크게 보기 (사용자 2026-11-23: "양쪽 파일창 넓이가 고정되어 불편, 한쪽 창을 전체로 보고 다시 여는 기능").
/// 각 창 도구 줄의 [이 창만 크게]를 누르면 다른 창을 접고, 같은 자리의 [두 창으로 보기]로 원래 넓이대로 돌아온다.
/// 접은 동안 Tab(창 바꾸기)을 누르면 다른 창이 크게 보인다. 넓이는 가운데 경계를 끌어 지금처럼 바꾼다.
/// </summary>
public partial class ShellWindow
{
    /// <summary>크게 보이는 창 (두 창이면 null).</summary>
    private PaneView? _singlePane;

    /// <summary>한쪽만 보기 전의 왼쪽 비율 (돌아올 때·세션 저장에 쓴다).</summary>
    private double _splitBeforeSingle = 0.5;

    private double _leftMin;
    private double _rightMin;

    private void InitSinglePane()
    {
        _leftMin = LeftColumn.MinWidth;
        _rightMin = RightColumn.MinWidth;
        LeftPane.SinglePaneToggleRequested += (_, _) => ToggleSinglePane(LeftPane);
        RightPane.SinglePaneToggleRequested += (_, _) => ToggleSinglePane(RightPane);
        Shell.PropertyChanged += OnShellPropertyChangedForSinglePane;
    }

    private void OnShellPropertyChangedForSinglePane(object? sender, PropertyChangedEventArgs e)
    {
        // 한쪽만 보는 동안 다른 창이 활성이 되면(Tab 등) 그 창을 크게 보인다
        if (e.PropertyName == nameof(Shell.ActivePane) && _singlePane is not null && ActiveView != _singlePane)
        {
            ShowSingle(ActiveView);
        }
    }

    /// <summary>[이 창만 크게] ↔ [두 창으로 보기].</summary>
    private void ToggleSinglePane(PaneView pane)
    {
        if (_singlePane is not null)
        {
            ShowBoth();
            return;
        }

        var total = LeftColumn.ActualWidth + RightColumn.ActualWidth;
        _splitBeforeSingle = total > 0 ? Math.Clamp(LeftColumn.ActualWidth / total, 0.2, 0.8) : 0.5;
        ShowSingle(pane);
    }

    private void ShowSingle(PaneView pane)
    {
        _singlePane = pane;
        var left = pane == LeftPane;
        LeftPane.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        RightPane.Visibility = left ? Visibility.Collapsed : Visibility.Visible;
        PaneSplitterColumn.Width = new GridLength(0);
        PaneSplitter.Visibility = Visibility.Collapsed;
        PaneDivider.Visibility = Visibility.Collapsed;
        LeftColumn.MinWidth = left ? _leftMin : 0;
        RightColumn.MinWidth = left ? 0 : _rightMin;
        LeftColumn.Width = left ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RightColumn.Width = left ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        LeftPane.SetSinglePane(left);
        RightPane.SetSinglePane(!left);
        var vm = left ? Shell.Left : Shell.Right;
        if (Shell.ActivePane != vm)
        {
            Shell.Activate(vm);
        }
    }

    private void ShowBoth()
    {
        _singlePane = null;
        LeftPane.Visibility = Visibility.Visible;
        RightPane.Visibility = Visibility.Visible;
        PaneSplitterColumn.Width = new GridLength(9);
        PaneSplitter.Visibility = Visibility.Visible;
        PaneDivider.Visibility = Visibility.Visible;
        LeftColumn.MinWidth = _leftMin;
        RightColumn.MinWidth = _rightMin;
        LeftColumn.Width = new GridLength(_splitBeforeSingle, GridUnitType.Star);
        RightColumn.Width = new GridLength(1 - _splitBeforeSingle, GridUnitType.Star);
        LeftPane.SetSinglePane(false);
        RightPane.SetSinglePane(false);
    }
}
