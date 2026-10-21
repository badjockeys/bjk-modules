using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure;
using Xunit;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class BlockedUrlTests
{
    [Theory]
    [InlineData("see https://www.bad.example/anything here", true)]
    [InlineData("see cdn.bad.example/x", true)]
    [InlineData("see other.example/path/to/bad/page/deeper", true)]
    [InlineData("see other.example/path/to/fine", false)]
    [InlineData("see good.example", false)]
    [InlineData("no links at all", false)]
    public void Hosts_cover_their_sites_and_paths_cover_the_pages_under_them(string text, bool blocked)
    {
        var list = new BlockedUrlList();
        list.Load(["bad.example", "other.example/path/to/bad/page"]);

        Assert.Equal(blocked, list.FindIn(text) is not null);
    }

    [Fact]
    public void Entries_are_stored_normalised()
    {
        var url = new BlockedUrl("HTTPS://WWW.Bad.Example/Page/", BlockedUrl.ModerationSource, "test", DateTimeOffset.UnixEpoch);

        Assert.Equal("bad.example/page", url.Pattern);
        Assert.Throws<ArgumentException>(() => new BlockedUrl("nonsense", BlockedUrl.ModerationSource, null, DateTimeOffset.UnixEpoch));
    }
}
