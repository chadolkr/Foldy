using System.Windows;

namespace Foldy.UI.Templates;

/// <summary>폴더 정리 미리보기 창 (지금/정리 후 트리, 이동 목록, [정리하기] [되돌리기]). <see cref="TemplateDialogs.ShowOrganize"/>로 연다.</summary>
public partial class OrganizeWindow : Window
{
    public OrganizeWindow()
    {
        InitializeComponent();
    }
}
