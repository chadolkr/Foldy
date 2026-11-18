using Foldy.Platform.Shell;
using Foldy.Presentation.Panes;
using Foldy.Presentation.Shell;
using Foldy.Presentation.Tree;
using Foldy.UI.Shell;

namespace Foldy.UI.Views;

/// <summary>패널 화면이 창(조립 루트)에서 받는 것들.</summary>
public interface IPaneHost
{
    ShellViewModel Shell { get; }

    /// <summary>셸 메뉴·IFileOperation 대화상자의 소유 창.</summary>
    IntPtr OwnerHandle { get; }

    ShellContextMenu ShellMenu { get; }

    IconCache Icons { get; }

    FolderTreeViewModel CreateTree();

    /// <summary>Markdown 탭 내용을 그릴 뷰 (Markdown 모듈이 합쳐지기 전에는 간단한 텍스트 보기).</summary>
    System.Windows.FrameworkElement CreateDocumentView(Presentation.Tabs.MarkdownTabViewModel tab, PaneViewModel pane);
}
