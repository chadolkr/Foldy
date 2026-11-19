using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Foldy.App.Services;
using Foldy.Core.Logs;
using Foldy.Presentation.Files;
using Foldy.Presentation.Shell;

namespace Foldy.App;

/// <summary>
/// 로그 정리: 드라이브의 오래된 *.log를 폴더·달별로 보여 주고, 고른 것을 월별 zip으로 묶은 뒤 원본을 휴지통으로 옮긴다.
/// 압축은 <see cref="LogArchiver"/>가 하고(검증 포함), 휴지통 이동은 ShellViewModel.RecycleAsync가 한다.
/// </summary>
public partial class LogCleanupWindow : Window
{
    private readonly string _drive;
    private readonly ShellViewModel _shell;
    private readonly CancellationTokenSource _cts = new();
    private readonly ObservableCollection<Row> _rows = [];
    private bool _running;

    public LogCleanupWindow(string drive, ShellViewModel shell)
    {
        InitializeComponent();
        _drive = drive;
        _shell = shell;
        Groups.ItemsSource = _rows;
        Title = $"로그 정리 - {drive.TrimEnd('\\')}";
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    private async Task LoadAsync()
    {
        Heading.Text = $"{_drive.TrimEnd('\\')} 드라이브의 로그 파일을 찾는 중…";
        List<LogFile>? files;
        try
        {
            files = await LogFinder.FromIndexAsync(_drive, _cts.Token);
            if (files is null)
            {
                Heading.Text = "색인 서비스가 없어 드라이브를 직접 훑는 중… (조금 걸려요)";
                files = await LogFinder.ScanAsync(_drive, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var groups = LogPlanner.Plan(files, DateTime.UtcNow);
        foreach (var g in groups)
        {
            _rows.Add(new Row(g));
        }

        Heading.Text = groups.Count == 0
            ? "정리할 오래된 로그가 없어요. 깔끔하네요!"
            : $"오래된 로그 {groups.Sum(g => g.Files.Count):N0}개, {FileItem.FormatBytes(groups.Sum(g => g.Size))}를 찾았어요.";
        UpdateSummary();
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        var sel = _rows.Where(r => r.Selected && !r.Done).ToList();
        var saving = sel.Sum(r => r.Group.EstimatedSaving);
        Summary.Text = sel.Count == 0 ? "" : $"고른 {sel.Sum(r => r.Group.Files.Count):N0}개 ({FileItem.FormatBytes(sel.Sum(r => r.Group.Size))}) → 압축하면 약 {FileItem.FormatBytes(saving)} 줄어요";
        RunButton.IsEnabled = !_running && sel.Count > 0;
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        _running = true;
        RunButton.IsEnabled = false;
        long freed = 0;
        var skipped = 0;
        foreach (var row in _rows.Where(r => r.Selected && !r.Done).ToList())
        {
            row.State = "압축 중…";
            LogArchiveResult r;
            try
            {
                r = await Task.Run(() => LogArchiver.Archive(row.Group, _cts.Token));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                row.State = "실패: " + ex.Message;
                continue;
            }

            skipped += r.Skipped.Count;
            var recycled = r.Archived.Count > 0 && await _shell.RecycleAsync(r.Archived);
            if (recycled)
            {
                freed += row.Group.Files.Where(f => r.Archived.Contains(f.Path)).Sum(f => f.Size) - r.ZipBytesAdded;
            }

            row.Done = true;
            row.State = r.Archived.Count == 0 ? "건너뜀 (사용 중)" : recycled ? $"완료 {r.Archived.Count:N0}개" : "압축만 함";
        }

        _running = false;
        Summary.Text = $"정리했어요. 약 {FileItem.FormatBytes(Math.Max(0, freed))} 줄었어요 (원본은 휴지통에 있어요)." + (skipped > 0 ? $" 사용 중이라 건너뛴 파일 {skipped:N0}개." : "");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private sealed class Row(LogGroup group) : INotifyPropertyChanged
    {
        private string _state = "";
        private bool _selected = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public LogGroup Group { get; } = group;

        public string Folder => Group.Folder;

        public string Month => Group.Month;

        public string CountText => Group.Files.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

        public string SizeText => FileItem.FormatBytes(Group.Size);

        public bool Done { get; set; }

        public bool Selected
        {
            get => _selected;
            set => Set(ref _selected, value);
        }

        public string State
        {
            get => _state;
            set => Set(ref _state, value);
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
