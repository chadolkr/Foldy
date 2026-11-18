using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foldy.Core.Descriptions;

namespace Foldy.Presentation.Templates;

/// <summary>
/// 폴더 설명 편집기 (설계서 5.13 DescriptionEditorViewModel): 위쪽 제목·태그 입력칸, 왼쪽 본문, 오른쪽 미리보기.
/// 머리말은 직접 치지 않아도 된다. 저장은 원자적 쓰기, 쓸 수 없는 폴더는 "이 PC에만 저장됨".
/// </summary>
public sealed partial class DescriptionEditorViewModel : ObservableObject
{
    private readonly FolderDescriptionService _service;
    private FolderDescription _original = new();

    public DescriptionEditorViewModel(FolderDescriptionService service, string folder)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        Folder = folder;
    }

    public string Folder { get; }

    public string FolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder)) is { Length: > 0 } n ? n : Folder;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _tagsText = "";

    [ObservableProperty]
    private string _body = "";

    [ObservableProperty]
    private string? _template;

    [ObservableProperty]
    private DescriptionLocation _location;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool? _dialogResult;

    public bool IsLocalOnly => Location == DescriptionLocation.LocalStore || FolderDescriptionService.MustUseLocalStore(Folder);

    /// <summary>미리보기 (머리말 포함 원문).</summary>
    public string PreviewMarkdown => FolderDescriptionParser.Serialize(Build());

    public string Headline => Build().Headline;

    /// <summary>드라이브 큐에서 호출해도 된다 (속성 변경만 일어남).</summary>
    public void Load()
    {
        var r = _service.Read(Folder);
        _original = r.Description ?? new FolderDescription();
        Location = r.Location;
        Title = _original.Title ?? "";
        TagsText = string.Join(", ", _original.Tags);
        Body = _original.Body;
        Template = _original.Template;
        IsDirty = false;
        OnPropertyChanged(nameof(IsLocalOnly));
    }

    partial void OnTitleChanged(string value) => Touch();

    partial void OnTagsTextChanged(string value) => Touch();

    partial void OnBodyChanged(string value) => Touch();

    private void Touch()
    {
        IsDirty = true;
        OnPropertyChanged(nameof(PreviewMarkdown));
        OnPropertyChanged(nameof(Headline));
        SaveCommand.NotifyCanExecuteChanged();
    }

    public FolderDescription Build() => new()
    {
        Title = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(),
        Tags = FolderDescriptionParser.ParseTags(TagsText),
        Template = Template,
        Body = Body,
        ExtraFrontMatter = _original.ExtraFrontMatter,
    };

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Save()
    {
        try
        {
            var d = Build();
            var empty = d.Title is null && d.Tags.Count == 0 && d.Template is null && string.IsNullOrWhiteSpace(d.Body) && d.ExtraFrontMatter.Count == 0;
            Location = _service.Write(Folder, empty ? null : d);
            IsDirty = false;
            OnPropertyChanged(nameof(IsLocalOnly));
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void Cancel() => DialogResult = false;
}

/// <summary>파일 목록 위의 접힌 설명 띠 (제목 + 첫 문단 두 줄, 템플릿 배지).</summary>
public sealed partial class DescriptionBannerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _headline = "";

    [ObservableProperty]
    private string _firstParagraph = "";

    [ObservableProperty]
    private string? _templateBadge;

    [ObservableProperty]
    private bool _isLocalOnly;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _hasDescription;

    public string FullMarkdown { get; private set; } = "";

    /// <param name="badgeOf">템플릿 id → 배지 문자열 (TemplateLibrary.Find(id)?.DisplayBadge).</param>
    public void Show(FolderDescriptionRead read, Func<string, string?>? badgeOf = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        var d = read.Description;
        HasDescription = d is not null;
        Headline = d?.Headline ?? "";
        FirstParagraph = d?.FirstParagraph() ?? "";
        TemplateBadge = d?.Template is { } t ? badgeOf?.Invoke(t) ?? t : null;
        IsLocalOnly = read.Location == DescriptionLocation.LocalStore;
        FullMarkdown = d?.Body ?? "";
        OnPropertyChanged(nameof(FullMarkdown));
    }
}
