namespace Foldy.Presentation.Preview;

/// <summary>
/// 미리보기 확장 지점 (설계서 9장 IPreviewHandler: 확장자 → 뷰). 내장 구현은 텍스트, Markdown, 이미지,
/// Windows 미리보기 처리기 호스트. 선택은 <see cref="PreviewProviderRegistry"/>가 한다.
/// </summary>
public interface IPreviewProvider
{
    string Id { get; }

    PreviewKind Kind { get; }

    /// <summary>작을수록 먼저 고려한다 (구체적인 공급자 → 일반 공급자 → 셸 처리기 순).</summary>
    int Order { get; }

    /// <summary>
    /// 이 크기(바이트)를 넘는 파일은 미리 보지 않는다. 0이면 제한 없음 (텍스트는 앞부분만 읽으므로).
    /// </summary>
    long MaxBytes { get; }

    /// <summary>확장자·이름만 보고 판단 (디스크에 접근하지 않는다).</summary>
    bool CanPreview(PreviewRequest request);

    /// <summary>
    /// 내용을 준비한다 (백그라운드 스레드에서 호출됨). 취소되면 <see cref="OperationCanceledException"/>.
    /// 못 보는 파일이면 <see cref="UnavailableContent"/>를 돌려준다.
    /// </summary>
    Task<PreviewContent> LoadAsync(PreviewRequest request, CancellationToken ct);
}

/// <summary>공급자 모음. 요청마다 <see cref="Order"/>가 가장 작은, <see cref="IPreviewProvider.CanPreview"/>가 참인 것을 고른다.</summary>
public sealed class PreviewProviderRegistry
{
    private readonly List<IPreviewProvider> _providers = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<IPreviewProvider> Providers
    {
        get
        {
            lock (_gate)
            {
                return [.. _providers];
            }
        }
    }

    public PreviewProviderRegistry Register(IPreviewProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
        {
            _providers.RemoveAll(p => p.Id == provider.Id);
            _providers.Add(provider);
            _providers.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        return this;
    }

    public IPreviewProvider? Select(PreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var p in Providers)
        {
            if (p.CanPreview(request))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>
    /// 내장 공급자를 모두 등록한 기본 모음. <paramref name="shell"/>은 Windows 처리기 호스트용
    /// (처리기 유무·블랙리스트 확인 함수, Platform 쪽에서 주입). null이면 셸 공급자 없이 텍스트·Markdown·이미지만.
    /// </summary>
    public static PreviewProviderRegistry CreateDefault(Func<string, bool>? shellHandlerAvailable = null, Func<string, bool>? isImage = null)
    {
        var r = new PreviewProviderRegistry()
            .Register(new MarkdownPreviewProvider())
            .Register(new ImagePreviewProvider(isImage))
            .Register(new TextPreviewProvider());
        if (shellHandlerAvailable is not null)
        {
            r.Register(new ShellPreviewProvider(shellHandlerAvailable));
        }

        return r;
    }
}
