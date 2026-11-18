namespace Foldy.Git;

/// <summary>
/// 그래프 한 행의 그리기 정보.
/// Column: 노드 열. IncomingColumns: 위에서 이 노드로 합류하는 열(Column 포함, 자식 수만큼).
/// ParentColumns: 이 노드에서 아래로 나가는 열(부모 수만큼, 첫 부모가 먼저).
/// PassThroughColumns: 이 행을 그대로 관통하는 열. LaneCount: 이 행에서 필요한 열 수.
/// </summary>
public sealed record GraphRow(
    int Column,
    IReadOnlyList<int> IncomingColumns,
    IReadOnlyList<int> ParentColumns,
    IReadOnlyList<int> PassThroughColumns,
    int LaneCount);

/// <summary>
/// 토폴로지 순서의 커밋을 받아 "열린 부모 해시 → 열 번호" 배열로 레인을 계산한다 (설계서 5.7).
/// 상태(열린 레인)를 유지하므로 페이지 단위로 이어서 호출할 수 있다. 순수 계산이라 UI 의존이 없다.
/// </summary>
public sealed class GraphLaneCalculator
{
    // lanes[i] = i 열이 기다리는(아직 나타나지 않은) 부모 커밋 해시. null 이면 빈 열.
    private readonly List<string?> _lanes = [];

    /// <summary>현재 열린 레인 수 (다음 페이지로 이어질 선의 개수).</summary>
    public int OpenLaneCount => _lanes.Count(l => l is not null);

    /// <summary>커밋 하나를 처리해 행 정보를 만든다. 반드시 토폴로지 순서(자식이 부모보다 먼저)로 호출.</summary>
    public GraphRow Next(string hash, IReadOnlyList<string> parents)
    {
        // 이 커밋을 기다리던 모든 열 (자식이 여럿이면 여러 개)
        var incoming = new List<int>();
        for (var i = 0; i < _lanes.Count; i++)
            if (_lanes[i] == hash) incoming.Add(i);

        int column;
        if (incoming.Count > 0) column = incoming[0];
        else
        {
            // 어떤 자식도 없는 브랜치 끝: 첫 빈 열(없으면 새 열)
            column = FirstFree();
            _lanes[column] = hash;
            // 위에서 들어오는 선은 없으므로 incoming 은 비워 둔다
        }
        var laneCountBefore = _lanes.Count;

        // 합류하는 열은 닫는다 (노드 열은 아래에서 첫 부모로 다시 채운다)
        foreach (var c in incoming) _lanes[c] = null;

        var parentCols = new List<int>(parents.Count);
        if (parents.Count == 0)
        {
            _lanes[column] = null;
        }
        else
        {
            _lanes[column] = parents[0];
            parentCols.Add(column);
            for (var p = 1; p < parents.Count; p++)
            {
                var existing = _lanes.IndexOf(parents[p]);
                if (existing >= 0) parentCols.Add(existing);
                else
                {
                    var free = FirstFree();
                    _lanes[free] = parents[p];
                    parentCols.Add(free);
                }
            }
        }

        // 관통 열: 노드·합류·분기와 무관하게 이 행 전후로 이어지는 열
        var involved = new HashSet<int>(incoming) { column };
        foreach (var pc in parentCols) involved.Add(pc);
        var pass = new List<int>();
        for (var i = 0; i < _lanes.Count; i++)
            if (!involved.Contains(i) && _lanes[i] is not null && i < laneCountBefore) pass.Add(i);

        var laneCount = Math.Max(laneCountBefore, _lanes.Count);
        TrimTrailingFree();
        return new GraphRow(column, incoming, parentCols, pass, laneCount);
    }

    /// <summary>전체 목록을 한 번에 계산하는 편의 함수.</summary>
    public static IReadOnlyList<GraphRow> Compute(IEnumerable<CommitNode> commits)
    {
        var calc = new GraphLaneCalculator();
        return commits.Select(c => calc.Next(c.Hash, c.Parents)).ToList();
    }

    private int FirstFree()
    {
        var i = _lanes.IndexOf(null);
        if (i >= 0) return i;
        _lanes.Add(null);
        return _lanes.Count - 1;
    }

    private void TrimTrailingFree()
    {
        while (_lanes.Count > 0 && _lanes[^1] is null) _lanes.RemoveAt(_lanes.Count - 1);
    }
}
