namespace Foldy.Presentation.Preview;

/// <summary>Markdown: 내장 뷰어(5.9)의 렌더러를 미리보기 창에서도 쓴다. 읽기와 렌더는 MarkdownView가 한다.</summary>
public sealed class MarkdownPreviewProvider : IPreviewProvider
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd" };

    public string Id => "markdown";

    public PreviewKind Kind => PreviewKind.Markdown;

    public int Order => 10;

    // MarkdownView는 1MB 넘는 문서를 나눠 이어 붙이지만, 미리보기에서는 8MB까지만.
    public long MaxBytes => 8L * 1024 * 1024;

    public bool CanPreview(PreviewRequest request) => Extensions.Contains(request.Extension);

    public Task<PreviewContent> LoadAsync(PreviewRequest request, CancellationToken ct) =>
        Task.FromResult<PreviewContent>(new FilePreviewContent(PreviewKind.Markdown, request, AllowContentRead: !PreviewGuards.IsCloudOnly(request.Attributes)));
}

/// <summary>
/// 이미지: ImagePreviewPane(5.12)을 재사용. 클라우드 전용 파일은 내용을 읽지 않고 셸 썸네일만 쓴다
/// (<see cref="FilePreviewContent.AllowContentRead"/> = false).
/// </summary>
public sealed class ImagePreviewProvider(Func<string, bool>? isImage = null) : IPreviewProvider
{
    private static readonly HashSet<string> BaseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".webp", ".ico",
    };

    private readonly Func<string, bool> _isImage = isImage ?? (ext => BaseExtensions.Contains(ext));

    public string Id => "image";

    public PreviewKind Kind => PreviewKind.Image;

    public int Order => 20;

    public long MaxBytes => 128L * 1024 * 1024;

    public bool CanPreview(PreviewRequest request) => request.Extension.Length > 0 && _isImage(request.Extension);

    public Task<PreviewContent> LoadAsync(PreviewRequest request, CancellationToken ct) =>
        Task.FromResult<PreviewContent>(new FilePreviewContent(PreviewKind.Image, request, AllowContentRead: !PreviewGuards.IsCloudOnly(request.Attributes)));
}

/// <summary>텍스트·코드: 앞 1MB를 읽어 UTF-8/UTF-16/CP949를 판별한다. 바이너리면 미리 볼 수 없음.</summary>
public sealed class TextPreviewProvider : IPreviewProvider
{
    private static readonly Dictionary<string, string?> Extensions = Build();

    // 확장자 없는 알려진 파일 이름
    private static readonly HashSet<string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "LICENSE", "README", "NOTICE", "AUTHORS", "CHANGELOG", "Makefile", "Dockerfile", "CMakeLists.txt",
    };

    public string Id => "text";

    public PreviewKind Kind => PreviewKind.Text;

    public int Order => 30;

    // 큰 로그 파일도 앞부분만 읽으므로 크기 제한이 없다.
    public long MaxBytes => 0;

    public int ReadLimit { get; init; } = TextFileReader.DefaultLimit;

    public bool CanPreview(PreviewRequest request)
    {
        var ext = request.Extension;
        return ext.Length > 0 ? Extensions.ContainsKey(ext) : KnownNames.Contains(request.Name) || request.Name.StartsWith('.');
    }

    /// <summary>CodeHighlighter 언어 키 (모르면 null = 강조 없음).</summary>
    public static string? LanguageFor(string extension) =>
        Extensions.TryGetValue(extension, out var lang) ? lang : null;

    public async Task<PreviewContent> LoadAsync(PreviewRequest request, CancellationToken ct)
    {
        var r = await TextFileReader.ReadAsync(request.Path, ReadLimit, ct).ConfigureAwait(false);
        return r.Text is null
            ? new UnavailableContent(PreviewMessages.NotPreviewable)
            : new TextPreviewContent(r.Text, r.EncodingName, r.Truncated, r.TotalBytes, LanguageFor(request.Extension));
    }

    private static Dictionary<string, string?> Build()
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Add(string? lang, string exts)
        {
            foreach (var e in exts.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                d["." + e] = lang;
            }
        }

        Add(null, "txt text log ini cfg conf config properties env csv tsv gitignore gitattributes editorconfig diff patch rst tex srt vtt");
        Add("cs", "cs csx");
        Add("js", "js mjs cjs jsx ts tsx");
        Add("py", "py pyw");
        Add("rs", "rs");
        Add("json", "json jsonc json5 webmanifest");
        Add("sh", "sh bash zsh");
        Add("ps1", "ps1 psm1 psd1 bat cmd");
        Add("sql", "sql");
        Add("xml", "xml xaml csproj fsproj vbproj props targets slnx sln resx config svg html htm xhtml plist manifest wxs");
        Add(null, "yaml yml toml css scss less c h cpp hpp cc cxx java kt go rb php swift lua pl r dart vue gradle cmake mk");
        return d;
    }
}

/// <summary>
/// Windows 미리보기 처리기 호스트로 보여 주는 나머지 형식 (PDF, Office 등). 처리기가 있는지는
/// <paramref name="handlerAvailable"/>(Platform의 레지스트리 조회·블랙리스트)로 확인한다.
/// 타사 DLL을 부르므로 크기 제한을 두고, 클라우드 전용 파일은 정책 단계에서 막힌다.
/// </summary>
public sealed class ShellPreviewProvider(Func<string, bool> handlerAvailable) : IPreviewProvider
{
    public string Id => "shell";

    public PreviewKind Kind => PreviewKind.Shell;

    public int Order => 1000;

    public long MaxBytes => 256L * 1024 * 1024;

    public bool CanPreview(PreviewRequest request) => request.Extension.Length > 0 && handlerAvailable(request.Extension);

    public Task<PreviewContent> LoadAsync(PreviewRequest request, CancellationToken ct) =>
        Task.FromResult<PreviewContent>(new FilePreviewContent(PreviewKind.Shell, request, AllowContentRead: true));
}
