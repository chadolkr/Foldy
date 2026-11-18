namespace Foldy.Core.Threading;

/// <summary>드라이브 큐의 키. "C:" 또는 "\\server\share" 형태(대문자 정규화).</summary>
public readonly record struct DriveKey
{
    public string Value { get; }

    private DriveKey(string value) => Value = value;

    public static DriveKey FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var p = path;
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            p = @"\\" + p[8..];
        }
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\share\...
            var parts = p[2..].Split('\\', 3, StringSplitOptions.RemoveEmptyEntries);
            var root = parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : $@"\\{parts.FirstOrDefault() ?? ""}";
            return new DriveKey(root.ToUpperInvariant());
        }

        if (p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0]))
        {
            return new DriveKey(char.ToUpperInvariant(p[0]) + ":");
        }

        throw new ArgumentException($"드라이브를 알 수 없는 경로: {path}", nameof(path));
    }

    public override string ToString() => Value;
}
