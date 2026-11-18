using System.Windows;

namespace Foldy.UI.Templates;

/// <summary>템플릿 선택 창 (왼쪽 목록, 오른쪽 입력 + [미리보기] [사용법]). <see cref="TemplateDialogs.ShowTemplatePicker"/>로 연다.</summary>
public partial class TemplatePickerWindow : Window
{
    public TemplatePickerWindow()
    {
        InitializeComponent();
    }
}
