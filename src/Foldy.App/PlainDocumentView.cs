using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Foldy.Presentation.Tabs;

namespace Foldy.App;

/// <summary>
/// Markdown 모듈(Foldy.UI.Markdown)이 합쳐지기 전까지 쓰는 간단한 문서 보기: 원문을 고정폭 글꼴로 보여준다.
/// 파일 읽기는 백그라운드에서 한다.
/// </summary>
public sealed class PlainDocumentView : ContentControl
{
    public PlainDocumentView(MarkdownTabViewModel tab)
    {
        var text = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16),
            Text = "불러오는 중…",
        };
        text.SetResourceReference(TextBox.FontFamilyProperty, "Font.Mono");
        text.SetResourceReference(TextBox.BackgroundProperty, "Brush.Window");
        Content = text;
        _ = LoadAsync(tab.FilePath, text);
    }

    private static async Task LoadAsync(string path, TextBox target)
    {
        try
        {
            var s = await Task.Run(() => File.ReadAllText(path, Encoding.UTF8));
            target.Text = s;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            target.Text = "열 수 없습니다: " + ex.Message;
        }
    }
}
