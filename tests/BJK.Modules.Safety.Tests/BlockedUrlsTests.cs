using BJK.Modules.Safety;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BJK.Modules.Safety.Tests;

public sealed class BlockedUrlsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("https://www.Example.com/Some/Page/?q=1#top", "example.com/some/page")]
    [InlineData("example.com", "example.com")]
    [InlineData("http://sub.example.co.uk:8080/x", "sub.example.co.uk/x")]
    [InlineData("not a url", null)]
    [InlineData("localhost", null)]
    public void Addresses_are_normalised(string value, string? expected)
    {
        Assert.Equal(expected, BlockedUrls.Normalise(value));
    }

    [Fact]
    public void Addresses_are_found_in_text_with_or_without_a_scheme()
    {
        var found = BlockedUrls.Find("Look at https://bad.example/a/b, and also www.other.test/page. Nice driving!").ToList();

        Assert.Equal(["bad.example/a/b", "other.test/page"], found);
    }

    [Theory]
    [InlineData("example.com", "example.com/anything", true)]
    [InlineData("example.com", "cdn.example.com/x", true)]
    [InlineData("example.com", "notexample.com", false)]
    [InlineData("example.com/bad", "example.com/bad/page", true)]
    [InlineData("example.com/bad", "example.com/badger", false)]
    [InlineData("example.com/bad", "example.com/fine", false)]
    public void Entries_cover_their_site_or_path(string entry, string url, bool matches)
    {
        Assert.Equal(matches, BlockedUrls.Matches(entry, url));
    }

    [Fact]
    public async Task Text_with_a_blocked_address_is_held_whatever_the_screener_says()
    {
        var services = new ServiceCollection()
            .AddSingleton<IBlockedUrlList>(new FakeList("bad.example"))
            .AddContentScreening(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        var screener = services.GetRequiredService<IContentScreener>();

        var held = await screener.ScreenAsync("lovely driving, see bad.example/pics", Ct);
        var fine = await screener.ScreenAsync("lovely driving, see good.example", Ct);

        Assert.Equal(ScreeningVerdict.Hold, held.Verdict);
        Assert.Contains(BlockedUrls.Label, held.Labels);
        Assert.Equal(ScreeningVerdict.Allow, fine.Verdict);
    }

    private sealed class FakeList(string entry) : IBlockedUrlList
    {
        public string? FindIn(string text) => BlockedUrls.Find(text).FirstOrDefault(u => BlockedUrls.Matches(entry, u));
    }
}
