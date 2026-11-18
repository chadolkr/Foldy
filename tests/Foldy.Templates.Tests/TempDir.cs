namespace Foldy.Templates.Tests;

/// <summary>테스트마다 새 임시 폴더. 끝나면 지운다.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foldy-tpl-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string this[string rel] => System.IO.Path.Combine(Path, rel);

    public string File(string rel, string content = "x", DateTime? lastWriteUtc = null)
    {
        var p = this[rel];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        if (lastWriteUtc is { } t)
        {
            System.IO.File.SetLastWriteTimeUtc(p, t);
            System.IO.File.SetLastAccessTimeUtc(p, t);
        }

        return p;
    }

    public string Dir(string rel)
    {
        var p = this[rel];
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
