namespace Foldy.Presentation.Navigation;

/// <summary>탭 하나의 뒤로/앞으로 기록. 최대 100개, 같은 경로를 연달아 넣지 않는다.</summary>
public sealed class NavigationHistory
{
    public const int Limit = 100;
    private readonly List<string> _back = [];
    private readonly List<string> _forward = [];

    public IReadOnlyList<string> Back => _back;

    public IReadOnlyList<string> Forward => _forward;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>새 위치로 이동할 때 현재 위치를 뒤로 기록에 넣고 앞으로 기록을 지운다.</summary>
    public void Push(string current)
    {
        if (_back.Count > 0 && string.Equals(_back[^1], current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _back.Add(current);
        if (_back.Count > Limit)
        {
            _back.RemoveAt(0);
        }

        _forward.Clear();
    }

    public string? GoBack(string current)
    {
        if (_back.Count == 0)
        {
            return null;
        }

        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(current);
        return target;
    }

    public string? GoForward(string current)
    {
        if (_forward.Count == 0)
        {
            return null;
        }

        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(current);
        return target;
    }

    public void Restore(IEnumerable<string> back, IEnumerable<string> forward)
    {
        _back.Clear();
        _back.AddRange(back.TakeLast(Limit));
        _forward.Clear();
        _forward.AddRange(forward.TakeLast(Limit));
    }
}
