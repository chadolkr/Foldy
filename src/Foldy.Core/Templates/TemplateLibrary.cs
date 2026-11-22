using System.Reflection;
using System.Text.Json;

namespace Foldy.Core.Templates;

/// <summary>
/// 내장 템플릿(어셈블리에 포함된 assets/templates) + 내 템플릿(%LOCALAPPDATA%\Foldy\Templates).
/// 내 템플릿은 불러올 때 검사해 문제가 있으면 목록에서 빼고 <see cref="LoadErrors"/>에 남긴다.
/// </summary>
public sealed class TemplateLibrary
{
    public const string ResourcePrefix = "Foldy.Templates.";

    private readonly List<FolderTemplate> _templates = [];

    public TemplateLibrary(string? userFolder = null)
    {
        UserFolder = userFolder ?? DefaultUserFolder;
    }

    public static string DefaultUserFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy", "Templates");

    public string UserFolder { get; }

    public IReadOnlyList<FolderTemplate> Templates => _templates;

    public List<(string Path, string Error)> LoadErrors { get; } = [];

    public FolderTemplate? Find(string id) =>
        _templates.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>내장 + 내 템플릿을 다시 읽는다. 같은 id면 내 템플릿이 뒤에 붙는다 (내장을 덮지 않음).</summary>
    public TemplateLibrary Reload()
    {
        _templates.Clear();
        LoadErrors.Clear();
        _templates.AddRange(LoadBuiltIn());
        if (Directory.Exists(UserFolder))
        {
            foreach (var f in Directory.EnumerateFiles(UserFolder, "*" + TemplateJson.TemplateExtension).Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var t = TemplateJson.Load(f);
                    var issues = TemplateValidator.Validate(t);
                    if (issues.Count > 0)
                    {
                        LoadErrors.Add((f, issues[0].Message));
                        continue;
                    }

                    _templates.Add(t);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    LoadErrors.Add((f, ex.Message));
                }
            }
        }

        return this;
    }

    /// <summary>내 템플릿 저장 (검사 통과해야 함). 저장한 경로.</summary>
    public string SaveUserTemplate(FolderTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var issues = TemplateValidator.Validate(template);
        if (issues.Count > 0)
        {
            throw new InvalidDataException(issues[0].Message);
        }

        if (string.IsNullOrWhiteSpace(template.Id))
        {
            template.Id = TemplateCapture.MakeId(template.Name);
        }

        template.IsBuiltIn = false;
        var path = template.SourcePath ?? Path.Combine(UserFolder, template.Id + TemplateJson.TemplateExtension);
        TemplateJson.Save(template, path);
        return path;
    }

    /// <summary>가져오기: 파일을 검사한 뒤 내 템플릿 폴더로 복사. 문제가 있으면 예외로 거부한다.</summary>
    public FolderTemplate Import(string file)
    {
        var t = TemplateJson.Load(file);
        var issues = TemplateValidator.Validate(t);
        if (issues.Count > 0)
        {
            throw new InvalidDataException(Foldy.Core.Localization.Loc.F("tl.cantImport", issues[0].Message, issues[0].Path));
        }

        t.SourcePath = null;
        var path = Path.Combine(UserFolder, t.Id + TemplateJson.TemplateExtension);
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(UserFolder, $"{t.Id}-{i}{TemplateJson.TemplateExtension}");
        }

        t.SourcePath = path;
        TemplateJson.Save(t, path);
        _templates.Add(t);
        return t;
    }

    public static void Export(FolderTemplate template, string file) => File.WriteAllText(file, TemplateJson.Serialize(template), new System.Text.UTF8Encoding(false));

    public bool DeleteUserTemplate(FolderTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.IsBuiltIn || template.SourcePath is null)
        {
            return false;
        }

        File.Delete(template.SourcePath);
        _templates.Remove(template);
        return true;
    }

    /// <summary>어셈블리에 포함된 내장 템플릿 (사용법 .guide.md를 Guide에 채운다).</summary>
    public static IReadOnlyList<FolderTemplate> LoadBuiltIn()
    {
        var asm = typeof(TemplateLibrary).Assembly;
        var list = new List<FolderTemplate>();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(TemplateJson.TemplateExtension, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            var json = TemplateI18n.Localize(ReadResource(asm, res)!);
            var t = TemplateJson.Parse(json);
            var fileId = res[ResourcePrefix.Length..^TemplateJson.TemplateExtension.Length];
            if (string.IsNullOrWhiteSpace(t.Id))
            {
                t.Id = fileId;
            }

            t.IsBuiltIn = true;
            t.Guide ??= ReadResource(asm, ResourcePrefix + fileId + ".guide.md");
            list.Add(t);
        }

        return list;
    }

    /// <summary>위저드 질문·가중치 (assets/templates/wizard.json).</summary>
    public static WizardDefinition LoadWizard()
    {
        var json = ReadResource(typeof(TemplateLibrary).Assembly, ResourcePrefix + "wizard.json")
            ?? throw new InvalidOperationException(Foldy.Core.Localization.Loc.T("tl.noWizard"));
        return WizardDefinition.Parse(TemplateI18n.Localize(json));
    }

    private static string? ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name);
        if (s is null)
        {
            return null;
        }

        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
