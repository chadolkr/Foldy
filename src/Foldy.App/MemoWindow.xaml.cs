using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Foldy.Core.Vault;
using Foldy.UI.Markdown;
using Foldy.UI.Theming;

namespace Foldy.App;

/// <summary>
/// 잠긴 메모 편집 창. 내용은 메모리에만 있고, 저장할 때마다 같은 암호로 다시 암호화해 .memo.foldylock에 쓴다
/// (평문 파일·임시 파일을 만들지 않는다). 그림은 Ctrl+V·끌어 놓기로 넣고 images/ 안에 함께 담긴다.
/// 5분 동안 입력이 없으면 저장하고 닫는다.
/// </summary>
public partial class MemoWindow : Window
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);

    private readonly string _path;
    private readonly LockedNote _note;
    private readonly DispatcherTimer _idle;
    private readonly DispatcherTimer? _render;
    private string? _password;
    private bool _dirty;

    public MemoWindow(string path, LockedNote note, string password)
    {
        InitializeComponent();
        _path = path;
        _note = note;
        _password = password;
        Title = Path.GetFileName(path).Replace(SecureZip.MemoSuffix, "", StringComparison.OrdinalIgnoreCase) + " - 잠긴 메모";
        Editor.Text = note.Markdown;
        Editor.TextChanged += (_, _) =>
        {
            _dirty = true;
            _render?.Stop();
            _render?.Start();
        };
        Editor.PreviewKeyDown += OnEditorKey;
        Editor.PreviewDragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Editor.PreviewDrop += OnDrop;
        _render = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { _render?.Stop(); RenderPreview(); }, Dispatcher);
        _idle = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, OnIdleTick, Dispatcher);
        _idle.Start();
        PreviewMouseMove += (_, _) => _lastInput = DateTime.UtcNow;
        PreviewKeyDown += (_, _) => _lastInput = DateTime.UtcNow;
        Closing += OnClosing;
        Loaded += (_, _) =>
        {
            RenderPreview();
            Editor.Focus();
        };
    }

    private DateTime _lastInput = DateTime.UtcNow;

    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow - _lastInput > IdleLimit)
        {
            Close(); // 닫히면서 저장하고 암호를 잊는다
        }
    }

    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Save();
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage())
        {
            if (Clipboard.GetImage() is { } img)
            {
                InsertImage(Png(img), ".png");
            }

            e.Handled = true;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp")
            {
                try
                {
                    InsertImage(File.ReadAllBytes(f), ext);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    StatusText.Text = "그림을 읽지 못했어요: " + ex.Message;
                }
            }
        }

        e.Handled = true;
    }

    private void InsertImage(byte[] bytes, string ext)
    {
        var name = _note.AddImage(bytes, ext);
        var text = $"\n![그림]({name})\n";
        var at = Editor.CaretIndex;
        Editor.Text = Editor.Text.Insert(at, text);
        Editor.CaretIndex = at + text.Length;
    }

    private static byte[] Png(BitmapSource img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private void RenderPreview()
    {
        if (PreviewToggle.IsChecked != true)
        {
            return;
        }

        var ast = MarkdownParsing.Parse(Editor.Text);
        var renderer = new MarkdownFlowRenderer(new MarkdownRenderOptions
        {
            Dark = ThemeManager.IsDark,
            InMemoryImage = url => _note.Images.TryGetValue(url, out var b) ? b : null,
        }, MarkdownOutline.Extract(ast));
        Preview.Document = renderer.Render(ast);
    }

    private void OnPreviewToggle(object sender, RoutedEventArgs e)
    {
        if (PreviewColumn is null)
        {
            return;
        }

        PreviewColumn.Width = PreviewToggle.IsChecked == true ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RenderPreview();
    }

    private bool Save()
    {
        if (_password is null)
        {
            return false;
        }

        try
        {
            _note.Markdown = Editor.Text;
            _note.Save(_path, _password);
            _dirty = false;
            StatusText.Text = $"암호를 걸어 저장했어요 ({DateTime.Now:HH:mm})";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "저장하지 못했어요: " + ex.Message;
            return false;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private void OnLockClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty && !Save())
        {
            var r = MessageBox.Show(this, "저장하지 못했어요. 그래도 닫을까요? 고친 내용은 사라져요.", "잠긴 메모", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _idle.Stop();
        _render?.Stop();
        _password = null; // 암호를 잊는다
        Editor.Clear();
        _note.Markdown = "";
        _note.Images.Clear();
        Preview.Document = null;
    }
}
