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

    /// <summary>폴더 목록이 아닌 탭(Markdown 문서, Git 뷰)의 내용 화면.</summary>
    System.Windows.FrameworkElement? CreateTabView(Presentation.Tabs.TabBase tab, PaneViewModel pane);

    /// <summary>Git 뷰 탭을 연다 (브랜치 칩을 눌렀을 때).</summary>
    void OpenGit(PaneViewModel pane, string repoRoot);
}
