using BJK.Modules.Posts.Domain;
using BJK.Modules.Shared.Safety;
using Xunit;

namespace BJK.Modules.Posts.Tests.Domain;

public sealed class HashtagParserTests
{
    [Fact]
    public void Tags_are_found_in_the_title_and_description_once_each_in_order()
    {
        var tags = HashtagParser.Extract("Lorry on the #M62 again #BadParking", "Same as yesterday #m62 #Leeds_Ring_Road.");

        Assert.Equal(new[] { "M62", "BadParking", "Leeds_Ring_Road" }, tags);
    }

    [Theory]
    [InlineData("email me at a#b")]
    [InlineData("https://example.com/page#section")]
    [InlineData("##double")]
    [InlineData("#1stTime")]
    [InlineData("#a")]
    [InlineData("C# is a language")]
    public void Things_that_arent_tags_are_ignored(string text) => Assert.Empty(HashtagParser.Extract(text));

    [Fact]
    public void Tags_longer_than_50_characters_are_ignored_and_at_most_10_are_kept()
    {
        Assert.Empty(HashtagParser.Extract("#" + new string('a', 51)));
        Assert.Single(HashtagParser.Extract("#" + new string('a', 50)));

        var many = string.Join(' ', Enumerable.Range(1, 15).Select(i => $"#tag{i}"));
        Assert.Equal(HashtagParser.MaxPerPost, HashtagParser.Extract(many).Count);
    }

    [Fact]
    public void Number_plates_and_offensive_tags_are_never_tags()
    {
        Assert.Empty(HashtagParser.Extract("#AB12CDE #ab12cde #killhim #dickheaddriver"));
        Assert.Equal(new[] { "Scunthorpe", "spicy", "Pakistan", "muppet" }, HashtagParser.Extract("#Scunthorpe #spicy #Pakistan #muppet"));
    }

    [Theory]
    [InlineData("killyou", true)]
    [InlineData("driverkillyou", true)]
    [InlineData("paki", true)]
    [InlineData("pakistan", false)]
    [InlineData("scunthorpe", false)]
    [InlineData("idiot", false)]
    [InlineData("m62", false)]
    public void Offensive_tags_are_whole_or_leading_or_trailing_matches(string tag, bool offensive) =>
        Assert.Equal(offensive, ScreeningKeywords.IsOffensiveTag(tag));

    [Theory]
    [InlineData("m", true)]
    [InlineData("M62", true)]
    [InlineData("bad_", true)]
    [InlineData("", false)]
    [InlineData("6", false)]
    [InlineData("bad park", false)]
    [InlineData(null, false)]
    public void Prefixes_start_with_a_letter(string? prefix, bool valid) => Assert.Equal(valid, HashtagParser.IsPrefix(prefix));
}
