using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Foldy.Core.Mcp;

namespace Foldy.App;

/// <summary>
/// "AI 연결" 화면 (사용자 2026-11-21): AI 앱이 Foldy로 파일을 찾게 하기.
/// 할머니 원칙: 할 일은 [AI에 연결] 단추 하나. 깔린 AI 앱만 체크 목록으로 보이고, 누르면 그 앱 설정에 Foldy를 넣어 준다(원래 설정은 백업).
/// 아래에는 도구 목록(이름·설명·입력값)을 Swagger처럼 보여 주고, 직접 붙이는 사람을 위한 설정을 복사할 수 있다.
/// </summary>
public sealed class McpWindow : Window
{
    private readonly IReadOnlyList<McpClientApp> _apps;
    private readonly string _exe;
    private readonly Func<bool> _isEnabled;
    private readonly Action<bool> _setEnabled;
    private readonly bool _preview;
    private readonly List<(McpClientApp App, CheckBox Box)> _checks = [];
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 15 };
    private readonly CheckBox _enabled = new() { FontSize = 16, VerticalContentAlignment = VerticalAlignment.Center };

    /// <summary>연결한 뒤 할 일 (앱마다). 연결하면 보인다.</summary>
    private readonly StackPanel _next = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };

    /// <param name="apps">AI 앱 목록 (보통 <see cref="McpClientConfig.Known"/>).</param>
    /// <param name="exePath">AI 앱이 띄울 Foldy.exe.</param>
    /// <param name="isEnabled">설정의 "AI 연결 켜기".</param>
    /// <param name="setEnabled">켜고 끄기를 저장.</param>
    /// <param name="connected">앱마다 이미 연결돼 있나 (문서 캡처는 가짜 값).</param>
    /// <param name="preview">문서 캡처: 설정 파일을 건드리지 않는다.</param>
    public McpWindow(IReadOnlyList<McpClientApp> apps, string exePath, Func<bool> isEnabled, Action<bool> setEnabled, Func<McpClientApp, bool>? connected = null, bool preview = false)
    {
        _apps = apps;
        _exe = exePath;
        _isEnabled = isEnabled;
        _setEnabled = setEnabled;
        _preview = preview;
        connected ??= McpClientConfig.IsConnected;
        Title = Foldy.Core.Localization.Loc.T("cmd.ai");
        SetResourceReference(StyleProperty, "Foldy.Window");
        SetResourceReference(BackgroundProperty, "Brush.Window");
        Width = 760;
        Height = 820;
        MinWidth = 560;
        MinHeight = 480;
        FontSize = 15;
        ShowInTaskbar = false;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        var stack = new StackPanel { Margin = new Thickness(28, 22, 28, 22) };
        stack.Children.Add(Text(Foldy.Core.Localization.Loc.T("mcp.head"), 22, bold: true));
        stack.Children.Add(Text(Foldy.Core.Localization.Loc.T("mcp.intro") +
            Foldy.Core.Localization.Loc.T("mcp.readOnlyNote"), 15, secondary: true, margin: new Thickness(0, 6, 0, 16)));

        // 연결하면 무엇을 할 수 있나: 예시를 누르면 복사 (사용자 2026-11-21: "무엇을 할 수 있는지 안내해 주자")
        stack.Children.Add(Section(Foldy.Core.Localization.Loc.T("mcp.examples")));
        var asks = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (ask, what) in FoldyMcpTools.Examples)
        {
            var b = new Button { Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 6, 12, 6), ToolTip = what, HorizontalContentAlignment = HorizontalAlignment.Left };
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock { Text = "“" + ask + "”", FontSize = 15 });
            inner.Children.Add(Text(what, 12, secondary: true));
            b.Content = inner;
            System.Windows.Automation.AutomationProperties.SetName(b, ask);
            b.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(ask);
                    Say(Foldy.Core.Localization.Loc.F("mcp.copiedAsk", ask));
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                }
            };
            asks.Children.Add(b);
        }

        stack.Children.Add(asks);

        // 1) AI 앱 고르기 + 단추 하나
        stack.Children.Add(Section(Foldy.Core.Localization.Loc.T("mcp.apps")));
        var installed = apps.Where(a => a.Installed).ToList();
        if (installed.Count == 0)
        {
            stack.Children.Add(Text(Foldy.Core.Localization.Loc.T("mcp.noApps"), 15, secondary: true));
        }

        foreach (var app in installed)
        {
            var on = connected(app);
            var box = new CheckBox { IsChecked = on || !apps.Any(connected), Margin = new Thickness(0, 4, 0, 4), FontSize = 16, VerticalContentAlignment = VerticalAlignment.Center };
            box.Content = new TextBlock { Text = app.Name + (on ? Foldy.Core.Localization.Loc.T("mcp.connected") : ""), TextWrapping = TextWrapping.Wrap };
            System.Windows.Automation.AutomationProperties.SetName(box, app.Name);
            _checks.Add((app, box));
            stack.Children.Add(box);
        }

        var connect = new Button { Content = Foldy.Core.Localization.Loc.T("mcp.connect"), MinWidth = 160, MinHeight = 42, FontSize = 16, Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = installed.Count > 0 };
        connect.FontWeight = FontWeights.SemiBold;
        connect.Click += (_, _) => Apply();
        stack.Children.Add(connect);
        _status.SetResourceReference(ForegroundProperty, "Brush.Text");
        stack.Children.Add(_status);
        stack.Children.Add(_next);

        _enabled.Content = Foldy.Core.Localization.Loc.T("mcp.enable");
        _enabled.IsChecked = isEnabled();
        _enabled.Margin = new Thickness(0, 14, 0, 0);
        _enabled.Click += (_, _) =>
        {
            _setEnabled(_enabled.IsChecked == true);
            Say(_enabled.IsChecked == true ? Foldy.Core.Localization.Loc.T("mcp.enabled") : Foldy.Core.Localization.Loc.T("mcp.disabled"));
        };
        stack.Children.Add(_enabled);

        // 2) 도구 목록 (Swagger처럼)
        stack.Children.Add(Section(Foldy.Core.Localization.Loc.T("mcp.tools")));
        foreach (var t in FoldyMcpTools.Create(new FoldyMcpHost()))
        {
            stack.Children.Add(ToolCard(t));
        }

        // 3) 직접 붙이기
        stack.Children.Add(Section(Foldy.Core.Localization.Loc.T("mcp.manual")));
        stack.Children.Add(Text(Foldy.Core.Localization.Loc.T("mcp.manualHint") + exePath, 14, secondary: true, margin: new Thickness(0, 0, 0, 6)));
        stack.Children.Add(CopyRow("Claude Desktop·Gemini CLI·Cursor (JSON)", McpClientConfig.Snippet(McpConfigFormat.McpServers, exePath)));
        stack.Children.Add(CopyRow(Foldy.Core.Localization.Loc.T("mcp.claudeCode"), $"claude mcp add foldy -- \"{exePath}\" --mcp"));
        stack.Children.Add(CopyRow("Codex (config.toml)", McpClientConfig.Snippet(McpConfigFormat.CodexToml, exePath)));
        stack.Children.Add(Text(Foldy.Core.Localization.Loc.T("mcp.chatgpt"), 13, secondary: true, margin: new Thickness(0, 8, 0, 0)));

        var close = new Button { Content = Foldy.Core.Localization.Loc.T("cf.close"), MinWidth = 96, MinHeight = 38, IsCancel = true, FontSize = 15, Padding = new Thickness(16, 4, 16, 4) };
        close.Click += (_, _) => Close();
        var footer = new Border { Child = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { close } }, Padding = new Thickness(20, 12, 20, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        footer.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        System.Windows.Automation.AutomationProperties.SetName(this, Title);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    /// <summary>연결한 뒤 할 일 칸 (문서 캡처가 이곳으로 다가간다).</summary>
    internal FrameworkElement NextPanel => _next;

    private void Say(string s) => _status.Text = s;

    private void Apply()
    {
        var added = new List<string>();
        var removed = new List<string>();
        var failed = new List<string>();
        foreach (var (app, box) in _checks)
        {
            var want = box.IsChecked == true;
            var has = !_preview && McpClientConfig.IsConnected(app);
            if (want == has || _preview)
            {
                if (want)
                {
                    added.Add(app.Name);
                }

                continue;
            }

            try
            {
                var ok = want ? McpClientConfig.Connect(app, _exe) : McpClientConfig.Disconnect(app);
                (ok ? (want ? added : removed) : failed).Add(app.Name);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                failed.Add(app.Name);
            }
        }

        if (added.Count > 0 && !_isEnabled())
        {
            _setEnabled(true);
            _enabled.IsChecked = true;
        }

        var parts = new List<string>();
        if (added.Count > 0)
        {
            parts.Add(Foldy.Core.Localization.Loc.F("mcp.connectedTo", string.Join(", ", added)));
        }

        if (removed.Count > 0)
        {
            parts.Add(Foldy.Core.Localization.Loc.F("mcp.removedFrom", string.Join(", ", removed)));
        }

        if (failed.Count > 0)
        {
            parts.Add(Foldy.Core.Localization.Loc.F("mcp.configFailed", string.Join(", ", failed)));
        }

        Say(parts.Count == 0 ? Foldy.Core.Localization.Loc.T("mcp.pickApps") : string.Join(" ", parts));
        ShowNext(added.Count > 0 ? [.. _checks.Where(c => added.Contains(c.App.Name)).Select(c => c.App)] : []);
        foreach (var (app, box) in _checks)
        {
            if (box.Content is TextBlock tb)
            {
                tb.Text = app.Name + (box.IsChecked == true && !failed.Contains(app.Name) ? Foldy.Core.Localization.Loc.T("mcp.connected") : "");
            }
        }
    }

    /// <summary>
    /// 연결한 뒤 할 일 (사용자 2026-11-22: 연결했는데 Claude 새 대화에 Foldy가 없다고 나왔다).
    /// AI 앱은 설정을 켤 때만 읽으니 다시 켜기, 새 대화 열기, 처음 쓸 때 허용, 해 볼 질문을 앱마다 쉬운 말로 보여 준다.
    /// </summary>
    internal void ShowNext(IReadOnlyList<McpClientApp> apps)
    {
        _next.Children.Clear();
        if (apps.Count == 0)
        {
            _next.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var app in apps)
        {
            var card = new StackPanel();
            card.Children.Add(Text(Foldy.Core.Localization.Loc.F("mcp.next.head", app.Name), 16, bold: true, margin: new Thickness(0, 0, 0, 6)));
            var steps = McpNextSteps.For(app.Name);
            for (var i = 0; i < steps.Count; i++)
            {
                card.Children.Add(Text($"{i + 1}. {steps[i]}", 14.5, margin: new Thickness(0, 2, 0, 2)));
            }

            if (McpNextSteps.CanRestart(app.Name))
            {
                card.Children.Add(RestartRow());
            }

            var b = new Border { Child = card, Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            b.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
            b.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
            System.Windows.Automation.AutomationProperties.SetName(b, Foldy.Core.Localization.Loc.F("mcp.next.head", app.Name));
            _next.Children.Add(b);
        }

        // 해 볼 질문: 누르면 복사
        var ask = McpNextSteps.FirstAsk;
        var askRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        askRow.Children.Add(Text(Foldy.Core.Localization.Loc.T("mcp.next.try"), 14.5, bold: true, margin: new Thickness(0, 6, 10, 0)));
        var askButton = new Button { Content = "“" + ask + "”", Padding = new Thickness(12, 5, 12, 5), FontSize = 15, ToolTip = Foldy.Core.Localization.Loc.T("ops.copy") };
        System.Windows.Automation.AutomationProperties.SetName(askButton, ask);
        askButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(ask);
                Say(Foldy.Core.Localization.Loc.F("mcp.copiedAsk", ask));
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Say(Foldy.Core.Localization.Loc.T("mcp.noClipboard"));
            }
        };
        askRow.Children.Add(askButton);
        _next.Children.Add(askRow);
        _next.Children.Add(Text(Foldy.Core.Localization.Loc.T("mcp.next.stuck"), 13.5, secondary: true, margin: new Thickness(0, 8, 0, 0)));
        _next.Visibility = Visibility.Visible;
        _next.BringIntoView();
    }

    /// <summary>[Claude 다시 시작]: 한 번 더 묻고(Claude에서 하던 일이 멈춘다) 끄고 다시 켠다.</summary>
    private StackPanel RestartRow()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var row = new WrapPanel();
        var restart = new Button { Content = Foldy.Core.Localization.Loc.T("mcp.next.restart"), MinHeight = 36, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        restart.FontWeight = FontWeights.SemiBold;
        var yes = new Button { Content = Foldy.Core.Localization.Loc.T("mcp.next.restartYes"), MinHeight = 36, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
        var no = new Button { Content = Foldy.Core.Localization.Loc.T("dlg.cancel"), MinHeight = 36, Padding = new Thickness(14, 4, 14, 4), Visibility = Visibility.Collapsed };
        var note = Text("", 13.5, secondary: true, margin: new Thickness(0, 6, 0, 0));
        note.Visibility = Visibility.Collapsed;
        void Note(string text)
        {
            note.Text = text;
            note.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        void Asking(bool on)
        {
            restart.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            yes.Visibility = no.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        restart.Click += (_, _) =>
        {
            if (!_preview && Services.ClaudeRestart.RunningExe() is null)
            {
                Note(Foldy.Core.Localization.Loc.T("mcp.next.notRunning"));
                return;
            }

            Note(Foldy.Core.Localization.Loc.T("mcp.next.restartAsk"));
            Asking(true);
        };
        no.Click += (_, _) =>
        {
            Note("");
            Asking(false);
        };
        yes.Click += async (_, _) =>
        {
            yes.IsEnabled = no.IsEnabled = false;
            Note(Foldy.Core.Localization.Loc.T("mcp.next.restarting"));
            var r = _preview ? Services.ClaudeRestart.Result.Restarted : await Services.ClaudeRestart.RestartAsync();
            Note(r switch
            {
                Services.ClaudeRestart.Result.Restarted => Foldy.Core.Localization.Loc.T("mcp.next.restarted"),
                Services.ClaudeRestart.Result.NotRunning => Foldy.Core.Localization.Loc.T("mcp.next.notRunning"),
                _ => Foldy.Core.Localization.Loc.T("mcp.next.restartFailed"),
            });
            yes.IsEnabled = no.IsEnabled = true;
            Asking(false);
        };
        row.Children.Add(restart);
        row.Children.Add(yes);
        row.Children.Add(no);
        panel.Children.Add(row);
        panel.Children.Add(note);
        return panel;
    }

    private static TextBlock Text(string s, double size, bool bold = false, bool secondary = false, Thickness? margin = null)
    {
        var t = new TextBlock { Text = s, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0), LineHeight = size * 1.5 };
        if (bold)
        {
            t.FontWeight = FontWeights.SemiBold;
        }

        t.SetResourceReference(ForegroundProperty, secondary ? "Brush.TextSecondary" : "Brush.Text");
        return t;
    }

    private static TextBlock Section(string s)
    {
        var t = Text(s, 17, bold: true, margin: new Thickness(0, 22, 0, 8));
        return t;
    }

    private static Border ToolCard(McpTool t)
    {
        var p = new StackPanel();
        var head = new WrapPanel();
        var name = new TextBlock { Text = t.Name, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0) };
        name.SetResourceReference(ForegroundProperty, "Brush.Accent");
        head.Children.Add(name);
        head.Children.Add(Text(Shown($"mcp.tool.{t.Name}", t.Title), 15, bold: true));
        var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        badge.Child = Text(t.ReadOnly ? Foldy.Core.Localization.Loc.T("mcp.readOnly") : Foldy.Core.Localization.Loc.T("mcp.confirmFirst"), 12, secondary: true);
        head.Children.Add(badge);
        p.Children.Add(head);
        p.Children.Add(Text(Shown($"mcp.tool.{t.Name}.desc", t.Description), 14, secondary: true, margin: new Thickness(0, 4, 0, 4)));
        foreach (var a in t.Params)
        {
            var row = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Margin = new Thickness(12, 2, 0, 0) };
            var n = new System.Windows.Documents.Run(a.Name) { FontFamily = new FontFamily("Cascadia Mono, Consolas") };
            row.Inlines.Add(n);
            row.Inlines.Add(new System.Windows.Documents.Run($"  {TypeName(a.Type)}{(a.Required ? Foldy.Core.Localization.Loc.T("mcp.required") : "")}  —  {Shown($"mcp.param.{a.Name}", a.Description)}"));
            row.SetResourceReference(ForegroundProperty, "Brush.Text");
            p.Children.Add(row);
        }

        if (t.Params.Count == 0)
        {
            p.Children.Add(Text(Foldy.Core.Localization.Loc.T("mcp.noInput"), 13.5, secondary: true, margin: new Thickness(12, 2, 0, 0)));
        }

        var b = new Border { Child = p, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        return b;
    }

    /// <summary>
    /// 도구 설명을 화면 언어로. AI에게 보내는 설명(McpTool)은 그대로 두고 이 화면에서만 바꾼다.
    /// 번역이 없는 새 도구·입력값이면 원래 설명을 보인다.
    /// </summary>
    private static string Shown(string key, string original)
    {
        var s = Foldy.Core.Localization.Loc.T(key);
        return s == key ? original : s;
    }

    private static string TypeName(string t) => t switch
    {
        "string" => Foldy.Core.Localization.Loc.T("mcp.text"),
        "integer" => Foldy.Core.Localization.Loc.T("mcp.number"),
        "boolean" => Foldy.Core.Localization.Loc.T("mcp.bool"),
        "array" => Foldy.Core.Localization.Loc.T("mcp.list"),
        _ => t,
    };

    private Border CopyRow(string label, string text)
    {
        var p = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var copy = new Button { Content = Foldy.Core.Localization.Loc.T("ops.copy"), MinWidth = 72, MinHeight = 32, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(text);
                Say(Foldy.Core.Localization.Loc.F("mcp.configCopied", label));
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Say(Foldy.Core.Localization.Loc.T("mcp.noClipboard"));
            }
        };
        DockPanel.SetDock(copy, Dock.Right);
        p.Children.Add(copy);
        var inner = new StackPanel();
        inner.Children.Add(Text(label, 14, bold: true));
        var code = new TextBox { Text = text, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(0, 2, 0, 0) };
        code.SetResourceReference(ForegroundProperty, "Brush.Text");
        inner.Children.Add(code);
        p.Children.Add(inner);
        var b = new Border { Child = p, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(8) };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        return b;
    }
}
