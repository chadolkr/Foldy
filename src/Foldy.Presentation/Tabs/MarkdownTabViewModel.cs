using CommunityToolkit.Mvvm.ComponentModel;

namespace Foldy.Presentation.Tabs;

/// <summary>내장 Markdown 뷰어 탭 (설계서 5.9). 렌더링은 뷰(Foldy.UI)가, 문서 내 이동 기록은 여기서.</summary>
public sealed partial class MarkdownTabViewModel : TabBase
{
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();

    [ObservableProperty]
    private string _filePath;

    [ObservableProperty]
    private bool _showSource;

    public MarkdownTabViewModel(string filePath)
    {
        _filePath = filePath;
        Title = System.IO.Path.GetFileName(filePath);
    }

    public override TabKind Kind => TabKind.Markdown;

    public override string? Location => FilePath;

    public string Folder => System.IO.Path.GetDirectoryName(FilePath) ?? "";

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>문서 안의 상대 링크(.md)를 같은 탭에서 연다.</summary>
    public void OpenLinked(string path)
    {
        _back.Push(FilePath);
        _forward.Clear();
        Show(path);
    }

    public void GoBack()
    {
        if (_back.Count > 0)
        {
            _forward.Push(FilePath);
            Show(_back.Pop());
        }
    }

    public void GoForward()
    {
        if (_forward.Count > 0)
        {
            _back.Push(FilePath);
            Show(_forward.Pop());
        }
    }

    private void Show(string path)
    {
        FilePath = path;
        Title = System.IO.Path.GetFileName(path);
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }
}
