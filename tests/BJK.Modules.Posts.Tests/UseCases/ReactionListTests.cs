using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Reactions;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Security;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

/// <summary>Who reacted to a post (owner, 22 Oct 2026).</summary>
public sealed class ReactionListTests
{
    private static readonly Guid Adult = Guid.Parse("6b1c2d3e-0000-4000-8000-0000000000a1");
    private static readonly Guid Teen = Guid.Parse("6b1c2d3e-0000-4000-8000-0000000000a2");
    private static readonly Guid Blocked = Guid.Parse("6b1c2d3e-0000-4000-8000-0000000000a3");
    private static readonly Guid Sam = Guid.Parse("6b1c2d3e-0000-4000-8000-0000000000a4");

    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly IReactionRepository _reactions = Substitute.For<IReactionRepository>();
    private readonly IProfileDirectory _profiles = Substitute.For<IProfileDirectory>();
    private readonly IBlockDirectory _blocks = Substitute.For<IBlockDirectory>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly Post _post = NewPost(UserId);

    public ReactionListTests()
    {
        _caller.IdentityId.Returns(OtherUserId.ToString());
        _posts.GetAsync(_post.Id, Arg.Any<CancellationToken>()).Returns(_post);
        _blocks.GetBlockedIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _blocks.GetBlockedEitherWayAsync(OtherUserId, Arg.Any<CancellationToken>()).Returns(new List<Guid> { Blocked });
        _reactions.ListForPostAsync(_post.Id, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Reaction>
        {
            Reaction.Create(_post.Id, Adult, ReactionType.Heart, Now),
            Reaction.Create(_post.Id, Teen, ReactionType.Heart, Now.AddMinutes(-1)),
            Reaction.Create(_post.Id, Blocked, ReactionType.Angry, Now.AddMinutes(-2)),
            Reaction.Create(_post.Id, Sam, ReactionType.Like, Now.AddMinutes(-3)),
        });
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlyDictionary<Guid, ProfileSummary>)new Dictionary<Guid, ProfileSummary>
            {
                [Adult] = new(Adult, "dashcam_dave", "Dashcam Dave", null, Now, IsAdult: true),
                [Teen] = new(Teen, "young_one", "Young One", null, Now, IsAdult: false),
                [Blocked] = new(Blocked, "blocked_bob", "Blocked Bob", null, Now, IsAdult: true),
                [Sam] = new(Sam, "sam_k", "Sam Khan", null, Now, IsAdult: true),
            }.Where(p => c.Arg<IReadOnlyCollection<Guid>>().Contains(p.Key)).ToDictionary());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ListReactionsQueryHandler Handler() => new(_posts, _reactions, _profiles, _blocks, _caller);

    [Fact]
    public async Task Under_18s_are_counted_but_not_named_and_blocked_people_are_left_out()
    {
        var result = await Handler().Handle(new ListReactionsQuery(_post.Id), Ct);

        Assert.Equal(3, result.Total);
        Assert.Equal(2, result.Counts[ReactionType.Heart]);
        Assert.Equal(1, result.Counts[ReactionType.Like]);
        Assert.False(result.Counts.ContainsKey(ReactionType.Angry));
        Assert.Equal(["dashcam_dave", "sam_k"], result.People.Items.Select(p => p.Handle));
        Assert.Equal(1, result.UnnamedCount);
    }

    [Fact]
    public async Task Filters_by_reaction_and_searches_names()
    {
        var hearts = await Handler().Handle(new ListReactionsQuery(_post.Id, ReactionType.Heart), Ct);
        Assert.Equal(["dashcam_dave"], hearts.People.Items.Select(p => p.Handle));
        Assert.Equal(1, hearts.UnnamedCount);

        var search = await Handler().Handle(new ListReactionsQuery(_post.Id, Search: "khan"), Ct);
        Assert.Equal(["sam_k"], search.People.Items.Select(p => p.Handle));
        Assert.Equal(1, search.People.TotalCount);
    }
}
