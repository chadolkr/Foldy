using Foldy.Core.Localization;

namespace Foldy.Core.Tests;

/// <summary>앱에서 여는 소개 페이지 주소가 사이트의 언어별 주소와 맞는지.</summary>
public sealed class ProductSiteTests
{
    [Theory]
    [InlineData("ko", "https://chadolkr.github.io/Foldy/")]
    [InlineData("en", "https://chadolkr.github.io/Foldy/en/")]
    [InlineData("es", "https://chadolkr.github.io/Foldy/es/")]
    [InlineData("ja", "https://chadolkr.github.io/Foldy/ja/")]
    [InlineData("zh-Hans", "https://chadolkr.github.io/Foldy/zh-cn/")]
    [InlineData("fr", "https://chadolkr.github.io/Foldy/en/")]
    [InlineData(null, "https://chadolkr.github.io/Foldy/en/")]
    public void Opens_the_page_in_the_app_language(string? language, string expected) =>
        ProductSite.For(language).Should().Be(expected);

    [Fact]
    public void Every_app_language_has_a_page() =>
        Loc.Languages.Select(l => ProductSite.For(l.Code)).Should().OnlyHaveUniqueItems();
}
