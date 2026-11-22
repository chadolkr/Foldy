using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Foldy.Core.FileSystem;
using Foldy.Presentation.Files;

namespace Foldy.Presentation.Tabs;

/// <summary>파일 이름 검색. 색인 서비스가 있으면 전체 드라이브 즉시 검색, 없으면 지정 폴더 아래를 직접 찾는다.</summary>
public interface IFileSearchService
{
    /// <summary>색인 서비스로 찾는지 (아니면 폴더를 직접 훑는다).</summary>
    bool IsIndexed { get; }

    IAsyncEnumerable<IReadOnlyList<FsEntry>> SearchAsync(string query, string? scope, CancellationToken ct);
}

/// <summary>검색 결과 탭. 결과는 도착하는 대로 배치로 붙인다.</summary>
public sealed partial class SearchTabViewModel : TabBase
{
    private readonly IFileSearchService _search;
    private readonly Func<FsEntry, string>? _typeName;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _query;

    [ObservableProperty]
    private ItemList _items = ItemList.Empty;

    [ObservableProperty]
    private string _status = "";

    public SearchTabViewModel(IFileSearchService search, string query, string? scope, Func<FsEntry, string>? typeName = null)
    {
        _search = search;
        _query = query;
        _typeName = typeName;
        Scope = scope;
        Title = Foldy.Core.Localization.Loc.F("search.title", query);
    }

    public override TabKind Kind => TabKind.Search;

    public override string? Location => Scope;

    /// <summary>검색 범위 폴더 (색인 검색이면 전체 드라이브라 표시용).</summary>
    public string? Scope { get; }

    public IReadOnlyList<FileItem> Selection { get; set; } = [];

    public override void OnActivated()
    {
        if (Items.Count == 0 && !IsBusy)
        {
            _ = RunAsync();
        }
    }

    public override void Close() => _cts?.Cancel();

    public void Cancel() => _cts?.Cancel();

    public async Task RunAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var list = new ItemList();
        Items = list;
        IsBusy = true;
        var sw = Stopwatch.StartNew();
        Status = _search.IsIndexed ? Foldy.Core.Localization.Loc.T("search.inIndex") : Foldy.Core.Localization.Loc.F("search.under", Scope);
        try
        {
            await foreach (var batch in _search.SearchAsync(Query, Scope, cts.Token))
            {
                list.AddBatch(batch.Select(e => new FileItem(e, _typeName)));
                Status = Foldy.Core.Localization.Loc.F("search.progress", list.Count, sw.Elapsed.TotalSeconds);
            }

            Status = Foldy.Core.Localization.Loc.F("search.done", list.Count, sw.Elapsed.TotalMilliseconds) + (_search.IsIndexed ? Foldy.Core.Localization.Loc.T("search.indexTag") : "");
        }
        catch (OperationCanceledException)
        {
            Status = Foldy.Core.Localization.Loc.F("search.cancelled", list.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = Foldy.Core.Localization.Loc.T("search.error") + ex.Message;
        }
        finally
        {
            if (_cts == cts)
            {
                IsBusy = false;
            }
        }
    }
}
