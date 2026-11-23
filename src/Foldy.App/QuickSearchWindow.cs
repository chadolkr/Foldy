using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Foldy.Core.FileSystem;
using Foldy.Core.QuickSearch;

namespace Foldy.App;

/// <summary>
/// 빠른 검색 창 (Ctrl+Space, macOS Spotlight처럼): 화면 가운데 큰 입력 칸 하나에서 계산·단위 변환, 앱, Windows 설정,
/// 자주 쓰는 폴더, Foldy 기능, 파일을 함께 찾는다. 맨 위에 "가장 알맞은 것" 하나, 오른쪽에 미리보기와 [열기].
/// Windows 검색처럼 결과를 묶음(앱·설정·폴더·Foldy 기능·파일)으로 나눠 보여 주고, 위의 단추(모두·앱·설정…)나 Tab으로 한 묶음만 볼 수 있다.
/// Enter 열기·실행, Ctrl+Enter 들어 있는 폴더 보기, Esc 또는 다른 곳을 누르면 닫힌다. 밖으로 아무것도 보내지 않는다.
/// </summary>
internal sealed class QuickSearchWindow : Window
{
    /// <summary>
    /// 찾은 것 하나. <see cref="Group"/>은 묶음 이름(계산·앱·설정·폴더·기능·파일), <see cref="Score"/>가 크면 "가장 알맞은 것" 후보.
    /// <see cref="Reveal"/>은 파일·폴더만 (들어 있는 폴더 보기). <see cref="Preview"/>는 오른쪽에 보일 내용.
    /// </summary>
    public sealed record Hit(string Group, string Glyph, string Title, string Detail, Action Run, Action? Reveal = null, int Score = 0, Func<FrameworkElement?>? Preview = null);

    public static string TopGroup => Foldy.Core.Localization.Loc.T("qs.top");
    public const int MaxPerGroup = 5;
    public const int MaxFiles = 30;

    /// <summary>묶음 순서 (Spotlight처럼 바로 답 → 앱 → 설정 → 폴더 → 기능 → 파일).</summary>
    public static readonly string[] GroupOrder = [Foldy.Core.Localization.Loc.T("qs.calc"), Foldy.Core.Localization.Loc.T("qs.units"), Foldy.Core.Localization.Loc.T("sw.qs.app"), Foldy.Core.Localization.Loc.T("qs.grp.settings"), Foldy.Core.Localization.Loc.T("sw.qs.folder"), Foldy.Core.Localization.Loc.T("qs.grp.feature"), Foldy.Core.Localization.Loc.T("sw.qs.file")];

    /// <summary>위의 보기 단추: (단추 글자, 보여 줄 묶음). 묶음이 null이면 모두.</summary>
    public static readonly (string Label, string? Group)[] Filters =
    [
        (Foldy.Core.Localization.Loc.T("qs.filter.all"), null),
        (Foldy.Core.Localization.Loc.T("sw.qs.app"), Foldy.Core.Localization.Loc.T("sw.qs.app")),
        (Foldy.Core.Localization.Loc.T("qs.grp.settings"), Foldy.Core.Localization.Loc.T("qs.grp.settings")),
        (Foldy.Core.Localization.Loc.T("sw.qs.folder"), Foldy.Core.Localization.Loc.T("sw.qs.folder")),
        (Foldy.Core.Localization.Loc.T("qs.filter.files"), Foldy.Core.Localization.Loc.T("sw.qs.file")),
        (Foldy.Core.Localization.Loc.T("qs.grp.feature"), Foldy.Core.Localization.Loc.T("qs.grp.feature")),
    ];

    /// <summary>한 묶음만 볼 때 처음 보여 줄 파일 수 (아래로 내리면 더). 앱·설정·기능은 한 묶음만 보면 모두.</summary>
    public const int MaxFiltered = 50;

    private readonly Func<string, IEnumerable<Hit>> _instant;
    private readonly Func<string, CancellationToken, IAsyncEnumerable<IReadOnlyList<FsEntry>>> _files;
    private readonly Func<FsEntry, Hit> _fileHit;
    private readonly TextBox _input;
    private readonly TextBlock _placeholder;
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;
    private readonly Border _preview;
    private readonly TextBlock _footer;
    private readonly List<(Border Row, Hit Hit)> _hits = [];
    private readonly List<(Button Chip, string? Group)> _chips = [];
    private string? _filter;

    // 파일 결과: 모두 세고, 아래로 내리면 한 묶음씩 더 그린다 (2026-11-23 사용자: "더 보기는 스크롤하면 안 보인다")
    private ResultPager<FsEntry>? _pager;
    private TextBlock? _fileHeader;
    private string _fileHeaderText = "";
    private readonly TextBlock _fileStatus = new() { FontSize = 14, Margin = new Thickness(10, 8, 10, 8), TextWrapping = TextWrapping.Wrap };

    /// <summary>맨 위 결과 줄 (문서 캡처가 짧은 소개의 초점으로 쓴다).</summary>
    internal FrameworkElement? TopResult => _hits.Count > 0 ? _hits[0].Row : null;

    private int _selected = -1;
    private CancellationTokenSource? _cts;
    private bool _closing;
    private DateTime? _activatedAt;
    private readonly bool _animations;
    private readonly SniffDog _dog = new();

    public QuickSearchWindow(
        Func<string, IEnumerable<Hit>> instant,
        Func<string, CancellationToken, IAsyncEnumerable<IReadOnlyList<FsEntry>>> files,
        Func<FsEntry, Hit> fileHit,
        string footer,
        bool animations = true)
    {
        _animations = animations;
        _instant = instant;
        _files = files;
        _fileHit = fileHit;
        Title = Foldy.Core.Localization.Loc.T("qs.title");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 900 * Math.Max(1, App.UiScale);
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(FontFamilyProperty, "Font.UI");
        FontSize = 17;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        _input = new TextBox { FontSize = 26, BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 0, 4, 0) };
        _input.SetResourceReference(ForegroundProperty, "Brush.Text");
        _input.SetResourceReference(TextBox.CaretBrushProperty, "Brush.Text");
        System.Windows.Automation.AutomationProperties.SetName(_input, Foldy.Core.Localization.Loc.T("qs.boxName"));
        _placeholder = new TextBlock { Text = Foldy.Core.Localization.Loc.T("qs.placeholder"), FontSize = 26, IsHitTestVisible = false, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _placeholder.SetResourceReference(ForegroundProperty, "Brush.TextSecondary");
        var icon = new TextBlock { Text = "", FontSize = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 0) };
        icon.SetResourceReference(FontFamilyProperty, "Font.Icon");
        icon.SetResourceReference(ForegroundProperty, "Brush.TextSecondary");
        var inputGrid = new Grid { MinHeight = 56 };
        inputGrid.Children.Add(_placeholder);
        inputGrid.Children.Add(_input);
        var top = new DockPanel { Margin = new Thickness(12, 6, 12, 6) };
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        top.Children.Add(inputGrid);

        // 보기 단추 (Windows 검색처럼): 모두 · 앱 · 설정 · 폴더 · 파일 · Foldy 기능
        var chipRow = new WrapPanel { Margin = new Thickness(14, 0, 14, 8) };
        foreach (var (label, group) in Filters)
        {
            var chip = new Button { Content = label, FontSize = 15, MinHeight = 34, MinWidth = 56, Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0, 0, 6, 0), Focusable = false, Cursor = Cursors.Hand };
            System.Windows.Automation.AutomationProperties.SetName(chip, Foldy.Core.Localization.Loc.F("qs.filter.name", label));
            var g = group;
            chip.Click += (_, _) => SetFilter(g);
            _chips.Add((chip, g));
            chipRow.Children.Add(chip);
        }

        _scroll = new ScrollViewer { Content = _rows, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(6, 0, 6, 0) };
        _scroll.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange > 0 && ResultPaging.NearEnd(_scroll.VerticalOffset, _scroll.ViewportHeight, _scroll.ExtentHeight))
            {
                ShowMoreFiles(); // 아래 끝에 닿으면 더 (페이지 단추 없이)
            }
        };
        _fileStatus.SetResourceReference(ForegroundProperty, "Brush.TextSecondary");
        _preview = new Border { Width = 300, Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(1, 0, 0, 0), Visibility = Visibility.Collapsed };
        _preview.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var middle = new DockPanel();
        DockPanel.SetDock(_preview, Dock.Right);
        middle.Children.Add(_preview);
        middle.Children.Add(_scroll);

        _footer = new TextBlock { Text = footer, FontSize = 14, Margin = new Thickness(16, 6, 16, 10), TextWrapping = TextWrapping.Wrap };
        _footer.SetResourceReference(ForegroundProperty, "Brush.TextSecondary");

        var line = new Border { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(chipRow);
        body.Children.Add(line);
        body.Children.Add(middle);
        body.Children.Add(_dog);
        body.Children.Add(_footer);
        var card = new Border
        {
            Child = body,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.3 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        Content = card;
        if (App.UiScale > 1.001)
        {
            App.ApplyScale(this);
        }

        _input.TextChanged += (_, _) => _ = SearchAsync(_input.Text);
        PreviewKeyDown += OnKey;
        // 다른 곳을 누르면 닫힌다 (Spotlight처럼). 막 뜬 순간의 포커스 흔들림으로 닫히지 않게, 한 번 활성화되고 잠시 뒤부터
        Activated += (_, _) => _activatedAt ??= DateTime.UtcNow;
        Deactivated += (_, _) =>
        {
            if (_activatedAt is { } t && DateTime.UtcNow - t > TimeSpan.FromMilliseconds(400))
            {
                SafeClose();
            }
        };
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + wa.Height * 0.15;
            Activate();
            _input.Focus();
            MarkChips();
            _ = SearchAsync("");
        };
    }

    /// <summary>지금 보는 묶음 (null이면 모두).</summary>
    public string? Filter => _filter;

    /// <summary>한 묶음만 보기 (null이면 모두). 같은 글자로 다시 찾는다.</summary>
    public void SetFilter(string? group)
    {
        _filter = group;
        MarkChips();
        _input.Focus();
        _ = SearchAsync(_input.Text);
    }

    private void MarkChips()
    {
        foreach (var (chip, g) in _chips)
        {
            var on = g == _filter;
            chip.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            chip.BorderThickness = new Thickness(on ? 2 : 1);
            if (on)
            {
                chip.SetResourceReference(Control.BorderBrushProperty, "Brush.Accent");
            }
            else
            {
                chip.SetResourceReference(Control.BorderBrushProperty, "Brush.Border");
            }
        }
    }

    /// <summary>테스트·문서 캡처: 입력 칸에 글자를 넣는다.</summary>
    public void SetQuery(string q) => _input.Text = q;

    public IReadOnlyList<Hit> Hits => [.. _hits.Select(h => h.Hit)];

    /// <summary>테스트·문서 캡처용: 닫히지 않게 (캡처 중에는 다른 창이 앞에 올 수 있다).</summary>
    public bool KeepOpen { get; set; }

    private void SafeClose()
    {
        if (!_closing && !KeepOpen)
        {
            _closing = true;
            _cts?.Cancel();
            Close();
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                KeepOpen = false;
                SafeClose();
                e.Handled = true;
                break;
            case Key.Tab:
                // Tab: 다음 묶음만 보기, Shift+Tab: 앞 묶음 (Windows 검색처럼)
                var i = _chips.FindIndex(c => c.Group == _filter);
                var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                SetFilter(_chips[(i + step + _chips.Count) % _chips.Count].Group);
                e.Handled = true;
                break;
            case Key.Down:
                if (_selected >= _hits.Count - 1)
                {
                    ShowMoreFiles(); // 맨 아래에서 ↓를 누르면 더
                }

                Select(Math.Min(_hits.Count - 1, _selected + 1));
                e.Handled = true;
                break;
            case Key.Up:
                Select(Math.Max(0, _selected - 1));
                e.Handled = true;
                break;
            case Key.Enter:
                if (_selected >= 0 && _selected < _hits.Count)
                {
                    var h = _hits[_selected].Hit;
                    Run(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && h.Reveal is not null ? h.Reveal : h.Run);
                }

                e.Handled = true;
                break;
        }
    }

    private void Run(Action a)
    {
        KeepOpen = false;
        SafeClose();
        a();
    }

    /// <summary>
    /// 바로 찾는 것(계산·앱·설정·폴더·기능)을 묶음으로 나누고, 점수가 가장 높은 하나를 맨 위로.
    /// <paramref name="filter"/>가 있으면 그 묶음만 (모두). <c>Total</c>은 그 묶음에서 찾은 수 (보여 준 것보다 많으면 묶음 끝에 "N개 모두 보기").
    /// </summary>
    public static (Hit? Top, IReadOnlyList<(string Group, IReadOnlyList<Hit> Hits, int Total)> Groups) Arrange(IEnumerable<Hit> hits, string? filter = null)
    {
        var all = hits.Where(h => filter is null || h.Group == filter).ToList();
        var top = all.Where(h => h.Score > 0).OrderByDescending(h => h.Score).ThenBy(h => Array.IndexOf(GroupOrder, h.Group)).FirstOrDefault();
        var take = filter is null ? MaxPerGroup : int.MaxValue;
        var groups = all.Where(h => !ReferenceEquals(h, top))
            .GroupBy(h => h.Group)
            .OrderBy(g => Array.IndexOf(GroupOrder, g.Key) is var i && i < 0 ? 99 : i)
            .Select(g => (g.Key, (IReadOnlyList<Hit>)[.. g.OrderByDescending(h => h.Score).Take(take)], g.Count()))
            .ToList();
        return (top, groups);
    }

    private async Task SearchAsync(string q)
    {
        _placeholder.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _rows.Children.Clear();
        _dog.Reset();
        _hits.Clear();
        _selected = -1;
        _pager = null;
        _fileHeader = null;
        var query = q.Trim();

        var filter = _filter;
        var fileGroup = Foldy.Core.Localization.Loc.T("sw.qs.file");
        var folderGroup = Foldy.Core.Localization.Loc.T("sw.qs.folder");
        var (topHit, groups) = Arrange(_instant(query), filter);
        if (topHit is not null)
        {
            AddHeader(TopGroup);
            AddRow(topHit, big: true);
        }

        foreach (var (group, hits, total) in groups)
        {
            AddHeader(Counted(group, total));
            foreach (var h in hits)
            {
                AddRow(h);
            }

            if (filter is null && total > hits.Count)
            {
                AddSeeAll(group, total); // 묶음 끝에 (아래로 내려도 보이게)
            }
        }

        Select(_hits.Count > 0 ? 0 : -1);
        if (query.Length < 2 || (filter is not null && filter != fileGroup && filter != folderGroup))
        {
            return; // 파일은 두 글자부터 (한 글자는 너무 많이 나온다). 앱·설정·기능만 볼 때는 파일을 찾지 않는다
        }

        var pager = new ResultPager<FsEntry>(filter is null ? MaxFiles : MaxFiltered);
        _fileHeaderText = filter == folderGroup ? folderGroup : fileGroup;

        try
        {
            await Task.Delay(180, cts.Token); // 빨리 치는 동안은 기다린다
            _pager = pager;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await foreach (var batch in _files(query, cts.Token))
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                // 폴더만 볼 때는 폴더, 파일만 볼 때는 파일만
                IReadOnlyList<FsEntry> wanted = filter == folderGroup ? batch.Where(f => f.Kind == FsKind.Folder).ToList()
                    : filter == fileGroup ? batch.Where(f => f.Kind != FsKind.Folder).ToList()
                    : batch;
                pager.Add(wanted); // 모두 센다. 그리는 건 처음 몇 개, 아래로 내리면 더
                ShowMoreFiles(wantMore: false);
                if (_selected < 0 && _hits.Count > 0)
                {
                    Select(0);
                }
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            pager.Finish();
            UpdateFileStatus();
            var total = pager.Total;
            if (total > 0 && !cts.IsCancellationRequested)
            {
                _dog.PlayFound(total, sw.Elapsed, _animations); // 냄새 잘 맡는 개: 킁킁 → 쌩 → 물어 옴
            }

            if (_hits.Count == 0 && !cts.IsCancellationRequested)
            {
                _dog.PlayNotFound(_animations); // 고개 갸웃
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddHeader(Foldy.Core.Localization.Loc.T("qs.fileError") + ex.Message);
        }
    }

    /// <summary>묶음 이름에 찾은 수를 붙인다 ("파일 · 220개"). 계산·단위 변환은 답 하나라 붙이지 않는다.</summary>
    private static string Counted(string group, long total) =>
        group == Foldy.Core.Localization.Loc.T("qs.calc") || group == Foldy.Core.Localization.Loc.T("qs.units")
            ? group
            : Foldy.Core.Localization.Loc.F("qs.groupCount", group, total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));

    /// <summary>묶음 이름 줄.</summary>
    private TextBlock AddHeader(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 10, 10, 4) };
        t.SetResourceReference(ForegroundProperty, "Brush.TextSecondary");
        _rows.Children.Add(t);
        return t;
    }

    /// <summary>
    /// 묶음 끝의 "12개 모두 보기 ›" (누르면 그 묶음만 본다). 예전에는 묶음 이름 오른쪽에 "더 보기"가 있어서
    /// 아래로 내리면 안 보였다 (2026-11-23 사용자). 이제 그 묶음을 다 본 자리, 눈이 닿는 곳에 둔다.
    /// </summary>
    private void AddSeeAll(string group, long total)
    {
        var text = Foldy.Core.Localization.Loc.F("qs.seeAll", total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));
        var link = new TextBlock { Text = text + " ›", FontSize = 15, Cursor = Cursors.Hand, Padding = new Thickness(40, 6, 10, 8) };
        link.SetResourceReference(ForegroundProperty, "Brush.Accent");
        System.Windows.Automation.AutomationProperties.SetName(link, $"{group}: {text}");
        link.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetFilter(group); };
        _rows.Children.Add(link);
    }

    /// <summary>
    /// 들고 있는 파일 결과를 더 그린다. <paramref name="wantMore"/>면 한 묶음(50개) 더 (아래 끝에 닿음·맨 아래에서 ↓),
    /// 아니면 새로 찾은 것 중 아직 보여 줄 몫만 (처음 30·50개).
    /// </summary>
    private void ShowMoreFiles(bool wantMore = true)
    {
        if (_pager is not { } pager)
        {
            return;
        }

        if (wantMore)
        {
            pager.WantMore();
        }

        foreach (var f in pager.TakeNew())
        {
            if (_hits.Count == 0)
            {
                AddHeader(TopGroup); // 바로 찾은 게 없으면 첫 파일이 가장 알맞은 것
                AddRow(_fileHit(f), big: true);
                continue;
            }

            _rows.Children.Remove(_fileStatus); // 안내 줄은 늘 맨 아래 (UpdateFileStatus가 다시 붙인다)
            _fileHeader ??= AddHeader(_fileHeaderText);
            AddRow(_fileHit(f));
        }

        UpdateFileStatus();
    }

    /// <summary>파일 묶음 이름의 수와 맨 아래 안내 줄 ("220개 중 30개 · 아래로 내리면 더 보여요" / "220개 모두 보였어요").</summary>
    private void UpdateFileStatus()
    {
        if (_pager is not { } pager || pager.Total == 0)
        {
            return;
        }

        string N(long n) => n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        if (_fileHeader is not null)
        {
            _fileHeader.Text = Counted(_fileHeaderText, pager.Total);
        }

        _fileStatus.Text = pager.HasMore ? Foldy.Core.Localization.Loc.F("qs.moreBelow", N(pager.Shown), N(pager.Total))
            : !pager.Done ? Foldy.Core.Localization.Loc.F("qs.stillLooking", N(pager.Total))
            : pager.Capped ? Foldy.Core.Localization.Loc.F("qs.capped", N(pager.Shown), N(pager.Total))
            : Foldy.Core.Localization.Loc.F("qs.allShown", N(pager.Total));
        System.Windows.Automation.AutomationProperties.SetName(_fileStatus, _fileStatus.Text);
        _rows.Children.Remove(_fileStatus);
        _rows.Children.Add(_fileStatus); // 늘 목록 맨 아래
    }

    private void AddRow(Hit h, bool big = false)
    {
        var glyph = new TextBlock { Text = h.Glyph, FontSize = big ? 26 : 20, Width = big ? 40 : 30, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(FontFamilyProperty, "Font.Icon");
        var title = new TextBlock { Text = h.Title, FontSize = big ? 21 : 18, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = big ? FontWeights.SemiBold : FontWeights.Normal };
        var detail = new TextBlock { Text = h.Detail, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        detail.SetResourceReference(ForegroundProperty, "Brush.TextSecondary");
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        if (h.Detail.Length > 0)
        {
            texts.Children.Add(detail);
        }

        var dock = new DockPanel();
        DockPanel.SetDock(glyph, Dock.Left);
        dock.Children.Add(glyph);
        dock.Children.Add(texts);
        var row = new Border { Child = dock, Padding = new Thickness(10, 6, 10, 6), MinHeight = big ? 56 : 44, CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, ToolTip = h.Detail.Length > 0 ? h.Detail : h.Title };
        row.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.Text");
        System.Windows.Automation.AutomationProperties.SetName(row, $"{h.Group}: {h.Title}");
        var index = _hits.Count;
        row.MouseEnter += (_, _) => Select(index);
        row.MouseLeftButtonUp += (_, _) => Run(h.Run);
        _hits.Add((row, h));
        _rows.Children.Add(row);
    }

    private void Select(int i)
    {
        for (var k = 0; k < _hits.Count; k++)
        {
            if (k == i)
            {
                _hits[k].Row.SetResourceReference(Border.BackgroundProperty, "Brush.Selection");
            }
            else
            {
                _hits[k].Row.Background = Brushes.Transparent;
            }
        }

        _selected = i;
        if (i >= 0 && i < _hits.Count)
        {
            _hits[i].Row.BringIntoView();
            ShowPreview(_hits[i].Hit);
        }
        else
        {
            _preview.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowPreview(Hit h)
    {
        FrameworkElement? content = null;
        try
        {
            content = h.Preview?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
        }

        var panel = new StackPanel();
        var glyph = new TextBlock { Text = h.Glyph, FontSize = 44, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 8) };
        glyph.SetResourceReference(FontFamilyProperty, "Font.Icon");
        glyph.SetResourceReference(ForegroundProperty, "Brush.Accent");
        if (content is null)
        {
            panel.Children.Add(glyph);
        }

        var title = new TextBlock { Text = h.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        title.SetResourceReference(ForegroundProperty, "Brush.Text");
        panel.Children.Add(title);
        if (h.Detail.Length > 0)
        {
            var d = new TextBlock { Text = h.Detail, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 8) };
            d.SetResourceReference(ForegroundProperty, "Brush.TextSecondary");
            panel.Children.Add(d);
        }

        if (content is not null)
        {
            panel.Children.Add(content);
        }

        // [열기] (Windows 검색처럼 오른쪽에 큰 단추): 키보드를 몰라도 누를 수 있게
        var open = new Button { Content = h.Group == Foldy.Core.Localization.Loc.T("qs.calc") || h.Group == Foldy.Core.Localization.Loc.T("qs.units") ? Foldy.Core.Localization.Loc.T("ops.copy") : Foldy.Core.Localization.Loc.T("qs.open"), FontSize = 16, MinHeight = 40, MinWidth = 140, Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Focusable = false };
        open.Click += (_, _) => Run(h.Run);
        panel.Children.Add(open);
        if (h.Reveal is { } reveal)
        {
            var folder = new Button { Content = Foldy.Core.Localization.Loc.T("qs.openFolder"), FontSize = 14, MinHeight = 34, Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Focusable = false };
            folder.Click += (_, _) => Run(reveal);
            panel.Children.Add(folder);
        }

        var hint = new TextBlock { Text = h.Reveal is not null ? Foldy.Core.Localization.Loc.T("qs.hintOpen") : h.Group == Foldy.Core.Localization.Loc.T("qs.calc") || h.Group == Foldy.Core.Localization.Loc.T("qs.units") ? Foldy.Core.Localization.Loc.T("qs.hintCopy") : Foldy.Core.Localization.Loc.T("qs.hintRun"), FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        hint.SetResourceReference(ForegroundProperty, "Brush.TextSecondary");
        panel.Children.Add(hint);
        _preview.Child = panel;
        _preview.Visibility = Visibility.Visible;
    }

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico" };
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".log", ".csv", ".json", ".xml", ".ini", ".cfg", ".yaml", ".yml", ".cs", ".py", ".js", ".ts", ".html", ".css", ".ps1", ".bat", ".sql" };

    /// <summary>파일 미리보기: 그림은 작게, 글은 앞 몇 줄, 나머지는 크기·날짜. 큰 파일은 읽지 않는다.</summary>
    public static FrameworkElement? FilePreview(FsEntry f)
    {
        var path = f.Path.Value;
        var ext = Path.GetExtension(path);
        var info = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        info.SetResourceReference(ForegroundProperty, "Brush.TextSecondary");
        info.Text = f.Kind == FsKind.Folder
            ? Foldy.Core.Localization.Loc.F("qs.folderModified", f.Modified.ToLocalTime())
            : Foldy.Core.Localization.Loc.F("qs.fileModified", Foldy.Presentation.Files.FileItem.FormatBytes(f.Size), f.Modified.ToLocalTime());
        var panel = new StackPanel();
        if (f.Kind == FsKind.File && ImageExt.Contains(ext) && f.Size < 50L * 1024 * 1024)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 280;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            panel.Children.Add(new Image { Source = bmp, MaxHeight = 220, Stretch = Stretch.Uniform, Margin = new Thickness(0, 8, 0, 0) });
        }
        else if (f.Kind == FsKind.File && TextExt.Contains(ext) && f.Size < 1024 * 1024)
        {
            var lines = File.ReadLines(path).Take(14).Select(l => l.Length > 60 ? l[..60] + "…" : l);
            var t = new TextBlock { Text = string.Join("\n", lines), FontSize = 12, FontFamily = new FontFamily("Consolas, Malgun Gothic"), TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 8, 0, 0) };
            t.SetResourceReference(ForegroundProperty, "Brush.Text");
            var b = new Border { Child = t, Padding = new Thickness(8), CornerRadius = new CornerRadius(6), ClipToBounds = true, MaxHeight = 240 };
            b.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceAlt");
            panel.Children.Add(b);
        }

        panel.Children.Add(info);
        return panel;
    }
}
