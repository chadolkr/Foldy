using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Foldy.Presentation.Preview;
using Foldy.Presentation.Tabs;
using Foldy.UI.Imaging;
using Foldy.UI.Markdown;

namespace Foldy.UI.Preview;

/// <summary>
/// 미리보기 창 (설계서 9장). DataContext = <see cref="PreviewViewModel"/>. 호스트(패널)는 F3로 이 창을 패널 오른쪽에
/// 켜고 끄며, 포커스된 파일이 바뀔 때마다 <see cref="PreviewViewModel.ShowAsync(PreviewRequest?)"/>를 부른다 (docs/preview.md).
/// 종류별 뷰(텍스트·Markdown·이미지·셸 처리기)는 처음 쓸 때 만들고, 다른 종류로 바뀌면 이전 뷰를 비운다
/// (Markdown 파일 감시, 셸 처리기 Unload). UI 스레드 전용.
/// </summary>
public partial class PreviewPane : UserControl
{
    private PreviewViewModel? _vm;
    private TextPreviewView? _text;
    private MarkdownView? _markdown;
    private ImagePreviewPane? _image;
    private ShellPreviewHost? _shell;
    private PreviewContent? _shown;

    public PreviewPane()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => ClearViews();
    }

    /// <summary>현재 보여 주는 종류 (시험용).</summary>
    public PreviewKind ShownKind => _shown?.Kind ?? PreviewKind.None;

    public TextPreviewView? TextView => _text;

    public MarkdownView? MarkdownViewer => _markdown;

    public ImagePreviewPane? ImageView => _image;

    public ShellPreviewHost? ShellHost => _shell;

    /// <summary>머리 줄 둘째 줄 (크기 · 수정일), 시험용.</summary>
    public string HeaderInfo => InfoText.Text;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = e.NewValue as PreviewViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
        }

        Refresh();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_vm is not { } vm)
        {
            ClearViews();
            EmptyPanel.Visibility = Visibility.Visible;
            return;
        }

        EmptyPanel.Visibility = vm.Status == PreviewStatus.Empty ? Visibility.Visible : Visibility.Collapsed;

        InfoText.Text = string.Join(" · ", new[] { vm.SizeText, vm.ModifiedText }.Where(s => s.Length > 0));
        HeaderBar.Visibility = vm.Status == PreviewStatus.Empty ? Visibility.Collapsed : Visibility.Visible;

        switch (vm.Status)
        {
            case PreviewStatus.Loading:
                // 읽는 동안은 이전 내용을 그대로 둔다 (깜박임 방지).
                return;
            case PreviewStatus.Ready when vm.Content is { } content:
                ShowContent(content);
                StatusPanel.Visibility = Visibility.Collapsed;
                return;
            default:
                ClearViews();
                StatusPanel.Visibility = vm.Status == PreviewStatus.Empty ? Visibility.Collapsed : Visibility.Visible;
                ForceButton.Visibility = vm.CanForce ? Visibility.Visible : Visibility.Collapsed;
                return;
        }
    }

    private void ShowContent(PreviewContent content)
    {
        if (_shown is not null && _shown.Kind != content.Kind)
        {
            ClearViews();
        }

        _shown = content;
        UIElement? active = null;
        switch (content)
        {
            case TextPreviewContent t:
                _text ??= Attach(new TextPreviewView());
                _text.Show(t);
                active = _text;
                break;
            case FilePreviewContent { Kind: PreviewKind.Markdown } f:
                _markdown ??= Attach(new MarkdownView());
                // 같은 렌더러. 파일이 바뀌면 새 ViewModel을 넣어 다시 읽는다.
                _markdown.DataContext = new MarkdownTabViewModel(f.Request.Path);
                active = _markdown;
                break;
            case FilePreviewContent { Kind: PreviewKind.Image } f:
                _image ??= Attach(new ImagePreviewPane());
                _image.AllowContentDecode = f.AllowContentRead;
                _image.Show(new ImageItemInfo(f.Request.Path, f.Request.Name, f.Request.ModifiedUtc, f.Request.Length, f.Request.Attributes));
                active = _image;
                break;
            case FilePreviewContent { Kind: PreviewKind.Shell } f:
                if (_shell is null)
                {
                    _shell = Attach(new ShellPreviewHost());
                    _shell.Failed += OnShellFailed;
                }

                _shell.Open(f.Request.Path);
                active = _shell;
                break;
        }

        foreach (UIElement child in ContentHost.Children)
        {
            child.Visibility = child == active ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnShellFailed(object? sender, string message) => _vm?.ReportFailure(message);

    private T Attach<T>(T view)
        where T : UIElement
    {
        ContentHost.Children.Add(view);
        return view;
    }

    /// <summary>모든 종류별 뷰를 비운다: 처리기 Unload, Markdown 파일 감시 해제, 이미지 해제.</summary>
    private void ClearViews()
    {
        _shown = null;
        _text?.Clear();
        _shell?.Clear();
        _image?.Show(null);
        if (_markdown is not null)
        {
            _markdown.DataContext = null;
        }

        // 보이지 않는 뷰는 접어서 에어스페이스(HwndHost)가 다른 내용을 덮지 못하게 한다.
        foreach (UIElement child in ContentHost.Children)
        {
            child.Visibility = Visibility.Collapsed;
        }
    }
}
