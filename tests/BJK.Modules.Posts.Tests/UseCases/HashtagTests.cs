using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Hashtags;
using BJK.Modules.Shared.Exceptions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class HashtagTests
{
    private readonly IHashtagRepository _hashtags = Substitute.For<IHashtagRepository>();
    private readonly HashtagOptions _settings = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HashtagSync Sync() => new(_hashtags, Options.Create(_settings), new FixedTimeProvider(Now));

    private static Post PostWith(string title, string? description = null) =>
        Post.Create(UserId, Details(title) with { Description = description }, [Guid.NewGuid()], Now);

    [Fact]
    public async Task A_new_post_creates_missing_tags_and_links_each_once()
    {
        var existing = Hashtag.Create("M62", Now.AddDays(-3));
        _hashtags.GetLinksAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<PostHashtag>());
        _hashtags.GetByKeysAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Hashtag> { existing });
        var post = PostWith("Lorry on the #m62", "Again! #M62 #Leeds");

        await Sync().SyncAsync(post, Ct);

        _hashtags.Received(1).Add(Arg.Is<Hashtag>(h => h.Key == "leeds" && h.Display == "Leeds"));
        _hashtags.Received(1).Add(Arg.Is<PostHashtag>(l => l.HashtagId == existing.Id && l.AuthorId == UserId && l.PostedAt == post.CreatedAt));
        _hashtags.Received(2).Add(Arg.Any<PostHashtag>());
        Assert.Equal(1, existing.TotalUses);
        Assert.Equal("M62", existing.Display);
    }

    [Fact]
    public async Task Editing_a_post_unlinks_tags_that_were_removed()
    {
        var post = PostWith("Now about #Leeds only");
        var m62 = Hashtag.Create("M62", Now);
        m62.Used();
        var leeds = Hashtag.Create("Leeds", Now);
        leeds.Used();
        var m62Link = new PostHashtag(post.Id, m62.Id, UserId, Now);
        _hashtags.GetLinksAsync(post.Id, Arg.Any<CancellationToken>())
            .Returns(new List<PostHashtag> { m62Link, new(post.Id, leeds.Id, UserId, Now) });
        _hashtags.GetByKeysAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(new List<Hashtag> { leeds });
        _hashtags.GetByIdsAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(m62.Id)), Arg.Any<CancellationToken>())
            .Returns(new List<Hashtag> { m62 });

        await Sync().SyncAsync(post, Ct);

        _hashtags.Received(1).Remove(m62Link);
        _hashtags.DidNotReceive().Add(Arg.Any<PostHashtag>());
        Assert.Equal(0, m62.TotalUses);
        Assert.Equal(1, leeds.TotalUses);
    }

    [Fact]
    public async Task Blocklisted_tags_are_never_recorded()
    {
        _settings.Blocklist.Add("#SomeBrand");
        _hashtags.GetLinksAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<PostHashtag>());
        _hashtags.GetByKeysAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(new List<Hashtag>());

        await Sync().SyncAsync(PostWith("Buy #somebrand"), Ct);

        _hashtags.DidNotReceive().Add(Arg.Any<Hashtag>());
        _hashtags.DidNotReceive().Add(Arg.Any<PostHashtag>());
    }

    [Fact]
    public async Task Suggestions_use_the_lower_case_prefix_and_unknown_tags_are_not_found()
    {
        var m62 = Hashtag.Create("M62", Now);
        m62.Used();
        _hashtags.SuggestAsync("m6", 8, Arg.Any<CancellationToken>()).Returns(new List<Hashtag> { m62 });

        var suggestions = await new SuggestHashtagsQueryHandler(_hashtags).Handle(new SuggestHashtagsQuery("M6"), Ct);

        Assert.Equal(new[] { new HashtagSuggestionDto("M62", 1) }, suggestions);
        Assert.False(new SuggestHashtagsQueryValidator().Validate(new SuggestHashtagsQuery("6")).IsValid);
        await Assert.ThrowsAsync<NotFoundException>(() => new GetHashtagQueryHandler(_hashtags).Handle(new GetHashtagQuery("#nothing"), Ct));
    }

    [Fact]
    public async Task Trending_asks_for_the_last_day_against_the_week_before_with_the_minimums()
    {
        _hashtags.TrendingTagsAsync(default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new List<TrendingRow> { new() { Name = "M62", Posts = 12, Authors = 5, BaselinePosts = 7 } });
        _hashtags.TrendingPlacesAsync(default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new List<TrendingRow> { new() { Name = "Prescot", Posts = 9, Authors = 4 } });

        var trending = await new GetTrendingQueryHandler(_hashtags, Options.Create(_settings), new FixedTimeProvider(Now))
            .Handle(new GetTrendingQuery(), Ct);

        Assert.Equal(new[] { new TrendingTagDto("M62", 12) }, trending.Hashtags);
        Assert.Equal(new[] { new TrendingPlaceDto("Prescot", 9) }, trending.Places);
        await _hashtags.Received().TrendingTagsAsync(Now.AddHours(-24), Now.AddHours(-24).AddDays(-7), 7, 3, 3, 10, Arg.Any<CancellationToken>());
        await _hashtags.Received().TrendingPlacesAsync(Now.AddHours(-24), Now.AddHours(-24).AddDays(-7), 7, 3, 3, 5, Arg.Any<CancellationToken>());
    }
}
