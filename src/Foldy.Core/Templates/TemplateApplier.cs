using System.Text;
using Foldy.Core.Descriptions;

namespace Foldy.Core.Templates;

/// <summary>템플릿 생성에 필요한 파일 작업 (테스트에서는 임시 폴더, 앱에서는 로컬 구현).</summary>
public interface ITemplateFileSystem
{
    bool DirectoryExists(string path);

    bool FileExists(string path);

    void CreateDirectory(string path);

    /// <summary>없을 때만 만든다 (CreateNew). 이미 있으면 false.</summary>
    bool TryCreateFile(string path, string content);

    string? ReadAllText(string path);

    void DeleteFile(string path);

    /// <summary>비어 있을 때만 지운다. 지웠으면 true.</summary>
    bool DeleteDirectoryIfEmpty(string path);

    void SetHidden(string path);
}

/// <summary>로컬 디스크 구현.</summary>
public sealed class LocalTemplateFileSystem : ITemplateFileSystem
{
    public static readonly LocalTemplateFileSystem Instance = new();

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool TryCreateFile(string path, string content)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var bytes = new UTF8Encoding(false).GetBytes(content);
            fs.Write(bytes);
            return true;
        }
        catch (IOException) when (File.Exists(path) || Directory.Exists(path))
        {
            return false;
        }
    }

    public string? ReadAllText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public void DeleteFile(string path) => File.Delete(path);

    public bool DeleteDirectoryIfEmpty(string path)
    {
        if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any())
        {
            return false;
        }

        Directory.Delete(path);
        return true;
    }

    public void SetHidden(string path)
    {
        var a = File.GetAttributes(path);
        File.SetAttributes(path, a | FileAttributes.Hidden);
    }
}

public sealed record CreatedEntry(string Path, bool IsFile, string? Content);

/// <summary>생성 결과. <see cref="Created"/>는 만든 순서이며 되돌리기(Ctrl+Z)에 쓴다.</summary>
public sealed class TemplateApplyResult
{
    public List<CreatedEntry> Created { get; } = [];

    public List<string> SkippedExisting { get; } = [];

    public List<(string Path, string Error)> Failed { get; } = [];

    public string TopFolder { get; set; } = "";

    public bool Succeeded => Failed.Count == 0;
}

public sealed class TemplateApplyOptions
{
    /// <summary>최상위 폴더의 .folder_description.md에 사용법 넣기 (기본 켜짐, 이미 있으면 건드리지 않음).</summary>
    public bool WriteDescription { get; init; } = true;
}

/// <summary>계획을 실제로 만든다. 없는 것만 만들고, 있는 것은 그대로 둔다.</summary>
public static class TemplateApplier
{
    public static TemplateApplyResult Apply(TemplatePlan plan, FolderTemplate template, ITemplateFileSystem fs, TemplateApplyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(fs);
        if (!plan.CanApply)
        {
            throw new InvalidOperationException("검사를 통과하지 않은 계획은 만들 수 없습니다.");
        }

        options ??= new TemplateApplyOptions();
        var result = new TemplateApplyResult { TopFolder = plan.TopFolder };
        if (!fs.DirectoryExists(plan.TargetRoot))
        {
            fs.CreateDirectory(plan.TargetRoot);
            result.Created.Add(new(plan.TargetRoot, false, null));
        }

        foreach (var n in plan.Flatten())
        {
            if (n.State == PlannedState.Exists)
            {
                result.SkippedExisting.Add(n.FullPath);
                continue;
            }

            if (n.State != PlannedState.Create)
            {
                continue;
            }

            try
            {
                if (n.IsFile)
                {
                    if (fs.TryCreateFile(n.FullPath, n.Content ?? ""))
                    {
                        result.Created.Add(new(n.FullPath, true, n.Content ?? ""));
                    }
                    else
                    {
                        result.SkippedExisting.Add(n.FullPath);
                    }
                }
                else if (fs.DirectoryExists(n.FullPath))
                {
                    result.SkippedExisting.Add(n.FullPath);
                }
                else
                {
                    fs.CreateDirectory(n.FullPath);
                    result.Created.Add(new(n.FullPath, false, null));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Failed.Add((n.FullPath, ex.Message));
            }
        }

        if (options.WriteDescription && result.Failed.Count == 0)
        {
            WriteDescription(plan.TopFolder, template, fs, result);
        }

        return result;
    }

    private static void WriteDescription(string folder, FolderTemplate template, ITemplateFileSystem fs, TemplateApplyResult result)
    {
        var path = Path.Combine(folder, FolderDescription.FileName);
        if (!fs.DirectoryExists(folder) || fs.FileExists(path))
        {
            return; // 이미 있는 설명은 덮어쓰지 않는다
        }

        var desc = new FolderDescription
        {
            Title = template.Name,
            Template = template.Id,
            Body = string.IsNullOrWhiteSpace(template.Guide) ? (template.Description ?? "") : template.Guide!,
        };
        var text = FolderDescriptionParser.Serialize(desc);
        try
        {
            if (fs.TryCreateFile(path, text))
            {
                fs.SetHidden(path);
                result.Created.Add(new(path, true, text));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Failed.Add((path, ex.Message));
        }
    }

    /// <summary>
    /// 한 번에 되돌리기: 만든 파일은 내용이 그대로일 때만 지우고, 만든 폴더는 비어 있을 때만 지운다.
    /// 지우지 못하고 남긴 경로를 돌려준다 (그사이 사용자가 넣은 파일은 건드리지 않음).
    /// </summary>
    public static IReadOnlyList<string> Undo(TemplateApplyResult result, ITemplateFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(fs);
        var left = new List<string>();
        for (var i = result.Created.Count - 1; i >= 0; i--)
        {
            var c = result.Created[i];
            try
            {
                if (c.IsFile)
                {
                    if (!fs.FileExists(c.Path))
                    {
                        continue;
                    }

                    if (string.Equals(fs.ReadAllText(c.Path), c.Content, StringComparison.Ordinal))
                    {
                        fs.DeleteFile(c.Path);
                    }
                    else
                    {
                        left.Add(c.Path);
                    }
                }
                else if (fs.DirectoryExists(c.Path) && !fs.DeleteDirectoryIfEmpty(c.Path))
                {
                    left.Add(c.Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add(c.Path);
            }
        }

        return left;
    }
}
