using System.IO;
using System.Windows;
using System.Windows.Controls;
using Foldy.Copy.Native;

namespace Foldy.App;

/// <summary>병렬 복사 중 같은 이름이 있을 때 묻는 작은 창 (덮어쓰기 / 건너뛰기 / 둘 다 유지, 나머지에도 적용).</summary>
public sealed class ConflictWindow : Window
{
    private readonly CheckBox _all = new() { Content = Foldy.Core.Localization.Loc.T("conf.applyAll"), Margin = new Thickness(0, 12, 0, 0) };

    private ConflictWindow(CopyIssue issue)
    {
        Title = Foldy.Core.Localization.Loc.T("conf.title");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "Foldy.Window");
        _all.SetResourceReference(ForegroundProperty, "Brush.Text");

        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 520 };
        panel.Children.Add(new TextBlock { Text = Foldy.Core.Localization.Loc.F("conf.exists", Path.GetFileName(issue.Dest)), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = new TextBlock { Text = Foldy.Core.Localization.Loc.F("conf.paths", issue.Source, issue.Dest), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        panel.Children.Add(detail);
        panel.Children.Add(_all);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(Make(Foldy.Core.Localization.Loc.T("conf.keepBoth"), ConflictAction.Rename, isDefault: true));
        buttons.Children.Add(Make(Foldy.Core.Localization.Loc.T("conf.overwrite"), ConflictAction.Overwrite));
        buttons.Children.Add(Make(Foldy.Core.Localization.Loc.T("conf.skip"), ConflictAction.Skip, isCancel: true));
        panel.Children.Add(buttons);
        Content = panel;
    }

    private ConflictAction Choice { get; set; } = ConflictAction.Skip;

    private Button Make(string text, ConflictAction action, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button { Content = text, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
        b.Click += (_, _) =>
        {
            Choice = action;
            DialogResult = true;
        };
        return b;
    }

    /// <summary>SFTP 전송의 충돌: 같은 창으로 묻고 결과를 변환한다.</summary>
    public static async Task<Foldy.Providers.Sftp.TransferConflictDecision> AskTransferAsync(Foldy.Providers.Sftp.TransferConflict c, CancellationToken ct)
    {
        var d = await AskAsync(new CopyIssue(0, CopyIssueKind.Conflict, c.Source, c.Dest, "", false), ct);
        var action = d.Action switch
        {
            ConflictAction.Overwrite => Foldy.Providers.Sftp.TransferConflictAction.Overwrite,
            ConflictAction.Rename => Foldy.Providers.Sftp.TransferConflictAction.Rename,
            _ => Foldy.Providers.Sftp.TransferConflictAction.Skip,
        };
        return new Foldy.Providers.Sftp.TransferConflictDecision(action, d.ApplyToAll);
    }

    /// <summary>UI 스레드에서 묻고 결정을 돌려준다 (작업 스레드에서 불러도 된다).</summary>
    public static Task<ConflictDecision> AskAsync(CopyIssue issue, CancellationToken ct)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var w = new ConflictWindow(issue) { Owner = Application.Current.MainWindow };
            w.ShowDialog();
            return new ConflictDecision(w.Choice, w._all.IsChecked == true);
        }, System.Windows.Threading.DispatcherPriority.Normal, ct).Task;
    }
}
