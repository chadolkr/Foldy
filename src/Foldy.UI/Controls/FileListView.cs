using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Foldy.UI.Controls;

/// <summary>
/// 대용량 파일 목록용 ListView.
/// <para>
/// M0 측정 결과: UI 자동화 클라이언트(펜·터치 입력, 접근성 도구 등)가 붙어 있는 PC에서는 스크롤할 때마다
/// WPF가 행·셀 단위 AutomationPeer 트리를 다시 만들어(AutomationPeer.UpdateSubtree) 레이아웃보다 몇 배 많은
/// 시간을 쓴다. 그래서 목록 자체만 List 컨트롤로 노출하고 행 단위 피어는 만들지 않는다.
/// 화면 읽기 지원은 M1에서 포커스·선택 항목만 알려 주는 가벼운 피어로 보완한다.
/// </para>
/// </summary>
public class FileListView : ListView
{
    public static bool LightweightAutomation { get; set; } = true;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        LightweightAutomation ? new LightPeer(this) : base.OnCreateAutomationPeer();

    private sealed class LightPeer(FileListView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        protected override string GetClassNameCore() => nameof(FileListView);

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
