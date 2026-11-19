using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foldy.Presentation.Files;

namespace Foldy.Presentation.Preview;

/// <summary>
/// 미리보기 창 하나의 상태 (패널마다 하나, F3로 켜고 끈다). 호스트가 포커스된 파일이 바뀔 때마다
/// <see cref="ShowAsync"/>를 부른다. 빠르게 지나가는 선택은 <see cref="Debounce"/> 동안 기다렸다가 마지막 것만 읽고,
/// 이전 읽기는 취소한다. UI 스레드 전용 (SynchronizationContext로 돌아온다).
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    private readonly PreviewProviderRegistry _registry;
    private CancellationTokenSource? _cts;
    private int _generation;

    [ObservableProperty]
    private PreviewRequest? _current;

    [ObservableProperty]
    private PreviewKind _kind;

    [ObservableProperty]
    private PreviewStatus _status = PreviewStatus.Empty;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _canForce;

    [ObservableProperty]
    private PreviewContent? _content;

    [ObservableProperty]
    private string _fileName = "";

    [ObservableProperty]
    private string _sizeText = "";

    [ObservableProperty]
    private string _modifiedText = "";

    public PreviewViewModel(PreviewProviderRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ForceCommand = new RelayCommand(() => _ = ForceAsync(), () => CanForce);
    }

    /// <summary>[미리보기] 버튼 (네트워크 드라이브).</summary>
    public IRelayCommand ForceCommand { get; }

    /// <summary>원격(gdrive://) 파일을 로컬 캐시 경로로 내려받는 함수. 앱이 Google Drive를 쓸 수 있을 때 넣는다.</summary>
    public static Func<string, CancellationToken, Task<string>>? RemoteFetch { get; set; }

    /// <summary>선택이 바뀐 뒤 이 시간 동안 더 바뀌지 않으면 읽기 시작.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(120);

    /// <summary>비어 있거나 읽는 중이 아닌, 마지막 읽기가 끝난 상태에서 true.</summary>
    public bool IsIdle => Status is not (PreviewStatus.Loading);

    partial void OnCanForceChanged(bool value) => ForceCommand.NotifyCanExecuteChanged();

    /// <summary>호스트가 쓰는 짧은 진입점: 목록 항목에서.</summary>
    public Task ShowAsync(FileItem? item, Func<string, bool>? isRemote = null) =>
        ShowAsync(item is null ? null : PreviewRequest.From(item, isRemote));

    /// <summary>파일을 보여 준다. null이면 비운다. 읽기가 끝나(또는 취소돼) 반환한다.</summary>
    public async Task ShowAsync(PreviewRequest? request)
    {
        var gen = Interlocked.Increment(ref _generation);
        CancelPending();

        if (request is null)
        {
            Reset();
            return;
        }

        SetHeader(request);
        var decision = PreviewPolicy.Evaluate(request, _registry);
        if (!decision.IsReady)
        {
            Apply(request, decision.Status, decision.StatusText, decision.CanForce, null);
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        Apply(request, PreviewStatus.Loading, PreviewMessages.Loading, false, null);
        try
        {
            if (Debounce > TimeSpan.Zero)
            {
                await Task.Delay(Debounce, ct).ConfigureAwait(true);
            }

            var provider = decision.Provider!;
            if (RemoteFetch is { } fetch && request.Path.StartsWith("gdrive://", StringComparison.OrdinalIgnoreCase))
            {
                // Google Drive: [미리보기]를 눌렀을 때만 캐시로 내려받아 로컬 파일처럼 읽는다.
                var local = await fetch(request.Path, ct).ConfigureAwait(true);
                request = request with { Path = local, IsRemote = false };
            }

            // 공급자는 디스크를 읽으므로 UI 스레드를 비킨다.
            var content = await Task.Run(() => provider.LoadAsync(request, ct), ct).ConfigureAwait(true);
            if (gen != Volatile.Read(ref _generation))
            {
                return;
            }

            if (content is UnavailableContent u)
            {
                Apply(request, PreviewStatus.NotPreviewable, u.Reason, false, null);
            }
            else
            {
                Apply(request, PreviewStatus.Ready, "", false, content);
            }
        }
        catch (OperationCanceledException)
        {
            // 더 새로운 선택이 이 읽기를 대체했다.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or HttpRequestException)
        {
            if (gen == Volatile.Read(ref _generation))
            {
                Apply(request, PreviewStatus.Failed, PreviewMessages.NotPreviewable, false, null);
            }
        }
    }

    /// <summary>[미리보기]: 현재 파일의 네트워크 보호를 이번 한 번 푼다.</summary>
    public Task ForceAsync() =>
        Current is { } cur && CanForce ? ShowAsync(cur with { UserForced = true }) : Task.CompletedTask;

    /// <summary>뷰(셸 처리기 호스트 등)가 표시에 실패했음을 알린다 → 상태 줄로 바꾼다.</summary>
    public void ReportFailure(string? statusText = null)
    {
        if (Current is { } cur)
        {
            Apply(cur, PreviewStatus.Failed, statusText ?? PreviewMessages.Failed, false, null);
        }
    }

    private void CancelPending()
    {
        var old = Interlocked.Exchange(ref _cts, null);
        if (old is not null)
        {
            old.Cancel();
            old.Dispose();
        }
    }

    private void Reset()
    {
        Current = null;
        FileName = SizeText = ModifiedText = "";
        Apply(null, PreviewStatus.Empty, "", false, null);
    }

    private void SetHeader(PreviewRequest r)
    {
        FileName = r.Name;
        SizeText = r.IsFolder ? "" : FileItem.FormatBytes(r.Length);
        ModifiedText = r.ModifiedUtc == default
            ? ""
            : r.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    private void Apply(PreviewRequest? request, PreviewStatus status, string text, bool canForce, PreviewContent? content)
    {
        Current = request;
        Status = status;
        StatusText = text;
        CanForce = canForce;
        Content = content;
        Kind = content?.Kind ?? PreviewKind.None;
        OnPropertyChanged(nameof(IsIdle));
    }
}
