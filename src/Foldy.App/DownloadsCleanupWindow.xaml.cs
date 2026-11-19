using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Foldy.App.Services;
using Foldy.Core.Downloads;
using Foldy.Presentation.Files;
using Foldy.Presentation.Shell;

namespace Foldy.App;

/// <summary>다운로드 정리: 제안만 하고, 고른 항목을 휴지통으로 보내거나 보관 폴더로 옮긴다.</summary>
public partial class DownloadsCleanupWindow : Window
{
    private readonly string _folder;
    private readonly ShellViewModel _shell;
    private readonly CancellationTokenSource _cts = new();
    private readonly ObservableCollection<Row> _rows = [];

    public DownloadsCleanupWindow(string folder, ShellViewModel shell)
    {
        InitializeComponent();
        _folder = folder;
        _shell = shell;
        var view = CollectionViewSource.GetDefaultView(_rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Group)));
        Items.ItemsSource = view;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    /// <summary>현재 사용자의 다운로드 폴더 (없으면 null).</summary>
    public static string? DefaultFolder()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(p) ? p : null;
    }

    private async Task LoadAsync()
    {
        IReadOnlyList<DownloadItem> items;
        try
        {
            items = await Task.Run(() =>
            {
                var installed = InstalledPrograms.Names();
                return DownloadsPlanner.Plan(_folder, product => DownloadsPlanner.MatchInstalled(product, installed), DateTime.UtcNow, findDuplicates: true, _cts.Token);
            });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Heading.Text = "다운로드 폴더를 읽지 못했어요: " + ex.Message;
            return;
        }

        foreach (var i in items)
        {
            _rows.Add(new Row(i));
        }

        Heading.Text = items.Count == 0
            ? "다운로드 폴더가 깔끔해요. 정리할 게 없어요!"
            : $"정리할 만한 파일 {items.Count:N0}개, {FileItem.FormatBytes(items.Sum(i => i.Size))}를 찾았어요. 휴지통에 버리거나 보관하세요.";
        UpdateSummary();
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateSummary();

    private List<Row> Selected() => _rows.Where(r => r.Selected).ToList();

    private void UpdateSummary()
    {
        var sel = Selected();
        Summary.Text = sel.Count == 0 ? "" : $"고른 {sel.Count:N0}개, {FileItem.FormatBytes(sel.Sum(r => r.Item.Size))}";
        RecycleButton.IsEnabled = ArchiveButton.IsEnabled = sel.Count > 0;
    }

    private async void OnRecycle(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        RecycleButton.IsEnabled = ArchiveButton.IsEnabled = false;
        if (await _shell.RecycleAsync(sel.Select(r => r.Item.Path).ToList()))
        {
            Remove(sel.Where(r => !File.Exists(r.Item.Path)).ToList(), "휴지통으로 옮겼어요. 휴지통에서 되살릴 수 있어요.");
        }
        else
        {
            UpdateSummary();
        }
    }

    private async void OnArchive(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        RecycleButton.IsEnabled = ArchiveButton.IsEnabled = false;
        var dst = DownloadsPlanner.ArchiveFolder(_folder, DateTime.Now);
        var n = await Task.Run(() => DownloadsPlanner.MoveToArchive(sel.Select(r => r.Item.Path), dst));
        Remove(sel.Where(r => !File.Exists(r.Item.Path)).ToList(), $"{n:N0}개를 {dst}로 옮겼어요.");
    }

    private void Remove(List<Row> done, string message)
    {
        foreach (var r in done)
        {
            _rows.Remove(r);
        }

        UpdateSummary();
        Summary.Text = message;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private sealed class Row(DownloadItem item) : INotifyPropertyChanged
    {
        private bool _selected = item.Kind != DownloadKind.Old; // 오래된 파일은 직접 골라야 한다

        public event PropertyChangedEventHandler? PropertyChanged;

        public DownloadItem Item { get; } = item;

        public string Group => Item.Kind switch
        {
            DownloadKind.Installer => "이미 설치한 프로그램의 설치 파일",
            DownloadKind.Duplicate => "중복 파일 (같은 내용이 이미 있음)",
            DownloadKind.Unpacked => "이미 푼 압축 파일",
            _ => $"{DownloadsPlanner.OldDays}일 넘게 열지 않은 파일",
        };

        public string Name => Path.GetFileName(Item.Path);

        public string FullPath => Item.Path;

        public string SizeText => FileItem.FormatBytes(Item.Size);

        public string Reason => Item.Reason;

        public bool Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }
    }
}
