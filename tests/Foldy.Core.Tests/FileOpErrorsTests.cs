using Foldy.Core.FileSystem;

namespace Foldy.Core.Tests;

/// <summary>
/// 휴지통으로 보내다 Windows "폴더 액세스가 거부됨" 창이 뜨던 것 (사용자 제보 2026-11-23, 브라우저 캐시).
/// 이제 Windows 오류 창 대신 오류 코드로 쉬운 말을 고른다.
/// </summary>
public sealed class FileOpErrorsTests
{
    [Theory]
    [InlineData(0x80070005u, FileOpErrorKind.AccessDenied)]
    [InlineData(0x80270021u, FileOpErrorKind.AccessDenied)]
    [InlineData(0x80270002u, FileOpErrorKind.AccessDenied)]
    [InlineData(0x80070020u, FileOpErrorKind.InUse)]
    [InlineData(0x80270027u, FileOpErrorKind.InUse)]
    [InlineData(0x800700CEu, FileOpErrorKind.PathTooLong)]
    [InlineData(0x80270034u, FileOpErrorKind.PathTooLong)]
    [InlineData(0x800704C7u, FileOpErrorKind.Cancelled)]
    [InlineData(0x80270000u, FileOpErrorKind.Cancelled)]
    [InlineData(0x80004005u, FileOpErrorKind.Other)]
    [InlineData(0u, FileOpErrorKind.Other)]
    public void Windows_codes_map_to_plain_groups(uint hr, FileOpErrorKind kind) =>
        Assert.Equal(kind, FileOpErrors.Classify(unchecked((int)hr)));

    [Theory]
    [InlineData(@"C:\Users\smcuser\AppData\Local\Google\Chrome\User Data\Default\Cache", "Chrome")]
    [InlineData(@"C:\Users\a\AppData\Local\Google\Chrome\User Data\Profile 1\Code Cache", "Chrome")]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\Edge\User Data\Default\Cache", "Edge")]
    [InlineData(@"C:\SEHR\project\KBSMC\node_modules", null)]
    public void Browser_cache_folder_names_its_browser(string path, string? browser) =>
        Assert.Equal(browser, BrowserCache.BrowserName(path));

    [Fact]
    public void Worker_result_carries_the_error_code()
    {
        var line = FileOpWorkerProtocol.WriteResult(new FileOpWorkerResult(false, false, "액세스가 거부되었습니다.", unchecked((int)0x80070005)));
        FileOpWorkerProtocol.ReadLine(line, out _, out var r);
        Assert.NotNull(r);
        Assert.Equal(FileOpErrorKind.AccessDenied, FileOpErrors.Classify(r.Code));
    }
}
