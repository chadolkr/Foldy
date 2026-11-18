using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foldy.Core.Templates;

/// <summary>템플릿·위저드 JSON 읽기/쓰기 (System.Text.Json, camelCase, 한글은 이스케이프하지 않음).</summary>
public static class TemplateJson
{
    public const string TemplateExtension = ".foldy-template.json";

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>파싱만 한다 (검사는 <see cref="TemplateValidator"/>).</summary>
    public static FolderTemplate Parse(string json)
    {
        var t = JsonSerializer.Deserialize<FolderTemplate>(json, Options) ?? throw new JsonException("빈 템플릿입니다.");
        Normalize(t);
        return t;
    }

    public static string Serialize(FolderTemplate template) => JsonSerializer.Serialize(template, Options);

    public static FolderTemplate Load(string path)
    {
        var t = Parse(File.ReadAllText(path));
        t.SourcePath = path;
        if (string.IsNullOrWhiteSpace(t.Id))
        {
            t.Id = IdFromFileName(path);
        }

        return t;
    }

    /// <summary>원자적 저장 (임시 파일 → 교체).</summary>
    public static void Save(FolderTemplate template, string path)
    {
        ArgumentNullException.ThrowIfNull(template);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(template), new System.Text.UTF8Encoding(false));
        if (File.Exists(path))
        {
            File.Replace(tmp, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }

        template.SourcePath = path;
    }

    public static string IdFromFileName(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(TemplateExtension, StringComparison.OrdinalIgnoreCase)
            ? name[..^TemplateExtension.Length]
            : Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>JSON의 null 목록을 빈 목록으로 바꿔 이후 코드가 null을 신경 쓰지 않게 한다.</summary>
    private static void Normalize(FolderTemplate t)
    {
        t.Purposes ??= [];
        t.Variables ??= [];
        t.Tree ??= [];
        t.Naming ??= [];
        t.FirstSteps ??= [];
        t.Organize ??= [];
        foreach (var v in t.Variables)
        {
            v.Choices ??= [];
        }

        foreach (var r in t.Organize)
        {
            r.Keywords ??= [];
            r.Extensions ??= [];
            r.Groups ??= [];
        }

        foreach (var n in t.Naming)
        {
            n.Examples ??= [];
        }

        void Walk(IList<TemplateNode> nodes)
        {
            foreach (var n in nodes)
            {
                n.Children ??= [];
                Walk(n.Children);
            }
        }

        Walk(t.Tree);
    }
}
