using System.Collections.ObjectModel;
using Foldy.Presentation.Navigation;

namespace Foldy.Presentation.Sidebar;

/// <summary>quickaccess.json: 고정한 폴더와 최근 폴더 (공통 사이드바).</summary>
public sealed record QuickAccessState
{
    public IReadOnlyList<string> Pinned { get; init; } = [];

    public IReadOnlyList<string> Recent { get; init; } = [];
}

/// <summary>사이드바의 폴더 한 줄.</summary>
public sealed record QuickFolder(string Path)
{
    public string Name => PathUtil.DisplayName(Path);
}

/// <summary>
/// 공통 사이드바의 "자주 쓰는 폴더"(사용자가 고정)와 "최근 폴더"(자동, 최근 것이 위).
/// Windows 경로 비교는 대소문자를 가리지 않는다. 고정한 폴더는 최근 목록에 겹쳐 나오지 않는다.
/// </summary>
public sealed class QuickAccess
{
    public const int MaxRecent = 8;
    public const int MaxPinned = 30;

    public QuickAccess(QuickAccessState? state = null)
    {
        foreach (var p in state?.Pinned ?? [])
        {
            if (Clean(p) is { } c && !Contains(Pinned, c) && Pinned.Count < MaxPinned)
            {
                Pinned.Add(new QuickFolder(c));
            }
        }

        foreach (var p in state?.Recent ?? [])
        {
            if (Clean(p) is { } c && !Contains(Recent, c) && !Contains(Pinned, c) && Recent.Count < MaxRecent)
            {
                Recent.Add(new QuickFolder(c));
            }
        }
    }

    public ObservableCollection<QuickFolder> Pinned { get; } = [];

    public ObservableCollection<QuickFolder> Recent { get; } = [];

    /// <summary>목록이 바뀌었다 (저장할 때).</summary>
    public event Action? Changed;

    public bool IsPinned(string? path) => Clean(path) is { } c && Contains(Pinned, c);

    /// <summary>고정. 이미 있으면 false. 최근 목록에서는 뺀다.</summary>
    public bool Pin(string? path)
    {
        if (Clean(path) is not { } c || Contains(Pinned, c) || Pinned.Count >= MaxPinned)
        {
            return false;
        }

        Pinned.Add(new QuickFolder(c));
        Remove(Recent, c);
        Changed?.Invoke();
        return true;
    }

    public bool Unpin(string? path)
    {
        if (Clean(path) is not { } c || !Remove(Pinned, c))
        {
            return false;
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>고정 목록 안에서 순서를 옮긴다 (delta: -1 위로, +1 아래로).</summary>
    public bool Move(string? path, int delta)
    {
        if (Clean(path) is not { } c)
        {
            return false;
        }

        var i = IndexOf(Pinned, c);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Pinned.Count)
        {
            return false;
        }

        Pinned.Move(i, j);
        Changed?.Invoke();
        return true;
    }

    /// <summary>폴더를 열었다. 고정한 폴더나 이미 맨 위면 아무것도 바꾸지 않는다 (목록이 깜빡이지 않게).</summary>
    public void Visit(string? path)
    {
        if (Clean(path) is not { } c || Contains(Pinned, c))
        {
            return;
        }

        var i = IndexOf(Recent, c);
        if (i == 0)
        {
            return;
        }

        if (i > 0)
        {
            Recent.Move(i, 0);
        }
        else
        {
            Recent.Insert(0, new QuickFolder(c));
            while (Recent.Count > MaxRecent)
            {
                Recent.RemoveAt(Recent.Count - 1);
            }
        }

        Changed?.Invoke();
    }

    public void ClearRecent()
    {
        if (Recent.Count == 0)
        {
            return;
        }

        Recent.Clear();
        Changed?.Invoke();
    }

    public QuickAccessState Snapshot() => new()
    {
        Pinned = [.. Pinned.Select(p => p.Path)],
        Recent = [.. Recent.Select(p => p.Path)],
    };

    private static string? Clean(string? path) => PathUtil.Normalize(path);

    private static bool Contains(ObservableCollection<QuickFolder> list, string path) => IndexOf(list, path) >= 0;

    private static int IndexOf(ObservableCollection<QuickFolder> list, string path)
    {
        // Windows 경로는 대소문자를 가리지 않고, SFTP(유닉스) 경로는 가린다.
        var cmp = PathUtil.IsRemoteUri(path) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Path, path, cmp))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool Remove(ObservableCollection<QuickFolder> list, string path)
    {
        var i = IndexOf(list, path);
        if (i < 0)
        {
            return false;
        }

        list.RemoveAt(i);
        return true;
    }
}
