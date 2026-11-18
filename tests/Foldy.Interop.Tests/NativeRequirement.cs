namespace Foldy.Interop.Tests;

/// <summary>
/// Rust 산출물이 없으면 로컬에서는 건너뛰고, CI(FOLDY_REQUIRE_NATIVE=1)에서는 실패시킨다.
/// </summary>
internal static class NativeRequirement
{
    public static bool Required => Environment.GetEnvironmentVariable("FOLDY_REQUIRE_NATIVE") == "1";

    public static void Ensure(bool present, string what)
    {
        if (present)
        {
            return;
        }

        if (Required)
        {
            Assert.Fail($"{what} 이(가) 없습니다. build.ps1로 Rust 엔진을 먼저 빌드하세요.");
        }

        Assert.Skip($"{what} 없음 (Rust 엔진 미빌드)");
    }
}
