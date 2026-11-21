using Foldy.Core.Templates;

namespace Foldy.Templates.Tests;

/// <summary>엔진 시험용 템플릿 (내장에서 뺀 개발 템플릿을 시험 자료로 둔다).</summary>
internal static class TemplateFixtures
{
    public static FolderTemplate Load(string id) =>
        TemplateJson.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", id + ".foldy-template.json"));
}
