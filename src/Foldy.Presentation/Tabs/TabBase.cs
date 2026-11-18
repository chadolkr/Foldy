using CommunityToolkit.Mvvm.ComponentModel;

namespace Foldy.Presentation.Tabs;

public enum TabKind { Folder, Markdown, Search, Git, Analysis }

/// <summary>패널의 탭 하나. 폴더 탭과 문서(Markdown) 탭이 같은 탭 줄을 쓴다.</summary>
public abstract partial class TabBase : ObservableObject
{
    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// OneNote 섹션 탭처럼 탭마다 다른 색 (사용자 요청 2026-11-18). 0~7 팔레트 번호이며
    /// 실제 색은 테마 토큰 Brush.Tab0~7이 정한다 (라이트/다크에서 각각 맞는 톤).
    /// </summary>
    [ObservableProperty]
    private int _colorIndex;

    public Guid Id { get; } = Guid.NewGuid();

    public abstract TabKind Kind { get; }

    /// <summary>탭이 가리키는 경로(폴더 또는 파일).</summary>
    public abstract string? Location { get; }

    /// <summary>탭이 화면에 보이게 됐을 때 (활성화).</summary>
    public virtual void OnActivated()
    {
    }

    /// <summary>탭이 숨겨졌을 때. 감시·폴링을 멈춘다.</summary>
    public virtual void OnDeactivated()
    {
    }

    public virtual void Close()
    {
    }
}
