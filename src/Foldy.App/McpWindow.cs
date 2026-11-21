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
        Title = "AI 연결 (MCP)";
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
        stack.Children.Add(Text("AI가 Foldy로 내 PC의 파일을 찾아요", 22, bold: true));
        stack.Children.Add(Text("Claude 같은 AI에게 \"작년 견적서 찾아 줘\", \"D 드라이브에서 큰 폴더가 뭐야?\"라고 물으면 Foldy가 바로 찾아 줘요. " +
            "AI는 읽기만 하고, 옮기거나 지우지 않아요. 내 계정으로 볼 수 있는 것만 보여요.", 15, secondary: true, margin: new Thickness(0, 6, 0, 16)));

        // 1) AI 앱 고르기 + 단추 하나
        stack.Children.Add(Section("이 PC에 있는 AI 앱"));
        var installed = apps.Where(a => a.Installed).ToList();
        if (installed.Count == 0)
        {
            stack.Children.Add(Text("깔려 있는 AI 앱을 찾지 못했어요. Claude Desktop을 깔고 다시 열거나, 아래 '직접 붙이기'를 써 주세요.", 15, secondary: true));
        }

        foreach (var app in installed)
        {
            var on = connected(app);
            var box = new CheckBox { IsChecked = on || !apps.Any(connected), Margin = new Thickness(0, 4, 0, 4), FontSize = 16, VerticalContentAlignment = VerticalAlignment.Center };
            box.Content = new TextBlock { Text = app.Name + (on ? "  · 연결됨" : ""), TextWrapping = TextWrapping.Wrap };
            System.Windows.Automation.AutomationProperties.SetName(box, app.Name);
            _checks.Add((app, box));
            stack.Children.Add(box);
        }

        var connect = new Button { Content = "AI에 연결", MinWidth = 160, MinHeight = 42, FontSize = 16, Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = installed.Count > 0 };
        connect.FontWeight = FontWeights.SemiBold;
        connect.Click += (_, _) => Apply();
        stack.Children.Add(connect);
        _status.SetResourceReference(ForegroundProperty, "Brush.Text");
        stack.Children.Add(_status);

        _enabled.Content = "AI 연결 켜기 (끄면 AI가 물어도 Foldy가 답하지 않아요)";
        _enabled.IsChecked = isEnabled();
        _enabled.Margin = new Thickness(0, 14, 0, 0);
        _enabled.Click += (_, _) =>
        {
            _setEnabled(_enabled.IsChecked == true);
            Say(_enabled.IsChecked == true ? "AI 연결을 켰어요." : "AI 연결을 껐어요. 연결한 앱 설정은 그대로라 다시 켜면 바로 돼요.");
        };
        stack.Children.Add(_enabled);

        // 2) 도구 목록 (Swagger처럼)
        stack.Children.Add(Section("AI가 쓸 수 있는 도구 (모두 읽기 전용)"));
        foreach (var t in FoldyMcpTools.Create(new FoldyMcpHost()))
        {
            stack.Children.Add(ToolCard(t));
        }

        // 3) 직접 붙이기
        stack.Children.Add(Section("직접 붙이기 (다른 AI 앱)"));
        stack.Children.Add(Text("AI 앱의 MCP 설정에 아래를 넣으면 돼요. 실행 파일: " + exePath, 14, secondary: true, margin: new Thickness(0, 0, 0, 6)));
        stack.Children.Add(CopyRow("Claude Desktop·Gemini CLI·Cursor (JSON)", McpClientConfig.Snippet(McpConfigFormat.McpServers, exePath)));
        stack.Children.Add(CopyRow("Claude Code (명령)", $"claude mcp add foldy -- \"{exePath}\" --mcp"));
        stack.Children.Add(CopyRow("Codex (config.toml)", McpClientConfig.Snippet(McpConfigFormat.CodexToml, exePath)));
        stack.Children.Add(Text("ChatGPT는 인터넷으로만 MCP를 붙여서, PC 안 Foldy를 밖에 열어야 해요. 안전을 위해 하지 않아요.", 13, secondary: true, margin: new Thickness(0, 8, 0, 0)));

        var close = new Button { Content = "닫기", MinWidth = 96, MinHeight = 38, IsCancel = true, FontSize = 15, Padding = new Thickness(16, 4, 16, 4) };
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
            parts.Add($"{string.Join(", ", added)}에 연결했어요. 그 앱을 껐다 켜면 Foldy 도구가 보여요. \"D 드라이브에서 제안서 찾아 줘\"라고 해 보세요.");
        }

        if (removed.Count > 0)
        {
            parts.Add($"{string.Join(", ", removed)}에서 뺐어요.");
        }

        if (failed.Count > 0)
        {
            parts.Add($"{string.Join(", ", failed)} 설정 파일을 고치지 못했어요(형식이 낯설어요). 아래 '직접 붙이기'를 써 주세요.");
        }

        Say(parts.Count == 0 ? "고를 앱을 체크해 주세요." : string.Join(" ", parts));
        foreach (var (app, box) in _checks)
        {
            if (box.Content is TextBlock tb)
            {
                tb.Text = app.Name + (box.IsChecked == true && !failed.Contains(app.Name) ? "  · 연결됨" : "");
            }
        }
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
        head.Children.Add(Text(t.Title, 15, bold: true));
        var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        badge.Child = Text(t.ReadOnly ? "읽기 전용" : "확인 후 실행", 12, secondary: true);
        head.Children.Add(badge);
        p.Children.Add(head);
        p.Children.Add(Text(t.Description, 14, secondary: true, margin: new Thickness(0, 4, 0, 4)));
        foreach (var a in t.Params)
        {
            var row = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Margin = new Thickness(12, 2, 0, 0) };
            var n = new System.Windows.Documents.Run(a.Name) { FontFamily = new FontFamily("Cascadia Mono, Consolas") };
            row.Inlines.Add(n);
            row.Inlines.Add(new System.Windows.Documents.Run($"  {TypeName(a.Type)}{(a.Required ? " · 꼭" : "")}  —  {a.Description}"));
            row.SetResourceReference(ForegroundProperty, "Brush.Text");
            p.Children.Add(row);
        }

        if (t.Params.Count == 0)
        {
            p.Children.Add(Text("입력값 없음", 13.5, secondary: true, margin: new Thickness(12, 2, 0, 0)));
        }

        var b = new Border { Child = p, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        return b;
    }

    private static string TypeName(string t) => t switch
    {
        "string" => "글자",
        "integer" => "숫자",
        "boolean" => "예/아니오",
        "array" => "목록",
        _ => t,
    };

    private Border CopyRow(string label, string text)
    {
        var p = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var copy = new Button { Content = "복사", MinWidth = 72, MinHeight = 32, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(text);
                Say($"{label} 설정을 복사했어요.");
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Say("클립보드를 쓸 수 없어요. 잠시 뒤 다시 눌러 주세요.");
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
