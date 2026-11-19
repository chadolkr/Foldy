using System.Windows.Automation;
using System.Windows.Automation.Peers;
using Foldy.UI.Controls;

namespace Foldy.UI.Tests;

/// <summary>FastFileList는 행마다 자동화 피어를 만들지 않으므로(성능), 포커스 행을 목록 이름 + 라이브 영역 이벤트로 알린다.</summary>
public class FastFileListAccessibilityTests
{
    private static FastFileList Make(int n)
    {
        var list = new FastFileList();
        list.Columns.Add(new FastColumn("이름", 200, o => (string)o));
        list.Columns.Add(new FastColumn("크기", 80, o => "1 KB", alignRight: true));
        list.ItemsSource = Enumerable.Range(0, n).Select(i => "파일" + i).ToList();
        return list;
    }

    [Fact]
    public void Focusing_a_row_updates_the_name_with_position_and_selection()
    {
        Sta.Run(() =>
        {
            var list = Make(5);
            list.FocusIndex(2);
            var name = AutomationProperties.GetName(list);
            name.Should().Contain("파일2").And.Contain("3/5").And.Contain("선택됨");
        });
    }

    [Fact]
    public void List_is_a_polite_live_region_with_a_list_peer()
    {
        Sta.Run(() =>
        {
            var list = Make(3);
            AutomationProperties.GetLiveSetting(list).Should().Be(AutomationLiveSetting.Polite);
            var peer = UIElementAutomationPeer.CreatePeerForElement(list);
            peer.Should().NotBeNull();
            peer!.GetAutomationControlType().Should().Be(AutomationControlType.List);
            list.Focusable.Should().BeTrue("키보드로 목록에 들어갈 수 있어야 한다");
        });
    }

    [Fact]
    public void Moving_focus_without_selecting_still_announces_nothing_stale()
    {
        Sta.Run(() =>
        {
            var list = Make(4);
            list.FocusIndex(1);
            list.FocusIndex(3);
            AutomationProperties.GetName(list).Should().Contain("파일3").And.Contain("4/4");
        });
    }
}