using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Foldy.Presentation.Services;
using Foldy.Presentation.Tabs;

namespace Foldy.Presentation.Panes;

public enum PaneSide { Left, Right }

/// <summary>패널 하나: 탭 목록과 활성 탭, 드라이브 목록 (설계서 6.1).</summary>
public sealed partial class PaneViewModel : ObservableObject
{
    public const int TabColors = 8;

    private readonly TabEnvironment _env;
    private int _nextColor;

    [ObservableProperty]
    private TabBase? _activeTab;

    [ObservableProperty]
    private bool _isActive;

    public PaneViewModel(PaneSide side, TabEnvironment env)
    {
        Side = side;
        _env = env;
        _nextColor = side == PaneSide.Left ? 0 : 4; // 좌우 패널의 첫 탭 색이 겹치지 않게
    }

    public PaneSide Side { get; }

    public ObservableCollection<TabBase> Tabs { get; } = [];

    public ObservableCollection<DriveItem> Drives { get; } = [];

    public FolderTabViewModel? ActiveFolder => ActiveTab as FolderTabViewModel;

    /// <summary>다른 패널·세션 복원 등에서 이미 만든 탭을 넣는다.</summary>
    public TabBase AddTab(TabBase tab, bool activate = true, int? index = null, bool assignColor = false)
    {
        if (assignColor || tab.ColorIndex < 0 || tab.ColorIndex >= TabColors)
        {
            tab.ColorIndex = NextColor();
        }

        var i = Math.Clamp(index ?? Tabs.Count, 0, Tabs.Count);
        Tabs.Insert(i, tab);
        if (activate || ActiveTab is null)
        {
            ActiveTab = tab;
        }

        return tab;
    }

    public FolderTabViewModel NewFolderTab(string path, bool activate = true)
    {
        var tab = new FolderTabViewModel(_env, path) { ColorIndex = NextColor() };
        AddTab(tab, activate, ActiveTab is null ? null : Tabs.IndexOf(ActiveTab) + 1);
        return tab;
    }

    public void CloseTab(TabBase tab)
    {
        var i = Tabs.IndexOf(tab);
        if (i < 0)
        {
            return;
        }

        if (Tabs.Count == 1)
        {
            // 마지막 탭은 닫지 않고 그대로 둔다 (패널이 비지 않게)
            return;
        }

        tab.Close();
        Tabs.RemoveAt(i);
        if (ActiveTab == tab)
        {
            ActiveTab = Tabs[Math.Min(i, Tabs.Count - 1)];
        }
    }

    /// <summary>탭을 떼어 낸다 (반대 패널로 옮길 때). 마지막 탭이면 떼지 않는다.</summary>
    public bool DetachTab(TabBase tab)
    {
        var i = Tabs.IndexOf(tab);
        if (i < 0 || Tabs.Count == 1)
        {
            return false;
        }

        tab.OnDeactivated();
        Tabs.RemoveAt(i);
        if (ActiveTab == tab)
        {
            ActiveTab = Tabs[Math.Min(i, Tabs.Count - 1)];
        }

        return true;
    }

    public void MoveTab(int from, int to)
    {
        if (from < 0 || from >= Tabs.Count)
        {
            return;
        }

        to = Math.Clamp(to, 0, Tabs.Count - 1);
        if (from != to)
        {
            Tabs.Move(from, to);
        }
    }

    public void ActivateRelative(int delta)
    {
        if (Tabs.Count == 0 || ActiveTab is null)
        {
            return;
        }

        var i = (Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[i];
    }

    private int NextColor()
    {
        var c = _nextColor % TabColors;
        _nextColor++;
        return c;
    }

    partial void OnActiveTabChanged(TabBase? oldValue, TabBase? newValue)
    {
        oldValue?.OnDeactivated();
        newValue?.OnActivated();
        OnPropertyChanged(nameof(ActiveFolder));
    }

    public void SetDrives(IEnumerable<DriveItem> drives)
    {
        Drives.Clear();
        foreach (var d in drives)
        {
            Drives.Add(d);
        }
    }

    public void UpdateDrive(DriveItem d)
    {
        for (var i = 0; i < Drives.Count; i++)
        {
            if (string.Equals(Drives[i].Root, d.Root, StringComparison.OrdinalIgnoreCase))
            {
                Drives[i] = d;
                return;
            }
        }
    }
}
