using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace Foldy.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = Foldy.Core.Localization.Loc.F("about.version", typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "?");
        // 소개 페이지는 앱 언어에 맞는 주소로 (사용자 요청 2026-11-25)
        SiteLink.NavigateUri = new Uri(Foldy.Core.Localization.ProductSite.Current);
        SiteText.Text = Foldy.Core.Localization.ProductSite.Current;
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnReportBug(object sender, RoutedEventArgs e) => App.OpenBugReport();
}
