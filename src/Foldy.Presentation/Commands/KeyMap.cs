using System.Text.Json;

namespace Foldy.Presentation.Commands;

/// <summary>제스처의 수정 키. 표기 순서는 Ctrl, Alt, Shift, Win.</summary>
[Flags]
public enum GestureModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// 키 조합 하나 ("Ctrl+Shift+P", "F5", "Ctrl+`"). WPF를 참조하지 않는 순수 값이다.
/// Key는 정규화된 이름(A-Z, 0-9, F1-F24, Enter, Delete, Up, `, ...)이다.
/// </summary>
public readonly record struct KeyGesture(GestureModifiers Modifiers, string Key)
{
    private static readonly string[] NamedKeys =
    [
        "Enter", "Escape", "Tab", "Space", "Backspace", "Delete", "Insert", "Home", "End",
        "PageUp", "PageDown", "Up", "Down", "Left", "Right",
    ];

    private const string Punctuation = "`-=[];',./\\";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = "Escape",
        ["Return"] = "Enter",
        ["Del"] = "Delete",
        ["Ins"] = "Insert",
        ["PgUp"] = "PageUp",
        ["PgDn"] = "PageDown",
        ["PageDn"] = "PageDown",
        ["Back"] = "Backspace",
        ["Backtick"] = "`",
        ["Oem3"] = "`",
        ["Minus"] = "-",
        ["Comma"] = ",",
        ["Period"] = ".",
        ["Control"] = "Ctrl",
        ["Windows"] = "Win",
        ["Meta"] = "Win",
        ["Cmd"] = "Win",
    };

    private static readonly Dictionary<string, GestureModifiers> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = GestureModifiers.Ctrl,
        ["Alt"] = GestureModifiers.Alt,
        ["Shift"] = GestureModifiers.Shift,
        ["Win"] = GestureModifiers.Win,
    };

    /// <summary>"Ctrl+Shift+P" 형식 문자열을 해석한다. 잘못된 키 이름, 수정 키만 있는 조합, 빈 문자열은 실패.</summary>
    public static bool TryParse(string? text, out KeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = SplitTokens(text.Trim());
        if (parts is null || parts.Count == 0)
        {
            return false;
        }

        var mods = GestureModifiers.None;
        for (var i = 0; i < parts.Count - 1; i++)
        {
            var name = Aliases.TryGetValue(parts[i], out var a) ? a : parts[i];
            if (!ModifierNames.TryGetValue(name, out var m) || (mods & m) != 0)
            {
                return false; // 수정 키가 아니거나 중복
            }

            mods |= m;
        }

        var key = NormalizeKey(parts[^1]);
        if (key is null)
        {
            return false;
        }

        gesture = new KeyGesture(mods, key);
        return true;
    }

    /// <summary>"Ctrl++"처럼 마지막 키가 '+'인 경우를 포함해 '+'로 나눈다.</summary>
    private static List<string>? SplitTokens(string text)
    {
        var tokens = new List<string>();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '+' && sb.Length > 0)
            {
                tokens.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else if (c == '+' && sb.Length == 0 && i == text.Length - 1)
            {
                return null; // "Ctrl+" 같은 끝맺음. 단독 '+'는 키로 받지 않는다 (= 키는 "=" 로 표기)
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString().Trim());
        }

        return tokens.Any(t => t.Length == 0) ? null : tokens;
    }

    private static string? NormalizeKey(string token)
    {
        if (Aliases.TryGetValue(token, out var alias))
        {
            token = alias;
        }

        if (token.Length == 1)
        {
            var ch = token[0];
            if (char.IsAsciiLetter(ch))
            {
                return char.ToUpperInvariant(ch).ToString();
            }

            return char.IsAsciiDigit(ch) || Punctuation.Contains(ch) ? token : null;
        }

        if ((token[0] is 'F' or 'f') && int.TryParse(token.AsSpan(1), out var n) && n is >= 1 and <= 24)
        {
            return "F" + n;
        }

        foreach (var named in NamedKeys)
        {
            if (string.Equals(named, token, StringComparison.OrdinalIgnoreCase))
            {
                return named;
            }
        }

        return null;
    }

    /// <summary>정규화된 표기 ("Ctrl+Shift+P"). 같은 조합은 항상 같은 문자열이 된다.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & GestureModifiers.Ctrl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & GestureModifiers.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & GestureModifiers.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & GestureModifiers.Win) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }

    /// <summary>임의 표기를 정규화한다. 해석할 수 없으면 null.</summary>
    public static string? Normalize(string? text) => TryParse(text, out var g) ? g.ToString() : null;
}

/// <summary>명령 한 개의 정의 (설계서 6.3). Id는 "file.copy"처럼 점으로 구분한 소문자 시작 이름.</summary>
public sealed record CommandDefinition(string Id, string Title, string? DefaultGesture);

/// <summary>두 개 이상의 명령이 같은 제스처를 쓰는 충돌.</summary>
public sealed record KeyConflict(KeyGesture Gesture, IReadOnlyList<string> CommandIds);

/// <summary>
/// 명령 ID → 제스처 표. 기본값은 코드에 있고, keymap.json(%LOCALAPPDATA%\Foldy\keymap.json)에는
/// 기본값과 다른 것(재지정)만 저장한다. 빈 문자열은 "단축키 없음"이다. 순수 로직, 화면 의존 없음.
/// </summary>
public sealed class KeyMap
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, CommandDefinition> _defs;
    private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);

    public KeyMap(IEnumerable<CommandDefinition>? commands = null, IReadOnlyDictionary<string, string>? overrides = null)
    {
        _defs = (commands ?? DefaultCommands).ToDictionary(c => c.Id, StringComparer.Ordinal);
        if (overrides is not null)
        {
            foreach (var (id, gesture) in overrides)
            {
                if (_defs.ContainsKey(id))
                {
                    SetOverride(id, gesture);
                }
            }
        }
    }

    /// <summary>Foldy가 기본으로 제공하는 명령과 단축키. 호스트의 명령 팔레트 ID와 같다.</summary>
    public static IReadOnlyList<CommandDefinition> DefaultCommands { get; } =
    [
        new("file.copy", Foldy.Core.Localization.Loc.T("cmd.copyOther"), "F5"),
        new("file.move", Foldy.Core.Localization.Loc.T("cmd.moveOther"), "F6"),
        new("file.newFolder", Foldy.Core.Localization.Loc.T("cmd.newFolder"), "F7"),
        new("file.rename", Foldy.Core.Localization.Loc.T("cmd.rename"), "F2"),
        new("file.delete", Foldy.Core.Localization.Loc.T("cmd.recycle"), "Delete"),
        new("file.deletePermanent", Foldy.Core.Localization.Loc.T("cmd.deletePermanent"), "Shift+Delete"),
        new("folder.fromTemplate", Foldy.Core.Localization.Loc.T("cmd.fromTemplate"), "Ctrl+Shift+N"),
        new("folder.wizard", Foldy.Core.Localization.Loc.T("cmd.wizard"), null),
        new("folder.organize", Foldy.Core.Localization.Loc.T("cmd.organize"), null),
        new("folder.description", Foldy.Core.Localization.Loc.T("cmd.editDescription"), null),
        new("pane.switch", Foldy.Core.Localization.Loc.T("cmd.switchPane"), "Tab"),
        new("pane.single", Foldy.Core.Localization.Loc.T("cmd.singlePane"), null),
        new("tab.new", Foldy.Core.Localization.Loc.T("cmd.newTab"), "Ctrl+T"),
        new("tab.close", Foldy.Core.Localization.Loc.T("cmd.closeTab"), "Ctrl+W"),
        new("tab.next", Foldy.Core.Localization.Loc.T("cmd.nextTab"), "Ctrl+Tab"),
        new("tab.prev", Foldy.Core.Localization.Loc.T("cmd.prevTab"), "Ctrl+Shift+Tab"),
        new("search.focus", Foldy.Core.Localization.Loc.T("cmd.search"), "Ctrl+F"),
        new("nav.path", Foldy.Core.Localization.Loc.T("cmd.path"), "Ctrl+L"),
        new("nav.refresh", Foldy.Core.Localization.Loc.T("cmd.refresh"), "Ctrl+R"),
        new("nav.back", Foldy.Core.Localization.Loc.T("cmd.back"), "Alt+Left"),
        new("nav.forward", Foldy.Core.Localization.Loc.T("cmd.forward"), "Alt+Right"),
        new("nav.up", Foldy.Core.Localization.Loc.T("cmd.up"), "Alt+Up"),
        new("terminal.open", Foldy.Core.Localization.Loc.T("cmd.terminal"), "Ctrl+`"),
        new("terminal.admin", Foldy.Core.Localization.Loc.T("cmd.terminalAdmin"), "Ctrl+Shift+`"),
        new("terminal.cmd", Foldy.Core.Localization.Loc.T("cmd.cmd"), null),
        new("terminal.cmdAdmin", Foldy.Core.Localization.Loc.T("cmd.cmdAdmin"), null),
        new("view.hidden", Foldy.Core.Localization.Loc.T("cmd.hidden"), "Ctrl+H"),
        new("analysis.open", Foldy.Core.Localization.Loc.T("cmd.analysis"), null),
        new("index.now", Foldy.Core.Localization.Loc.T("sw.c.indexNow"), null),
        new("ai.connect", Foldy.Core.Localization.Loc.T("cmd.ai"), null),
        new("view.preview", Foldy.Core.Localization.Loc.T("cmd.preview"), "F3"),
        new("view.tree", Foldy.Core.Localization.Loc.T("cmd.tree"), "Ctrl+B"),
        new("view.sidebar", Foldy.Core.Localization.Loc.T("cmd.sidebar"), "Ctrl+Shift+B"),
        new("sidebar.pin", Foldy.Core.Localization.Loc.T("cmd.pin"), null),
        new("view.images", Foldy.Core.Localization.Loc.T("cmd.images"), "Ctrl+Shift+I"),
        new("git.open", Foldy.Core.Localization.Loc.T("cmd.git"), null),
        new("git.blame", Foldy.Core.Localization.Loc.T("cmd.blame"), null),
        new("sftp.connect", Foldy.Core.Localization.Loc.T("cmd.sftp"), "Ctrl+Shift+O"),
        new("app.palette", Foldy.Core.Localization.Loc.T("cmd.palette"), "Ctrl+Shift+P"),
        new("app.quickSearch", Foldy.Core.Localization.Loc.T("cmd.quickSearch"), "Ctrl+Space"),
        new("app.settings", Foldy.Core.Localization.Loc.T("cmd.settings"), "Ctrl+,"),
        new("app.help", Foldy.Core.Localization.Loc.T("cmd.help"), "F1"),
        new("app.checkUpdate", Foldy.Core.Localization.Loc.T("cmd.update"), null),
    ];

    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy", "keymap.json");

    public IReadOnlyCollection<CommandDefinition> Commands => _defs.Values;

    /// <summary>기본값과 다른 재지정만. 값이 ""이면 단축키 없음.</summary>
    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    public bool IsOverridden(string commandId) => _overrides.ContainsKey(commandId);

    /// <summary>지금 유효한 제스처 문자열 (정규화됨). 없으면 null.</summary>
    public string? GetGesture(string commandId)
    {
        if (_overrides.TryGetValue(commandId, out var o))
        {
            return o.Length == 0 ? null : o;
        }

        return _defs.TryGetValue(commandId, out var d) ? KeyGesture.Normalize(d.DefaultGesture) : null;
    }

    public string? GetDefaultGesture(string commandId) =>
        _defs.TryGetValue(commandId, out var d) ? KeyGesture.Normalize(d.DefaultGesture) : null;

    /// <summary>
    /// 제스처를 재지정한다. null/빈 문자열은 "단축키 없음". 기본값과 같으면 재지정을 지운다.
    /// 해석할 수 없는 제스처, 모르는 명령이면 false (아무것도 바꾸지 않음). 충돌은 막지 않는다 (ConflictsFor로 경고).
    /// </summary>
    public bool SetGesture(string commandId, string? gesture)
    {
        if (!_defs.ContainsKey(commandId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(gesture) && !KeyGesture.TryParse(gesture, out _))
        {
            return false;
        }

        SetOverride(commandId, gesture);
        return true;
    }

    private void SetOverride(string id, string? gesture)
    {
        var norm = string.IsNullOrWhiteSpace(gesture) ? "" : KeyGesture.Normalize(gesture);
        if (norm is null)
        {
            return; // 파일에서 읽은 잘못된 값은 무시
        }

        if (norm == (GetDefaultGesture(id) ?? ""))
        {
            _overrides.Remove(id);
        }
        else
        {
            _overrides[id] = norm;
        }
    }

    public void Reset(string commandId) => _overrides.Remove(commandId);

    public void ResetAll() => _overrides.Clear();

    /// <summary>해당 제스처를 쓰는 다른 명령 ID들.</summary>
    public IReadOnlyList<string> ConflictsFor(string commandId, string? gesture)
    {
        var norm = KeyGesture.Normalize(gesture);
        if (norm is null)
        {
            return [];
        }

        return _defs.Keys.Where(id => id != commandId && GetGesture(id) == norm).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>전체 충돌 목록 (같은 제스처를 둘 이상이 쓰는 경우).</summary>
    public IReadOnlyList<KeyConflict> FindConflicts()
    {
        var result = new List<KeyConflict>();
        foreach (var g in _defs.Keys.Select(id => (id, g: GetGesture(id))).Where(x => x.g is not null).GroupBy(x => x.g!))
        {
            var ids = g.Select(x => x.id).Order(StringComparer.Ordinal).ToList();
            if (ids.Count > 1 && KeyGesture.TryParse(g.Key, out var gesture))
            {
                result.Add(new KeyConflict(gesture, ids));
            }
        }

        return result;
    }

    /// <summary>눌린 키 조합에 해당하는 명령 ID. 충돌이면 정의 순서상 첫 명령. 없으면 null.</summary>
    public string? Resolve(KeyGesture gesture)
    {
        var text = gesture.ToString();
        return _defs.Keys.FirstOrDefault(id => GetGesture(id) == text);
    }

    public KeyMap Clone() => new(_defs.Values, _overrides);

    /// <summary>keymap.json을 읽는다. 없거나 깨졌으면 기본값. 모르는 명령·잘못된 제스처는 건너뛴다.</summary>
    public static KeyMap Load(string? path = null, IEnumerable<CommandDefinition>? commands = null)
    {
        path ??= DefaultPath;
        Dictionary<string, string>? raw = null;
        try
        {
            if (File.Exists(path))
            {
                using var fs = File.OpenRead(path);
                raw = JsonSerializer.Deserialize<Dictionary<string, string>>(fs);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            raw = null;
        }

        return new KeyMap(commands, raw);
    }

    /// <summary>재지정만 저장한다. 재지정이 하나도 없으면 파일을 지운다.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        if (_overrides.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sorted = new SortedDictionary<string, string>(_overrides, StringComparer.Ordinal);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(sorted, WriteOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
