using System.Windows;

namespace Foldy.UI.Templates;

/// <summary>폴더 위저드 창 (왼쪽 단계 목록, 아래 [이전] [다음] [취소]). <see cref="TemplateDialogs.ShowFolderWizard"/>로 연다.</summary>
public partial class FolderWizardWindow : Window
{
    public FolderWizardWindow()
    {
        InitializeComponent();
    }
}
