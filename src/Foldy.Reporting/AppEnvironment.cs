using System.Reflection;
using System.Runtime.InteropServices;

namespace Foldy.Reporting;

/// <summary>리포트에 넣는 실행 환경. 테스트에서 바꿀 수 있게 값으로 둔다.</summary>
public sealed record AppEnvironment(string AppVersion, string OsVersion, string Runtime)
{
    public static AppEnvironment Current(Assembly appAssembly) => new(
        appAssembly.GetName().Version?.ToString(3) ?? "0.0.0",
        // "Microsoft Windows 10.0.26100" → 빌드 번호까지만 (기기 식별 정보 없음)
        $"Windows {Environment.OSVersion.Version} {RuntimeInformation.OSArchitecture}",
        RuntimeInformation.FrameworkDescription);
}

/// <summary>빌드 때 csproj가 심는 값 (AssemblyMetadata).</summary>
public static class ReportingConfig
{
    private static string? Meta(string key) =>
        typeof(ReportingConfig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>비어 있으면 크래시 전송을 끈다.</summary>
    public static Uri? CrashRelayUrl =>
        Uri.TryCreate(Meta("FoldyCrashRelayUrl"), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null;

    public static string RepositoryUrl => Meta("FoldyRepositoryUrl") is { Length: > 0 } r ? r.TrimEnd('/') : "https://github.com/chadolkr/Foldy";
}
