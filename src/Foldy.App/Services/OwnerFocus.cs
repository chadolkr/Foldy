using System.Windows;

namespace Foldy.App.Services;

/// <summary>
/// 사용자 제보(2026-11-22, 1.2.0): "잠긴 메모를 저장하고 닫으면 Foldy 창이 내려가서 안 보인다".
/// Windows는 주인(Owner)이 있는 창이 닫힐 때 주인 창이 아니라 z 순서의 다음 창(다른 프로그램)을 앞으로 올리기도 한다.
/// 그래서 주인이 있는 모든 Foldy 창(메모·안내·금고·설정·AI 연결 등)이 닫히기 직전에, 아직 보이는 동안 주인 창을 먼저 앞으로 올린다.
/// 사용자가 그 창에서 닫은 경우(창이 활성)에만 한다: 빠른 검색에서 다른 앱을 연 뒤 저절로 닫히는 창이나
/// 5분 뒤 저절로 잠기는 메모가 다른 프로그램 위로 Foldy를 끌어올리지 않게.
/// </summary>
public static class OwnerFocus
{
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window w && ReferenceEquals(e.OriginalSource, w))
        {
            w.Closing -= OnClosing; // Loaded가 두 번 와도 한 번만
            w.Closing += OnClosing;
        }
    }

    private static void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 사용자가 내려 둔(최소화한) 주인 창은 그대로 둔다
        if (e.Cancel || sender is not Window { IsActive: true, Owner: { IsVisible: true, WindowState: not WindowState.Minimized } owner })
        {
            return;
        }

        owner.Activate();
    }
}
