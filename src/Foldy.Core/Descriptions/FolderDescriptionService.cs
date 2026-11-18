using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Foldy.Core.Descriptions;

public enum DescriptionLocation
{
    /// <summary>설명이 없다.</summary>
    None,

    /// <summary>폴더 안 숨김 파일 .folder_description.md.</summary>
    File,

    /// <summary>쓸 수 없는 폴더: 이 PC의 folder_notes에만 저장됨.</summary>
    LocalStore,
}

public sealed record FolderDescriptionRead(FolderDescription? Description, DescriptionLocation Location);

/// <summary>
/// 쓸 수 없는 폴더용 폴백 저장소 (설계서: foldy.db의 folder_notes, 경로 + 볼륨 일련번호 + NTFS 파일 ID).
/// 지금은 <see cref="JsonFolderNoteStore"/>가 맡고, SQLite가 들어오면 같은 인터페이스로 교체한다.
/// </summary>
public interface IFolderNoteStore
{
    string? GetNote(string folder, FolderIdentity? id);

    void SetNote(string folder, FolderIdentity? id, string? markdown);
}

/// <summary>폴더 이름이 바뀌어도 따라가기 위한 식별자 (Foldy.Platform이 GetFileInformationByHandle로 채운다).</summary>
public readonly record struct FolderIdentity(uint VolumeSerial, ulong FileId);

/// <summary>폴백 저장소 JSON 구현 (원자적 쓰기).</summary>
public sealed class JsonFolderNoteStore(string path) : IFolderNoteStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Lock _gate = new();

    public sealed class Note
    {
        public string Path { get; set; } = "";

        public uint? VolumeSerial { get; set; }

        public ulong? FileId { get; set; }

        public string Markdown { get; set; } = "";
    }

    public string? GetNote(string folder, FolderIdentity? id)
    {
        lock (_gate)
        {
            var notes = Load();
            return Find(notes, folder, id)?.Markdown;
        }
    }

    public void SetNote(string folder, FolderIdentity? id, string? markdown)
    {
        lock (_gate)
        {
            var notes = Load();
            var n = Find(notes, folder, id);
            if (markdown is null)
            {
                if (n is not null)
                {
                    notes.Remove(n);
                }
            }
            else
            {
                if (n is null)
                {
                    n = new Note();
                    notes.Add(n);
                }

                n.Path = Normalize(folder);
                n.VolumeSerial = id?.VolumeSerial;
                n.FileId = id?.FileId;
                n.Markdown = markdown;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            FolderDescriptionService.AtomicWrite(path, JsonSerializer.Serialize(notes, Json));
        }
    }

    private static Note? Find(List<Note> notes, string folder, FolderIdentity? id)
    {
        if (id is { } i)
        {
            var byId = notes.FirstOrDefault(n => n.FileId == i.FileId && n.VolumeSerial == i.VolumeSerial);
            if (byId is not null)
            {
                return byId;
            }
        }

        var p = Normalize(folder);
        return notes.FirstOrDefault(n => string.Equals(n.Path, p, StringComparison.OrdinalIgnoreCase));
    }

    private List<Note> Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Normalize(string p) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(p));
}

/// <summary>
/// 폴더 설명 읽기·쓰기·폴백 선택 (설계서 5.13 FolderDescriptionService).
/// 드라이브 루트·시스템 폴더·쓸 수 없는 폴더는 파일을 만들지 않고 <see cref="IFolderNoteStore"/>에 저장한다.
/// 호출은 드라이브 큐(백그라운드)에서 한다.
/// </summary>
public sealed class FolderDescriptionService(IFolderNoteStore fallback, Func<string, FolderIdentity?>? identify = null)
{
    /// <summary>쓸 수 있어도 항상 DB로 저장하는 시스템 폴더.</summary>
    public static bool IsSystemFolder(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string?[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];
        foreach (var r in roots)
        {
            if (string.IsNullOrEmpty(r))
            {
                continue;
            }

            var root = Path.TrimEndingDirectorySeparator(r);
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsDriveRoot(string folder)
    {
        var full = Path.GetFullPath(folder);
        return string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>폴더 안에 파일을 만들지 말아야 하는지 (루트·시스템 폴더).</summary>
    public static bool MustUseLocalStore(string folder) => IsDriveRoot(folder) || IsSystemFolder(folder);

    public FolderDescriptionRead Read(string folder)
    {
        var file = Path.Combine(folder, FolderDescription.FileName);
        try
        {
            if (!MustUseLocalStore(folder) && File.Exists(file))
            {
                return new(FolderDescriptionParser.Parse(File.ReadAllText(file, Encoding.UTF8)), DescriptionLocation.File);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 읽지 못하면 폴백 저장소를 본다
        }

        var md = fallback.GetNote(folder, identify?.Invoke(folder));
        return md is null ? new(null, DescriptionLocation.None) : new(FolderDescriptionParser.Parse(md), DescriptionLocation.LocalStore);
    }

    /// <summary>저장 (null이면 삭제). 실제로 저장된 위치를 돌려준다.</summary>
    public DescriptionLocation Write(string folder, FolderDescription? description)
    {
        var file = Path.Combine(folder, FolderDescription.FileName);
        if (description is null)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            fallback.SetNote(folder, identify?.Invoke(folder), null);
            return DescriptionLocation.None;
        }

        var text = FolderDescriptionParser.Serialize(description);
        if (!MustUseLocalStore(folder))
        {
            try
            {
                var isNew = !File.Exists(file);
                AtomicWrite(file, text);
                if (isNew)
                {
                    File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Hidden);
                }

                return DescriptionLocation.File;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 읽기 전용 미디어·권한 없는 공유 → 폴백
            }
        }

        fallback.SetNote(folder, identify?.Invoke(folder), text);
        return DescriptionLocation.LocalStore;
    }

    /// <summary>임시 파일 → 교체. 숨김 속성이 있는 대상도 교체할 수 있게 속성을 잠시 푼다.</summary>
    public static void AtomicWrite(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & (FileAttributes.Hidden | FileAttributes.ReadOnly)) != 0)
            {
                File.SetAttributes(path, attrs & ~(FileAttributes.Hidden | FileAttributes.ReadOnly));
            }

            File.Replace(tmp, path, null, ignoreMetadataErrors: true);
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        else
        {
            File.Move(tmp, path);
        }
    }
}
