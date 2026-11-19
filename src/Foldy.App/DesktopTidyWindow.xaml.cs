using System.IO;
using System.Windows;
using System.Windows.Data;
using Foldy.Core.Desktop;
using Foldy.Presentation.Files;

namespace Foldy.App;

/// <summary>바탕화면 정리: 미리 보여 주고, 보관 폴더로 옮기고, 한 번에 되돌린다. 지우지 않는다.</summary>
public partial class DesktopTidyWindow : Window
{
    private IReadOnlyList<DesktopMove> _plan = [];

    public DesktopTidyWindow()
    {
        InitializeComponent();
        Hint.Text = $"지우지 않고 보관만 해요. 바로 가기와 최근 {DesktopTidy.RecentDays}일 안에 쓴 파일은 그대로 두고, 나머지를 {ArchiveRoot}\\연-월\\종류 폴더로 옮깁니다. 마음에 안 들면 [되돌리기]로 한 번에 제자리로 돌아와요.";
        Loaded += (_, _) => Load();
    }

    public static string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string ArchiveRoot => Path.Combine(DesktopFolder, DesktopTidy.ArchiveFolderName); // 바탕화면 안 (폴더는 정리 대상이 아니라 다음에도 그대로)

    public static string JournalPath => Path.Combine(App.DataDir, "desktop-tidy-last.json");

    private void Load()
    {
        try
        {
            _plan = DesktopTidy.Plan(DesktopFolder, ArchiveRoot, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Heading.Text = "바탕화면을 읽지 못했어요: " + ex.Message;
            return;
        }

        var rows = _plan.Select(m => new
        {
            m.Src,
            m.Dst,
            m.Group,
            Name = Path.GetFileName(m.Src),
            SizeText = FileItem.FormatBytes(m.Size),
            DstFolder = Path.GetRelativePath(ArchiveRoot, Path.GetDirectoryName(m.Dst)!),
        }).ToList();
        var view = CollectionViewSource.GetDefaultView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
        Items.ItemsSource = view;
        Heading.Text = _plan.Count == 0
            ? "바탕화면이 깔끔해요. 보관할 파일이 없어요!"
            : $"바탕화면에서 보관할 파일 {_plan.Count:N0}개 ({FileItem.FormatBytes(_plan.Sum(m => m.Size))})";
        RunButton.IsEnabled = _plan.Count > 0;
        UndoButton.IsEnabled = DesktopTidy.LastMoves(JournalPath).Count > 0;
        OpenButton.IsEnabled = Directory.Exists(ArchiveRoot);
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        var plan = _plan;
        var done = await Task.Run(() => DesktopTidy.Execute(plan, JournalPath));
        Load();
        Summary.Text = $"{done.Count:N0}개를 바탕화면의 \"{DesktopTidy.ArchiveFolderName}\" 폴더로 옮겼어요." + (done.Count < plan.Count ? $" 사용 중이던 {plan.Count - done.Count:N0}개는 그대로 뒀어요." : "");
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        UndoButton.IsEnabled = false;
        var n = await Task.Run(() => DesktopTidy.Undo(JournalPath));
        Load();
        Summary.Text = $"{n:N0}개를 바탕화면 제자리로 돌려놨어요.";
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{ArchiveRoot}\"") { UseShellExecute = true });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
