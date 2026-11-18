using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Foldy.App.Services;
using Foldy.Providers.Sftp;
using Microsoft.Win32;

namespace Foldy.App;

/// <summary>SFTP 연결 창. 연결에 성공하면 <see cref="Result"/>에 sftp:// 경로를 담고 닫힌다.</summary>
public partial class SftpConnectWindow : Window
{
    private readonly SftpSessions _sessions;

    public SftpConnectWindow(SftpSessions sessions)
    {
        InitializeComponent();
        _sessions = sessions;
        ProfileBox.ItemsSource = sessions.Profiles.Load();
        Loaded += (_, _) => HostBox.Focus();
    }

    public string? Result { get; private set; }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not SftpProfile p)
        {
            return;
        }

        HostBox.Text = p.Host;
        PortBox.Text = p.Port.ToString(CultureInfo.InvariantCulture);
        UserBox.Text = p.User;
        PathBox.Text = p.InitialPath ?? "/";
        KeyRadio.IsChecked = p.Auth == SftpAuthKind.KeyFile;
        PasswordRadio.IsChecked = p.Auth == SftpAuthKind.Password;
        KeyBox.Text = p.KeyFilePath ?? "";
        PasswordBox.Password = "";
        SaveBox.IsChecked = false;
    }

    private void OnAuthChanged(object sender, RoutedEventArgs e)
    {
        if (PasswordPanel is null)
        {
            return;
        }

        var key = KeyRadio.IsChecked == true;
        PasswordPanel.Visibility = key ? Visibility.Collapsed : Visibility.Visible;
        KeyPanel.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "SSH 개인 키 파일",
            InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
            Filter = "모든 파일|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            KeyBox.Text = dlg.FileName;
        }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (!int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(HostBox.Text) || string.IsNullOrWhiteSpace(UserBox.Text))
        {
            ShowError("서버 주소, 포트, 사용자 이름을 확인하세요.");
            return;
        }

        var keyAuth = KeyRadio.IsChecked == true;
        var auth = keyAuth
            ? new SftpAuth(SftpAuthKind.KeyFile, KeyFilePath: KeyBox.Text.Trim(), Passphrase: NullIfEmpty(PassphraseBox.Password))
            : new SftpAuth(SftpAuthKind.Password, Password: PasswordBox.Password);
        var loc = new SftpLocation(UserBox.Text.Trim(), HostBox.Text.Trim(), port, SftpLocation.NormalizePath(PathBox.Text.Trim()));

        // 저장된 프로필을 골랐고 비밀번호를 비워 두면 저장된 비밀번호를 쓴다
        if (!keyAuth && auth.Password is "" && ProfileBox.SelectedItem is SftpProfile saved)
        {
            try
            {
                auth = saved.ToAuth();
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                ShowError("저장된 비밀번호를 읽을 수 없습니다. 다시 입력하세요.");
                return;
            }
        }

        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "연결 중…";
        try
        {
            _sessions.Remember(loc, auth);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _sessions.Provider.ConnectAsync(loc, cts.Token);
            if (SaveBox.IsChecked == true)
            {
                var profile = new SftpProfile
                {
                    Name = $"{loc.User}@{loc.Host}" + (port == 22 ? "" : $":{port}"),
                    Host = loc.Host,
                    Port = port,
                    User = loc.User ?? "",
                    Auth = auth.Kind,
                    KeyFilePath = auth.KeyFilePath,
                    InitialPath = loc.Path,
                }.WithPassword(auth.Password).WithPassphrase(auth.Passphrase);
                _sessions.Profiles.Upsert(profile);
            }

            Result = loc.ToString();
            DialogResult = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError("연결하지 못했습니다: " + ex.Message);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "연결";
        }
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
