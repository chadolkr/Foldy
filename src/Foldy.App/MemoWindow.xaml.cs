using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Foldy.Core.Vault;
using Foldy.UI.Markdown;
using Foldy.UI.Theming;

namespace Foldy.App;

/// <summary>
/// 메모 창: [쓰기] 글과 미리 보기를 나란히, [보기] Markdown 뷰어만. 그림은 Ctrl+V·끌어 놓기·[화면 캡처](Windows 캡처 도구)로 넣는다.
/// - 잠긴 메모(.memo.foldylock): 내용은 메모리에만, 저장할 때마다 같은 암호로 다시 암호화 (평문·임시 파일 없음). 그림은 안에 함께.
///   5분 동안 손대지 않으면 저장하고 닫는다.
/// - 보통 메모: 그림이 없으면 .md, 그림을 넣는 순간 .textpack(TextBundle: text.md + assets/)으로 바꿔 저장한다. 그림은 원본 화질 그대로.
/// </summary>
public partial class MemoWindow : Window
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);

    private string _path;
    private readonly LockedNote _note;
    private readonly bool _locked;
    private readonly DispatcherTimer? _idle;
    private readonly DispatcherTimer? _render;
    private DispatcherTimer? _capture;
    private string? _password;
    private bool _dirty;
    private DateTime _lastInput = DateTime.UtcNow;

    /// <summary>잠긴 메모.</summary>
    public MemoWindow(string path, LockedNote note, string password)
        : this(path, note, password, startInView: false)
    {
    }

    private MemoWindow(string path, LockedNote note, string? password, bool startInView)
    {
        InitializeComponent();
        App.ApplyScale(this);
        _path = path;
        _note = note;
        _password = password;
        _locked = password is not null;
        Title = _locked
            ? Path.GetFileName(path).Replace(SecureZip.MemoSuffix, "", StringComparison.OrdinalIgnoreCase) + " - 잠긴 메모"
            : Path.GetFileNameWithoutExtension(path) + " - 메모";
        LockIcon.Visibility = _locked ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Content = _locked ? "잠그고 닫기" : "닫기";
        CloseButton.ToolTip = _locked ? "저장하고 닫아요 (다시 열 때 암호가 필요해요)" : "저장하고 닫아요";
        StatusText.Text = _locked
            ? "잠긴 메모 · 그림은 Ctrl+V나 [화면 캡처]로 · 5분 동안 손대지 않으면 저장하고 잠가요"
            : "메모 · 그림은 Ctrl+V나 [화면 캡처]로 넣어요 (그림이 들어가면 .textpack 한 파일로 저장돼요)";
        Editor.Text = note.Markdown;
        _dirty = false;
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
        PreviewKeyDown += OnWindowKey;
        _render = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { _render?.Stop(); RenderPreview(); }, Dispatcher);
        if (_locked)
        {
            _idle = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, OnIdleTick, Dispatcher);
            _idle.Start();
        }

        PreviewMouseMove += (_, _) => _lastInput = DateTime.UtcNow;
        Closing += OnClosing;
        Loaded += (_, _) =>
        {
            ViewToggle.IsChecked = startInView;
            ApplyMode();
            if (!startInView)
            {
                Editor.Focus();
            }
        };
    }

    /// <summary>보통 메모(.md)를 쓰기로 연다. 파일이 없으면 빈 메모로 만든다.</summary>
    public static MemoWindow OpenMarkdown(string path, bool startInView = false)
    {
        var note = TextPack.Is(path) ? TextPack.Load(path) : new LockedNote { Markdown = File.Exists(path) ? File.ReadAllText(path) : "" };
        return new MemoWindow(path, note, null, startInView);
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow - _lastInput > IdleLimit)
        {
            Close(); // 닫히면서 저장하고 암호를 잊는다
        }
    }

    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        _lastInput = DateTime.UtcNow;
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Save();
            e.Handled = true;
        }
    }

    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage())
        {
            PasteImage();
            e.Handled = true;
        }
    }

    private bool PasteImage()
    {
        if (Clipboard.GetImage() is not { } img)
        {
            return false;
        }

        InsertImage(Png(img), ".png"); // 원본 화질 그대로 (PNG는 손실 없음)
        return true;
    }

    // ───────────── 화면 캡처: Windows 캡처 도구를 불러, 캡처가 클립보드에 들어오면 바로 넣는다 ─────────────

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private void OnCapture(object sender, RoutedEventArgs e)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = "캡처 도구를 열지 못했어요. Win+Shift+S로 캡처한 뒤 Ctrl+V로 붙여 넣어 주세요.";
            return;
        }

        var seq = GetClipboardSequenceNumber();
        var until = DateTime.UtcNow.AddMinutes(1);
        StatusText.Text = "캡처할 곳을 골라 주세요. 캡처하면 바로 메모에 들어가요.";
        _capture?.Stop();
        _capture = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            if (DateTime.UtcNow > until)
            {
                _capture?.Stop();
                StatusText.Text = "캡처를 그만뒀어요.";
                return;
            }

            if (GetClipboardSequenceNumber() != seq && Clipboard.ContainsImage())
            {
                _capture?.Stop();
                if (PasteImage())
                {
                    StatusText.Text = "캡처한 그림을 넣었어요.";
                    Activate();
                }
            }
        }, Dispatcher);
        _capture.Start();
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
        // 잠긴 메모는 암호화해서 안에, 보통 메모는 .textpack의 assets/ 안에
        var link = _note.AddImage(bytes, ext, _locked ? SecureZip.ImagesFolder : TextPack.Assets);
        var text = $"\n![그림]({link})\n";
        var at = Editor.CaretIndex;
        Editor.Text = Editor.Text.Insert(at, text);
        Editor.CaretIndex = at + text.Length;
        RenderPreview();
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
        var ast = MarkdownParsing.Parse(Editor.Text);
        var renderer = new MarkdownFlowRenderer(new MarkdownRenderOptions
        {
            Dark = ThemeManager.IsDark,
            BaseFolder = Path.GetDirectoryName(_path) ?? "",
            AllowRemoteImages = false, // 원격 그림은 불러오지 않는다
            InMemoryImage = url => _note.Images.TryGetValue(url, out var b) ? b : null,
        }, MarkdownOutline.Extract(ast));
        Preview.Document = renderer.Render(ast);
    }

    // [쓰기]: 글 + 미리 보기 / [보기]: 미리 보기(Markdown 뷰어)만
    private void OnModeToggle(object sender, RoutedEventArgs e) => ApplyMode();

    private void ApplyMode()
    {
        if (EditorColumn is null)
        {
            return;
        }

        var view = ViewToggle.IsChecked == true;
        ViewToggle.Content = view ? "쓰기로" : "보기로";
        EditorColumn.Width = view ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Editor.Visibility = view ? Visibility.Collapsed : Visibility.Visible;
        CaptureButton.IsEnabled = !view;
        RenderPreview();
    }

    private bool Save()
    {
        try
        {
            _note.Markdown = Editor.Text;
            if (_locked)
            {
                if (_password is null)
                {
                    return false;
                }

                _note.Save(_path, _password);
                StatusText.Text = $"암호를 걸어 저장했어요 ({DateTime.Now:HH:mm})";
            }
            else
            {
                var usesImages = _note.Images.Keys.Any(k => Editor.Text.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (TextPack.Is(_path))
                {
                    TextPack.Save(_path, _note);
                    StatusText.Text = $"저장했어요 ({DateTime.Now:HH:mm})";
                }
                else if (usesImages)
                {
                    // 그림이 들어왔다: 같은 이름의 .textpack으로 저장하고, 다 쓴 뒤에만 예전 .md를 지운다
                    var pack = SecureZip.Unique(Path.ChangeExtension(_path, TextPack.Extension));
                    TextPack.Save(pack, _note);
                    File.Delete(_path);
                    _path = pack;
                    Title = Path.GetFileNameWithoutExtension(pack) + " - 메모";
                    StatusText.Text = $"그림이 들어가서 \"{Path.GetFileName(pack)}\" 한 파일로 저장했어요 ({DateTime.Now:HH:mm})";
                }
                else
                {
                    File.WriteAllText(_path, Editor.Text);
                    StatusText.Text = $"저장했어요 ({DateTime.Now:HH:mm})";
                }
            }

            _dirty = false;
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
            var r = MessageBox.Show(this, "저장하지 못했어요. 그래도 닫을까요? 고친 내용은 사라져요.", "메모", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _idle?.Stop();
        _render?.Stop();
        _capture?.Stop();
        if (_locked)
        {
            _password = null; // 암호를 잊는다
            SecureZip.ForgetKeys(); // 암호로 만든 열쇠도 잊는다
            Editor.Clear();
            _note.Markdown = "";
            _note.Images.Clear();
            Preview.Document = null;
        }
    }
}
