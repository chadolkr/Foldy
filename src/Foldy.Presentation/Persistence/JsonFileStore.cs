using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foldy.Presentation.Persistence;

/// <summary>
/// JSON 파일 원자적 저장 (설계서 7장): 임시 파일에 쓰기 → 디스크에 flush → 교체.
/// 저장 중 전원이 꺼져도 원본이나 새 파일 둘 중 하나는 온전하다.
/// 읽기에 실패하면 원본을 *.broken-날짜.json으로 보관하고 기본값으로 시작한다.
/// </summary>
public static class JsonFileStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path))
            {
                return fallback();
            }

            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(fs, Options) ?? fallback();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try
            {
                var broken = Path.ChangeExtension(path, null) + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}.json";
                File.Copy(path, broken, overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
                // 보관 실패는 무시하고 기본값으로 시작
            }

            return fallback();
        }
    }

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(fs, value, Options);
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }
}
