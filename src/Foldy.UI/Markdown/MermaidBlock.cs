using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Foldy.UI.Markdown;

/// <summary>
/// Mermaid 다이어그램 렌더러 (설계서 5.9, v1 초반 추가 예정). 구현체는 WebView2 하나에서 mermaid.min.js(번들, 로컬 전용,
/// securityLevel: strict, CSP로 외부 요청 차단)로 SVG를 그린 뒤 WPF ImageSource로 바꿔 돌려준다.
/// 디스크 캐시(블록 내용 해시 키, thumbs.cache 방식)도 구현체가 맡는다.
/// 등록된 구현체가 없거나(WebView2 런타임 없음, 설정에서 끔) 실패하면 뷰어는 원문 코드 블록만 보여 준다.
/// </summary>
public interface IMermaidRenderer
{
    /// <summary>다이어그램을 그린다. 문법 오류면 예외. 취소 토큰은 2초 시간 제한을 포함한다.</summary>
    Task<ImageSource> RenderAsync(string code, bool dark, CancellationToken cancellationToken);
}

/// <summary>
/// 문서 안의 mermaid 블록 하나. 처음에는 코드 블록으로 그려 두고, 화면에 보이거나 "다이어그램 보기"를 누르면
/// 그때 렌더러를 부른다 (지연 로드). Mermaid가 없는 문서는 이 타입을 전혀 쓰지 않는다.
/// </summary>
public sealed class MermaidBlock
{
    /// <summary>설계서의 렌더 시간 제한.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    // 같은 세션 안에서 다시 열 때 렌더러를 부르지 않도록 메모리 캐시 (키: 내용 해시 + 다크 여부).
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.Ordinal);

    private readonly Section _section;
    private readonly Paragraph _code;
    private readonly Button _toggle;
    private BlockUIContainer? _diagram;
    private Paragraph? _error;
    private bool _busy;

    internal MermaidBlock(string code, Section section, Paragraph codeParagraph, Button toggle)
    {
        Code = code;
        _section = section;
        _code = codeParagraph;
        _toggle = toggle;
    }

    public string Code { get; }

    /// <summary>다이어그램을 한 번이라도 시도했는지 (보일 때 자동 렌더는 한 번만).</summary>
    public bool Attempted { get; private set; }

    public bool ShowingDiagram => _diagram is not null && _section.Blocks.Contains(_diagram);

    /// <summary>보이는지 판단할 때 쓰는 요소 (헤더의 버튼).</summary>
    public FrameworkElement Anchor => _toggle;

    public static string CacheKey(string code, bool dark) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))) + (dark ? ":d" : ":l");

    /// <summary>다이어그램 ↔ 코드 전환 (버튼).</summary>
    public async Task ToggleAsync(IMermaidRenderer renderer, bool dark)
    {
        if (ShowingDiagram)
        {
            ShowCode();
            return;
        }

        await RenderAsync(renderer, dark);
    }

    public async Task RenderAsync(IMermaidRenderer renderer, bool dark)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (_busy)
        {
            return;
        }

        Attempted = true;
        _busy = true;
        _toggle.IsEnabled = false;
        try
        {
            var key = CacheKey(Code, dark);
            if (!Cache.TryGetValue(key, out var image))
            {
                using var cts = new CancellationTokenSource(Timeout);
                image = await renderer.RenderAsync(Code, dark, cts.Token);
                if (image.CanFreeze)
                {
                    image.Freeze();
                }

                Cache[key] = image;
            }

            ShowDiagram(image);
        }
        catch (OperationCanceledException)
        {
            ShowError("시간 초과(2초)");
        }
#pragma warning disable CA1031 // 렌더러(외부 스크립트)의 어떤 실패든 코드 블록으로 되돌아간다
        catch (Exception e)
#pragma warning restore CA1031
        {
            ShowError(e.Message);
        }
        finally
        {
            _busy = false;
            _toggle.IsEnabled = true;
        }
    }

    private void ShowDiagram(ImageSource image)
    {
        RemoveError();
        _diagram = new BlockUIContainer(new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(8),
        });
        _section.Blocks.InsertBefore(_code, _diagram);
        _section.Blocks.Remove(_code);
        _toggle.Content = "코드 보기";
    }

    private void ShowCode()
    {
        if (_diagram is not null)
        {
            _section.Blocks.InsertBefore(_diagram, _code);
            _section.Blocks.Remove(_diagram);
        }

        _toggle.Content = "다이어그램 보기";
    }

    private void ShowError(string message)
    {
        RemoveError();
        _error = new Paragraph(new Run("다이어그램을 그리지 못했습니다: " + message)) { Margin = new Thickness(8, 0, 8, 6) };
        _error.SetResourceReference(TextElement.ForegroundProperty, "Brush.Error");
        _section.Blocks.Add(_error);
    }

    private void RemoveError()
    {
        if (_error is not null)
        {
            _section.Blocks.Remove(_error);
            _error = null;
        }
    }
}
