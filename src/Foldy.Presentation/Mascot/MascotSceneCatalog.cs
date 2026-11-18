using System.Globalization;
using System.Text.Json;

namespace Foldy.Presentation.Mascot;

/// <summary>scenes.json의 장면 하나. 모던·레트로 렌더러가 같은 정의를 쓴다.</summary>
public sealed record SceneDefinition(string Id, double LoopSeconds, bool Loop, string Description);

/// <summary>렌더 공통 설정 (scenes.json의 "render").</summary>
public sealed record MascotRenderSettings(int ModernFps, int RetroFps, int RetroScale, double DesignHeight, double MaxWorldWidth)
{
    public static MascotRenderSettings Default { get; } = new(30, 10, 3, 48, 420);
}

/// <summary>assets/mascot/scenes.json 파서. 파일이 없거나 깨져도 앱이 죽지 않도록 기본값으로 대체할 수 있다.</summary>
public sealed class MascotSceneCatalog
{
    private readonly Dictionary<string, SceneDefinition> _scenes;

    private MascotSceneCatalog(MascotRenderSettings render, Dictionary<string, SceneDefinition> scenes)
    {
        Render = render;
        _scenes = scenes;
    }

    public MascotRenderSettings Render { get; }

    public IReadOnlyCollection<SceneDefinition> Scenes => _scenes.Values;

    /// <summary>JSON에 없는 장면은 2초 반복으로 대체한다.</summary>
    public SceneDefinition Get(MascotScene scene) =>
        _scenes.TryGetValue(scene.ToString(), out var def) ? def : new SceneDefinition(scene.ToString(), 2.0, true, string.Empty);

    public static MascotSceneCatalog Fallback { get; } = new(MascotRenderSettings.Default, []);

    public static MascotSceneCatalog Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var render = MascotRenderSettings.Default;
        if (root.TryGetProperty("render", out var r))
        {
            render = new MascotRenderSettings(
                Int(r, "modernFps", render.ModernFps),
                Int(r, "retroFps", render.RetroFps),
                Int(r, "retroScale", render.RetroScale),
                Num(r, "designHeight", render.DesignHeight),
                Num(r, "maxWorldWidth", render.MaxWorldWidth));
        }

        var scenes = new Dictionary<string, SceneDefinition>(StringComparer.Ordinal);
        if (root.TryGetProperty("scenes", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in arr.EnumerateArray())
            {
                var id = s.GetProperty("id").GetString() ?? throw new FormatException("scene id missing");
                var loopSeconds = Num(s, "loopSeconds", 2.0);
                if (loopSeconds <= 0)
                {
                    throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"scene '{id}': loopSeconds must be > 0"));
                }

                var loop = !s.TryGetProperty("loop", out var l) || l.ValueKind != JsonValueKind.False;
                var desc = s.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                scenes[id] = new SceneDefinition(id, loopSeconds, loop, desc);
            }
        }

        return new MascotSceneCatalog(render, scenes);
    }

    private static double Num(JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
}
