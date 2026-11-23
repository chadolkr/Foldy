using System.Globalization;
using System.Text.Json;
using Foldy.Analysis;
using Foldy.Core.Organize;
using Microsoft.Data.Sqlite;

namespace Foldy.Presentation.Templates;

/// <summary>저장한 계획을 만들 때 고른 것 (이어서 할 때 화면 선택도 그대로 돌려놓는다).</summary>
public sealed record SavedPlanSettings
{
    public OrganizeMode Mode { get; init; }

    public string DateFormat { get; init; } = "yyyy/yyyy-MM";

    public string? TemplateId { get; init; }

    public List<CustomRule> Rules { get; init; } = [];

    public bool DetectDuplicates { get; init; } = true;

    public bool PrefixDate { get; init; }

    public bool IncludeSubfolders { get; init; }

    public bool OrganizeProjectAnyway { get; init; }
}

/// <summary>저장한 계획 요약 ("어제 만든 계획이 있어요"에 쓴다). 시각은 UTC.</summary>
public sealed record SavedPlanInfo(string Root, DateTime CreatedUtc, DateTime UpdatedUtc, int Moves, int Checked);

/// <summary>불러온 계획: 계획, 만들 때 고른 것, 앞으로 바뀐 것만 저장하는 데 쓰는 손잡이.</summary>
public sealed record LoadedPlan(OrganizePlan Plan, SavedPlanSettings Settings, SavedPlanInfo Info, SavedPlanHandle Handle);

/// <summary>
/// 화면 스레드에서 떠 둔 계획 모양 (저장은 뒤에서 하는데, 그 사이 사용자가 체크를 바꿔도 섞이지 않게).
/// </summary>
public sealed class PlanSnapshot
{
    internal PlanSnapshot(string root, MoveRow[] moves, KeeperRow[] keepers, SkippedEntry[] skipped)
    {
        Root = root;
        Moves = moves;
        Keepers = keepers;
        Skipped = skipped;
    }

    public string Root { get; }

    public int Count => Moves.Length;

    internal MoveRow[] Moves { get; }

    internal KeeperRow[] Keepers { get; }

    internal SkippedEntry[] Skipped { get; }

    /// <summary>계획의 지금 모양을 뜬다 (화면 스레드에서, 50만 줄도 수십 ms).</summary>
    /// <param name="without">빼고 뜰 원본 경로 (중간에 멈춘 정리에서 이미 옮긴 것).</param>
    public static PlanSnapshot Of(OrganizePlan plan, ISet<string>? without = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var setIndex = new Dictionary<DuplicateSet, int>(plan.DuplicateSets.Count);
        var keepers = new KeeperRow[plan.DuplicateSets.Count];
        for (var i = 0; i < plan.DuplicateSets.Count; i++)
        {
            var s = plan.DuplicateSets[i];
            setIndex[s] = i;
            keepers[i] = new KeeperRow(s, s.Members.IndexOf(s.Keeper));
        }

        var moves = new List<MoveRow>(plan.Moves.Count);
        foreach (var m in plan.Moves)
        {
            if (without is not null && without.Contains(m.Source))
            {
                continue;
            }

            moves.Add(new MoveRow(
                m.Source, m.Destination, m.TargetFolder, m.IsFolder, m.Reason, m.Included, m.Size, m.SourceLastWriteUtc,
                m.DuplicateSet is { } set && setIndex.TryGetValue(set, out var si) ? si : -1));
        }

        return new PlanSnapshot(plan.Root, moves.ToArray(), keepers, plan.Skipped.ToArray());
    }

    internal readonly record struct MoveRow(
        string Source, string Destination, string TargetFolder, bool IsFolder, string Reason, bool Included, long Size, DateTime? LastWriteUtc, int Set);

    internal readonly record struct KeeperRow(DuplicateSet Set, int Keeper);
}

/// <summary>저장한 계획 하나와, 마지막으로 저장한 모양 (다음에는 바뀐 줄만 쓴다).</summary>
public sealed class SavedPlanHandle
{
    internal SavedPlanHandle(long id) => Id = id;

    public long Id { get; }

    /// <summary>원본 경로 → 마지막으로 저장한 줄 (seq 포함).</summary>
    internal Dictionary<string, (long Seq, PlanSnapshot.MoveRow Row)> Rows { get; } = new(StringComparer.Ordinal);

    /// <summary>묶음 번호(저장한 순서) → 남길 파일 번호.</summary>
    internal int[] Keepers { get; set; } = [];

    internal long NextSeq { get; set; }
}

/// <summary>
/// 큰 폴더의 정리 계획을 SQLite에 저장해 두고 다음에 이어서 한다 (사용자 2026-11-23).
/// 계획을 만드는 데 몇 분 걸리는 폴더에서 창을 닫거나 PC를 꺼도 체크한 것까지 그대로 돌아온다.
/// 폴더마다 하나만 두고, 손대지 않은 지 <see cref="MaxAge"/>가 지나면 저절로 지운다.
/// 파일은 하나도 건드리지 않는다: 이 DB는 언제 지워도 되는 메모다.
/// </summary>
public sealed class OrganizePlanStore
{
    /// <summary>스키마가 바뀌면 올린다 (예전 것은 지우고 새로 만든다: 계획은 다시 만들 수 있다).</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly object _gate = new();
    private bool _ready;

    public OrganizePlanStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
    }

    /// <summary>%LOCALAPPDATA%\Foldy\organize-plans.db.</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy", "organize-plans.db");

    /// <summary>손대지 않은 지 이만큼 지난 계획은 지운다.</summary>
    public static TimeSpan MaxAge { get; } = TimeSpan.FromDays(7);

    public string Path { get; }

    /// <summary>테스트에서 "지금"을 바꾼다.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>같은 폴더인지 견주는 열쇠 (대소문자·끝의 \ 무시).</summary>
    public static string KeyOf(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = root;
        }

        return System.IO.Path.TrimEndingDirectorySeparator(full).ToUpperInvariant();
    }

    /// <summary>이 폴더에 저장한 계획이 있으면 요약을 준다 (오래된 계획은 먼저 지운다).</summary>
    public SavedPlanInfo? Find(string root)
    {
        lock (_gate)
        {
            using var c = Open();
            PurgeOld(c);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT root, created, updated, moves, checked FROM plan WHERE root_key = $k";
            cmd.Parameters.AddWithValue("$k", KeyOf(root));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadInfo(r) : null;
        }
    }

    /// <summary>계획을 통째로 저장한다 (같은 폴더의 예전 계획은 바꿔 끼운다).</summary>
    public SavedPlanHandle Save(PlanSnapshot snapshot, SavedPlanSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            Exec(c, tx, "DELETE FROM plan WHERE root_key = $1", KeyOf(snapshot.Root));
            var now = Ticks(UtcNow());
            long id;
            using (var cmd = Command(c, tx, "INSERT INTO plan (root_key, root, created, updated, settings, moves, checked) VALUES ($1,$2,$3,$3,$4,$5,$6) RETURNING id", 6))
            {
                Set(cmd, KeyOf(snapshot.Root), snapshot.Root, now, JsonSerializer.Serialize(settings, Json), snapshot.Count, CountChecked(snapshot));
                id = (long)cmd.ExecuteScalar()!;
            }

            var handle = new SavedPlanHandle(id);
            using (var set = Command(c, tx, "INSERT INTO dupset (plan, id, target, keeper) VALUES ($1,$2,$3,$4)", 4))
            using (var member = Command(c, tx, "INSERT INTO dupmember (plan, dupset, idx, path, size, mtime, keep_target, keep_reason, new_name) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)", 9))
            {
                for (var i = 0; i < snapshot.Keepers.Length; i++)
                {
                    var k = snapshot.Keepers[i];
                    Set(set, id, i, k.Set.TargetFolder, k.Keeper);
                    set.ExecuteNonQuery();
                    for (var j = 0; j < k.Set.Members.Count; j++)
                    {
                        var m = k.Set.Members[j];
                        Set(member, id, i, j, m.FullPath, m.Size, m.LastWriteUtc == default ? (long?)null : Ticks(m.LastWriteUtc), m.KeepTarget, m.KeepReason, m.NewName);
                        member.ExecuteNonQuery();
                    }
                }
            }

            handle.Keepers = snapshot.Keepers.Select(k => k.Keeper).ToArray();
            using (var skip = Command(c, tx, "INSERT INTO skipped (plan, seq, path, reason) VALUES ($1,$2,$3,$4)", 4))
            {
                for (var i = 0; i < snapshot.Skipped.Length; i++)
                {
                    Set(skip, id, i, snapshot.Skipped[i].FullPath, snapshot.Skipped[i].Reason);
                    skip.ExecuteNonQuery();
                }
            }

            using var reasons = new ReasonTable(c, tx, id);
            using (var insert = InsertMove(c, tx))
            {
                foreach (var row in snapshot.Moves)
                {
                    var seq = handle.NextSeq++;
                    WriteMove(insert, id, seq, row, reasons);
                    handle.Rows[row.Source] = (seq, row);
                }
            }

            tx.Commit();
            return handle;
        }
    }

    /// <summary>
    /// 지난번 저장한 뒤 바뀐 줄만 쓴다 (체크, 남길 파일, 옮길 곳, 새로 생기거나 빠진 줄).
    /// 아무것도 안 바뀌었으면 DB를 건드리지 않는다. 바뀐 줄 수를 준다.
    /// </summary>
    public int Update(SavedPlanHandle handle, PlanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            var seen = new HashSet<string>(snapshot.Moves.Length, StringComparer.Ordinal);
            var added = new List<PlanSnapshot.MoveRow>();
            var changed = new List<(long Seq, PlanSnapshot.MoveRow Row)>();
            foreach (var row in snapshot.Moves)
            {
                seen.Add(row.Source);
                if (!handle.Rows.TryGetValue(row.Source, out var old))
                {
                    added.Add(row);
                }
                else if (old.Row != row)
                {
                    changed.Add((old.Seq, row));
                }
            }

            var removed = handle.Rows.Keys.Where(k => !seen.Contains(k)).ToList();
            var keepers = new List<(int Set, int Keeper)>();
            for (var i = 0; i < snapshot.Keepers.Length && i < handle.Keepers.Length; i++)
            {
                if (snapshot.Keepers[i].Keeper != handle.Keepers[i])
                {
                    keepers.Add((i, snapshot.Keepers[i].Keeper));
                }
            }

            var total = added.Count + changed.Count + removed.Count + keepers.Count;
            if (total == 0)
            {
                return 0;
            }

            using var c = Open();
            using var tx = c.BeginTransaction();
            if (!PlanExists(c, tx, handle.Id))
            {
                return 0; // 그사이 지웠다 (정리를 마쳤거나 새로 만들었다)
            }

            using var reasons = new ReasonTable(c, tx, handle.Id);
            using (var del = Command(c, tx, "DELETE FROM move WHERE plan = $1 AND seq = $2", 2))
            {
                foreach (var source in removed)
                {
                    Set(del, handle.Id, handle.Rows[source].Seq);
                    del.ExecuteNonQuery();
                }
            }

            using (var upd = Command(c, tx, "UPDATE move SET dest = $3, target = $4, reason = $5, included = $6, dupset = $7 WHERE plan = $1 AND seq = $2", 7))
            {
                foreach (var (seq, row) in changed)
                {
                    Set(upd, handle.Id, seq, Rel(snapshot.Root, row.Destination), row.TargetFolder, reasons.Id(row.Reason), row.Included ? 1 : 0, row.Set < 0 ? (int?)null : row.Set);
                    upd.ExecuteNonQuery();
                }
            }

            var newSeqs = new List<long>(added.Count);
            using (var insert = InsertMove(c, tx))
            {
                foreach (var row in added)
                {
                    var seq = handle.NextSeq++;
                    newSeqs.Add(seq);
                    WriteMove(insert, handle.Id, seq, row, reasons);
                }
            }

            using (var keep = Command(c, tx, "UPDATE dupset SET keeper = $3 WHERE plan = $1 AND id = $2", 3))
            {
                foreach (var (set, keeper) in keepers)
                {
                    Set(keep, handle.Id, set, keeper);
                    keep.ExecuteNonQuery();
                }
            }

            Exec(c, tx, "UPDATE plan SET updated = $2, moves = $3, checked = $4 WHERE id = $1", handle.Id, Ticks(UtcNow()), snapshot.Count, CountChecked(snapshot));
            tx.Commit();

            // 저장이 끝난 뒤에만 기억을 고친다 (중간에 실패하면 다음에 다시 쓴다)
            foreach (var source in removed)
            {
                handle.Rows.Remove(source);
            }

            foreach (var (seq, row) in changed)
            {
                handle.Rows[row.Source] = (seq, row);
            }

            for (var i = 0; i < added.Count; i++)
            {
                handle.Rows[added[i].Source] = (newSeqs[i], added[i]);
            }

            foreach (var (set, keeper) in keepers)
            {
                handle.Keepers[set] = keeper;
            }

            return total;
        }
    }

    /// <summary>저장한 계획을 불러온다. 없거나 읽을 수 없으면 null.</summary>
    public LoadedPlan? Load(string root, CancellationToken ct = default)
    {
        lock (_gate)
        {
            using var c = Open();
            PurgeOld(c);
            long id;
            SavedPlanInfo info;
            SavedPlanSettings? settings;
            string savedRoot;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT id, root, created, updated, moves, checked, settings FROM plan WHERE root_key = $k";
                cmd.Parameters.AddWithValue("$k", KeyOf(root));
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                {
                    return null;
                }

                id = r.GetInt64(0);
                savedRoot = r.GetString(1);
                info = new SavedPlanInfo(savedRoot, FromTicks(r.GetInt64(2)), FromTicks(r.GetInt64(3)), r.GetInt32(4), r.GetInt32(5));
                try
                {
                    settings = JsonSerializer.Deserialize<SavedPlanSettings>(r.GetString(6), Json);
                }
                catch (JsonException)
                {
                    settings = null;
                }
            }

            if (settings is null)
            {
                return null;
            }

            var plan = new OrganizePlan { Root = savedRoot };
            var handle = new SavedPlanHandle(id);

            // 같은 파일 묶음
            var members = new Dictionary<int, List<DuplicateMember>>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT dupset, path, size, mtime, keep_target, keep_reason, new_name FROM dupmember WHERE plan = $p ORDER BY dupset, idx";
                cmd.Parameters.AddWithValue("$p", id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var set = r.GetInt32(0);
                    if (!members.TryGetValue(set, out var list))
                    {
                        members[set] = list = [];
                    }

                    list.Add(new DuplicateMember
                    {
                        FullPath = r.GetString(1), Size = r.GetInt64(2), LastWriteUtc = r.IsDBNull(3) ? default : FromTicks(r.GetInt64(3)),
                        KeepTarget = r.GetString(4), KeepReason = r.GetString(5), NewName = r.GetString(6),
                    });
                }
            }

            var sets = new List<DuplicateSet>();
            var keepers = new List<int>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT id, target, keeper FROM dupset WHERE plan = $p ORDER BY id";
                cmd.Parameters.AddWithValue("$p", id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var setId = r.GetInt32(0);
                    if (setId != sets.Count || !members.TryGetValue(setId, out var list) || list.Count == 0)
                    {
                        return null; // 깨졌다: 다시 만드는 편이 안전하다
                    }

                    var keeper = Math.Clamp(r.GetInt32(2), 0, list.Count - 1);
                    var set = new DuplicateSet { Keeper = list[keeper], TargetFolder = r.GetString(1) };
                    set.Members.AddRange(list);
                    sets.Add(set);
                    keepers.Add(keeper);
                }
            }

            plan.DuplicateSets.AddRange(sets);
            handle.Keepers = keepers.ToArray();

            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT path, reason FROM skipped WHERE plan = $p ORDER BY seq";
                cmd.Parameters.AddWithValue("$p", id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    plan.Skipped.Add(new SkippedEntry(r.GetString(0), r.GetString(1)));
                }
            }

            var reasons = new Dictionary<long, string>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT id, text FROM reason WHERE plan = $p";
                cmd.Parameters.AddWithValue("$p", id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    reasons[r.GetInt64(0)] = r.GetString(1);
                }
            }

            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT seq, source, dest, target, folder, reason, included, size, mtime, dupset FROM move WHERE plan = $p ORDER BY seq";
                cmd.Parameters.AddWithValue("$p", id);
                using var r = cmd.ExecuteReader();
                var n = 0;
                while (r.Read())
                {
                    if ((++n & 0x3FFF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var seq = r.GetInt64(0);
                    var setIdx = r.IsDBNull(9) ? -1 : r.GetInt32(9);
                    var move = new PlannedMove
                    {
                        Source = Full(savedRoot, r.GetString(1)),
                        Destination = Full(savedRoot, r.GetString(2)),
                        TargetFolder = r.GetString(3),
                        IsFolder = r.GetInt64(4) != 0,
                        Reason = reasons.TryGetValue(r.GetInt64(5), out var text) ? text : "",
                        Included = r.GetInt64(6) != 0,
                        Size = r.GetInt64(7),
                        SourceLastWriteUtc = r.IsDBNull(8) ? null : FromTicks(r.GetInt64(8)),
                        DuplicateSet = setIdx >= 0 && setIdx < sets.Count ? sets[setIdx] : null,
                    };
                    plan.Moves.Add(move);
                    handle.Rows[move.Source] = (seq, new PlanSnapshot.MoveRow(
                        move.Source, move.Destination, move.TargetFolder, move.IsFolder, move.Reason, move.Included, move.Size, move.SourceLastWriteUtc,
                        move.DuplicateSet is null ? -1 : setIdx));
                    handle.NextSeq = Math.Max(handle.NextSeq, seq + 1);
                }
            }

            return new LoadedPlan(plan, settings, info, handle);
        }
    }

    /// <summary>이 폴더의 저장한 계획을 지운다 (정리를 마쳤거나 새로 만들 때).</summary>
    public void Delete(string root)
    {
        lock (_gate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            Exec(c, tx, "DELETE FROM plan WHERE root_key = $1", KeyOf(root));
            tx.Commit();
        }
    }

    /// <summary>손대지 않은 지 <see cref="MaxAge"/>가 지난 계획을 지운다. 지운 수를 준다.</summary>
    public int PurgeOld()
    {
        lock (_gate)
        {
            using var c = Open();
            return PurgeOld(c);
        }
    }

    private int PurgeOld(SqliteConnection c)
    {
        using var tx = c.BeginTransaction();
        var n = Exec(c, tx, "DELETE FROM plan WHERE updated < $1", Ticks(UtcNow() - MaxAge));
        tx.Commit();
        return n;
    }

    private SqliteConnection Open()
    {
        AnalysisSqlite.EnsureInitialized();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var cs = new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        var c = new SqliteConnection(cs);
        try
        {
            c.Open();
            Exec(c, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY;");
            if (!_ready)
            {
                EnsureSchema(c);
                _ready = true;
            }

            return c;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    private static void EnsureSchema(SqliteConnection c)
    {
        long version;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version";
            version = (long)cmd.ExecuteScalar()!;
        }

        if (version == SchemaVersion)
        {
            return;
        }

        // 예전(또는 모르는) 모양이면 비우고 새로 만든다: 계획은 언제든 다시 만들 수 있다
        Exec(c, null, """
            DROP TABLE IF EXISTS move; DROP TABLE IF EXISTS reason; DROP TABLE IF EXISTS skipped;
            DROP TABLE IF EXISTS dupmember; DROP TABLE IF EXISTS dupset; DROP TABLE IF EXISTS plan;
            CREATE TABLE plan (
              id INTEGER PRIMARY KEY AUTOINCREMENT, -- 지운 계획의 번호를 다시 쓰지 않는다 (예전 손잡이가 새 계획에 쓰지 않게)
              root_key TEXT NOT NULL UNIQUE,
              root TEXT NOT NULL,
              created INTEGER NOT NULL,
              updated INTEGER NOT NULL,
              settings TEXT NOT NULL,
              moves INTEGER NOT NULL,
              checked INTEGER NOT NULL);
            CREATE TABLE reason (
              plan INTEGER NOT NULL REFERENCES plan(id) ON DELETE CASCADE,
              id INTEGER NOT NULL,
              text TEXT NOT NULL,
              PRIMARY KEY (plan, id)) WITHOUT ROWID;
            CREATE TABLE move (
              plan INTEGER NOT NULL REFERENCES plan(id) ON DELETE CASCADE,
              seq INTEGER NOT NULL,
              source TEXT NOT NULL,
              dest TEXT NOT NULL,
              target TEXT NOT NULL,
              folder INTEGER NOT NULL,
              reason INTEGER NOT NULL,
              included INTEGER NOT NULL,
              size INTEGER NOT NULL,
              mtime INTEGER,
              dupset INTEGER,
              PRIMARY KEY (plan, seq)) WITHOUT ROWID;
            CREATE TABLE skipped (
              plan INTEGER NOT NULL REFERENCES plan(id) ON DELETE CASCADE,
              seq INTEGER NOT NULL,
              path TEXT NOT NULL,
              reason TEXT NOT NULL,
              PRIMARY KEY (plan, seq)) WITHOUT ROWID;
            CREATE TABLE dupset (
              plan INTEGER NOT NULL REFERENCES plan(id) ON DELETE CASCADE,
              id INTEGER NOT NULL,
              target TEXT NOT NULL,
              keeper INTEGER NOT NULL,
              PRIMARY KEY (plan, id)) WITHOUT ROWID;
            CREATE TABLE dupmember (
              plan INTEGER NOT NULL REFERENCES plan(id) ON DELETE CASCADE,
              dupset INTEGER NOT NULL,
              idx INTEGER NOT NULL,
              path TEXT NOT NULL,
              size INTEGER NOT NULL,
              mtime INTEGER,
              keep_target TEXT NOT NULL,
              keep_reason TEXT NOT NULL,
              new_name TEXT NOT NULL,
              PRIMARY KEY (plan, dupset, idx)) WITHOUT ROWID;
            """);
        Exec(c, null, "PRAGMA user_version=" + SchemaVersion.ToString(CultureInfo.InvariantCulture));
    }

    private static SqliteCommand InsertMove(SqliteConnection c, SqliteTransaction tx) =>
        Command(c, tx, "INSERT INTO move (plan, seq, source, dest, target, folder, reason, included, size, mtime, dupset) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)", 11);

    private static void WriteMove(SqliteCommand insert, long plan, long seq, PlanSnapshot.MoveRow row, ReasonTable reasons)
    {
        var root = reasons.Root;
        Set(insert, plan, seq, Rel(root, row.Source), Rel(root, row.Destination), row.TargetFolder, row.IsFolder ? 1 : 0, reasons.Id(row.Reason),
            row.Included ? 1 : 0, row.Size, row.LastWriteUtc is { } lw ? Ticks(lw) : (long?)null, row.Set < 0 ? (int?)null : row.Set);
        insert.ExecuteNonQuery();
    }

    private static bool PlanExists(SqliteConnection c, SqliteTransaction tx, long id)
    {
        using var cmd = Command(c, tx, "SELECT 1 FROM plan WHERE id = $1", 1);
        Set(cmd, id);
        return cmd.ExecuteScalar() is not null;
    }

    private static int CountChecked(PlanSnapshot s)
    {
        var n = 0;
        foreach (var m in s.Moves)
        {
            if (m.Included)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>정리할 폴더 안 경로는 상대 경로로 줄여 저장한다 (DB가 절반 크기).</summary>
    private static string Rel(string root, string path)
    {
        var r = System.IO.Path.TrimEndingDirectorySeparator(root);
        return path.Length > r.Length + 1 && path.StartsWith(r, StringComparison.OrdinalIgnoreCase) && path[r.Length] is '\\' or '/'
            ? path[(r.Length + 1)..]
            : "\u0001" + path; // 밖의 경로 (드물다): 표시를 붙여 그대로
    }

    private static string Full(string root, string stored) =>
        stored.Length > 0 && stored[0] == '\u0001' ? stored[1..] : System.IO.Path.Combine(root, stored);

    private static long Ticks(DateTime utc) => utc.ToUniversalTime().Ticks;

    private static DateTime FromTicks(long ticks) => new(ticks, DateTimeKind.Utc);

    private static SavedPlanInfo ReadInfo(SqliteDataReader r) =>
        new(r.GetString(0), FromTicks(r.GetInt64(1)), FromTicks(r.GetInt64(2)), r.GetInt32(3), r.GetInt32(4));

    private static int Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] values)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        for (var i = 0; i < values.Length; i++)
        {
            cmd.Parameters.AddWithValue("$" + (i + 1).ToString(CultureInfo.InvariantCulture), values[i] ?? DBNull.Value);
        }

        return cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, int n)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        for (var i = 1; i <= n; i++)
        {
            cmd.Parameters.Add(new SqliteParameter("$" + i.ToString(CultureInfo.InvariantCulture), null));
        }

        cmd.Prepare();
        return cmd;
    }

    private static void Set(SqliteCommand cmd, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
        }
    }

    /// <summary>같은 이유 글("종류: 문서 (.pdf)")은 한 번만 저장하고 번호로 가리킨다.</summary>
    private sealed class ReasonTable : IDisposable
    {
        private readonly Dictionary<string, long> _ids = new(StringComparer.Ordinal);
        private readonly SqliteCommand _insert;
        private readonly long _plan;
        private long _next;

        public ReasonTable(SqliteConnection c, SqliteTransaction tx, long plan)
        {
            _plan = plan;
            using (var cmd = Command(c, tx, "SELECT id, text FROM reason WHERE plan = $1", 1))
            {
                Set(cmd, plan);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    _ids[r.GetString(1)] = r.GetInt64(0);
                    _next = Math.Max(_next, r.GetInt64(0) + 1);
                }
            }

            using (var cmd = Command(c, tx, "SELECT root FROM plan WHERE id = $1", 1))
            {
                Set(cmd, plan);
                Root = cmd.ExecuteScalar() as string ?? "";
            }

            _insert = Command(c, tx, "INSERT INTO reason (plan, id, text) VALUES ($1,$2,$3)", 3);
        }

        public string Root { get; }

        public long Id(string text)
        {
            if (!_ids.TryGetValue(text, out var id))
            {
                id = _next++;
                Set(_insert, _plan, id, text);
                _insert.ExecuteNonQuery();
                _ids[text] = id;
            }

            return id;
        }

        public void Dispose() => _insert.Dispose();
    }
}
