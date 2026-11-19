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

    /// <summary>설정의 "Git 바 보이기".</summary>
    bool ShowGitBar { get; }

    /// <summary>Git 바 버튼 (받기/올리기/커밋…/Git 화면 열기).</summary>
    void GitBarAction(PaneViewModel pane, string repoRoot, GitBarCommand command);

    /// <summary>폴더 탭이 Git 저장소 안으로 들어왔다 (설정의 "Git 화면 자동 열기"가 켜져 있으면 호스트가 탭을 연다).</summary>
    void GitDetected(PaneViewModel pane, string repoRoot);

    /// <summary>파일에 암호 걸기 (대화 창).</summary>
    void LockFile(string file);

    /// <summary>폴더를 금고에 넣기 (대화 창).</summary>
    void PutInVault(string folder);

    /// <summary>"이 폴더 관리 방식 정하기" (대화 창).</summary>
    void ManageFolder(string folder);

    /// <summary>관리 폴더의 정리 제안 (대화 창).</summary>
    void ShowManagedSuggestions(string root);
}

/// <summary>Git 바의 버튼.</summary>
public enum GitBarCommand { Pull, Push, Commit, Open }
