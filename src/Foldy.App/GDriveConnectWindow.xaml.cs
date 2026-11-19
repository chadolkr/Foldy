using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Foldy.App.Services;
using Foldy.Presentation.Files;
using Foldy.Providers.GoogleDrive;

namespace Foldy.App;

/// <summary>
/// Google Drive 연결 창: 연결한 계정 목록(열기·연결 끊기), [Google 계정 연결](시스템 브라우저 로그인),
/// 고급(내 OAuth 클라이언트 ID). 계정을 열면 <see cref="Result"/>에 gdrive:// 경로를 담고 닫힌다.
/// </summary>
public partial class GDriveConnectWindow : Window
{
    private readonly GDriveSessions _drive;
    private CancellationTokenSource? _signIn;

    private sealed record Row(string Email, string Detail);

    public GDriveConnectWindow(GDriveSessions drive)
    {
        InitializeComponent();
        _drive = drive;
        Reload();
        Closed += (_, _) => _signIn?.Cancel();
    }

    public string? Result { get; private set; }

    private void Reload()
    {
        var rows = _drive.Store.Accounts.Select(a => new Row(a.Email, a.DisplayName ?? "")).ToList();
        AccountList.ItemsSource = rows;
        AccountsPanel.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count > 0 && AccountList.SelectedIndex < 0)
        {
            AccountList.SelectedIndex = 0;
        }

        AddButton.Content = rows.Count > 0 ? "다른 Google 계정 연결…" : "Google 계정 연결…";
        AddButton.IsDefault = rows.Count == 0;

        var user = _drive.Store.GetUserClient();
        ClientIdBox.Text = user?.ClientId ?? "";
        ClientHint.Text = GDriveSessions.HasBuiltInClient
            ? "이 Foldy에는 기본 클라이언트가 들어 있어 비워 두면 됩니다. 직접 만든 클라이언트를 쓰려면 넣으세요."
            : "이 Foldy 빌드에는 기본 클라이언트가 없습니다. Google Cloud 콘솔에서 '데스크톱 앱' OAuth 클라이언트를 만들어 넣으세요.";
        if (_drive.Client is null)
        {
            ClientExpander.IsExpanded = true;
        }

        UpdateButtons();
        _ = FillQuotaAsync(rows);
    }

    /// <summary>계정마다 사용량을 채운다(실패하면 다시 로그인 안내).</summary>
    private async Task FillQuotaAsync(List<Row> rows)
    {
        var updated = new List<Row>(rows);
        for (var i = 0; i < rows.Count; i++)
        {
            string detail;
            try
            {
                var about = await _drive.AboutAsync(rows[i].Email, CancellationToken.None);
                var used = FileItem.FormatBytes(about.QuotaUsage);
                detail = (about.DisplayName is { Length: > 0 } n ? n + " · " : "") +
                         (about.QuotaLimit is > 0 ? $"{used} / {FileItem.FormatBytes(about.QuotaLimit.Value)} 사용" : $"{used} 사용");
            }
            catch (GDriveAuthException)
            {
                detail = "로그인이 만료됐습니다. [다른 Google 계정 연결]로 같은 계정에 다시 로그인하세요.";
            }
            catch (Exception ex) when (ex is System.IO.IOException or HttpRequestException)
            {
                detail = "연결할 수 없습니다 (네트워크·프록시 확인)";
            }

            updated[i] = rows[i] with { Detail = detail };
        }

        if (IsLoaded || IsVisible)
        {
            var sel = AccountList.SelectedIndex;
            AccountList.ItemsSource = updated;
            AccountList.SelectedIndex = sel;
        }
    }

    private void UpdateButtons()
    {
        var busy = _signIn is not null;
        OpenButton.IsEnabled = DisconnectButton.IsEnabled = !busy && AccountList.SelectedItem is Row;
        AddButton.IsEnabled = !busy;
        WaitPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is Row r && _signIn is null)
        {
            Result = GDriveSessions.RootPath(r.Email);
            DialogResult = true;
        }
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        ShowError(null);
        if (_drive.Client is null)
        {
            ClientExpander.IsExpanded = true;
            ShowError("먼저 OAuth 클라이언트 ID를 넣고 [저장]을 누르세요.");
            ClientIdBox.Focus();
            return;
        }

        _signIn = new CancellationTokenSource();
        UpdateButtons();
        try
        {
            var email = await _drive.SignInAsync(_signIn.Token);
            Result = GDriveSessions.RootPath(email);
            Activate();
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // 사용자가 취소함
        }
        catch (Exception ex) when (ex is System.IO.IOException or HttpRequestException or TimeoutException or InvalidOperationException or System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception)
        {
            ShowError(GDriveFileOperationService.Describe(ex));
        }
        finally
        {
            _signIn?.Dispose();
            _signIn = null;
            if (IsVisible)
            {
                UpdateButtons();
            }
        }
    }

    private void OnCancelSignIn(object sender, RoutedEventArgs e) => _signIn?.Cancel();

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is not Row r)
        {
            return;
        }

        if (MessageBox.Show(this, $"{r.Email} 연결을 끊을까요?\n\nDrive의 파일은 그대로이고, 이 PC에 저장된 로그인 정보만 지웁니다.",
                "Google Drive", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await _drive.DisconnectAsync(r.Email, CancellationToken.None);
        }
        catch (Exception ex) when (ex is System.IO.IOException or HttpRequestException)
        {
            ShowError(ex.Message);
        }

        Reload();
    }

    private void OnSaveClient(object sender, RoutedEventArgs e)
    {
        var id = ClientIdBox.Text.Trim();
        if (id.Length > 0 && !id.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("클라이언트 ID는 보통 '….apps.googleusercontent.com'으로 끝납니다. 다시 확인하세요.");
            return;
        }

        _drive.Store.SetUserClient(id, ClientSecretBox.Password);
        ClientSecretBox.Password = "";
        ShowError(null);
        Reload();
    }

    private void OnClearClient(object sender, RoutedEventArgs e)
    {
        _drive.Store.SetUserClient(null, null);
        ClientSecretBox.Password = "";
        Reload();
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        using var _ = Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void ShowError(string? text)
    {
        ErrorText.Text = text ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
