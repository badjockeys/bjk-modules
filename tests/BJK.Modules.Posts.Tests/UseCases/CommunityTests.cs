using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Communities;
using BJK.Modules.Posts.UseCases.Safety;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Safety;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

/// <summary>Communities (owner, 23 Oct 2026).</summary>
public sealed class CommunityTests
{
    private readonly ICommunityRepository _communities = Substitute.For<ICommunityRepository>();
    private readonly IProfileDirectory _profiles = Substitute.For<IProfileDirectory>();
    private readonly IPlanResolver _plans = Substitute.For<IPlanResolver>();
    private readonly IContentScreener _screener = Substitute.For<IContentScreener>();
    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly IBlockDirectory _blocks = Substitute.For<IBlockDirectory>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);

    public CommunityTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _caller.EmailVerified.Returns(true);
        _plans.CurrentPlan().Returns(SubscriptionPlan.Premium);
        _screener.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ScreeningResult.Clean);
        People(adult: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- The community itself ---

    [Theory]
    [InlineData("Salford Road Watch!", "salford-road-watch")]
    [InlineData("  A6 / A580 — Swinton ", "a6-a580-swinton")]
    [InlineData("Café Racers", "cafe-racers")]
    [InlineData("!!!", "community")]
    public void Addresses_come_from_the_name(string name, string slug) => Assert.Equal(slug, Community.SlugFrom(name));

    [Fact]
    public void An_area_covers_points_within_its_radius()
    {
        var salford = Make(CommunityVisibility.Public, new CommunityArea("Salford", 53.4875m, -2.2901m, 10));

        Assert.True(salford.Covers(53.51m, -2.33m));
        Assert.False(salford.Covers(53.80m, -1.55m));
        Assert.False(Make(CommunityVisibility.Public).Covers(53.4875m, -2.2901m));
    }

    [Fact]
    public async Task Premium_adults_start_communities_and_are_their_admins()
    {
        _communities.SlugTakenAsync("salford-road-watch", Arg.Any<CancellationToken>()).Returns(true);

        var dto = await Create().Handle(new CreateCommunityCommand("Salford Road Watch", "Clips and chat", null, CommunityVisibility.Public), Ct);

        Assert.Equal("salford-road-watch-2", dto.Slug);
        Assert.True(dto.IsAdmin);
        Assert.Equal(1, dto.MemberCount);
        _communities.Received(1).Add(Arg.Is<CommunityMember>(m => m.UserId == UserId && m.Role == CommunityRole.Admin && m.IsMember));
    }

    [Fact]
    public async Task Free_members_and_under_18s_cannot_start_one()
    {
        _plans.CurrentPlan().Returns(SubscriptionPlan.Free);
        await Assert.ThrowsAsync<ForbiddenException>(() => Create().Handle(new CreateCommunityCommand("Mine", null, null, CommunityVisibility.Public), Ct));

        _plans.CurrentPlan().Returns(SubscriptionPlan.PremiumPlus);
        People(adult: false);
        await Assert.ThrowsAsync<ForbiddenException>(() => Create().Handle(new CreateCommunityCommand("Mine", null, null, CommunityVisibility.Public), Ct));
    }

    [Fact]
    public void An_area_needs_a_place_a_UK_point_and_an_offered_radius()
    {
        var validator = new CreateCommunityCommandValidator();

        Assert.True(validator.Validate(new CreateCommunityCommand("Salford", null, null, CommunityVisibility.Public, "Salford", 53.48m, -2.29m, 10)).IsValid);
        Assert.False(validator.Validate(new CreateCommunityCommand("Salford", null, null, CommunityVisibility.Public, "Salford", 53.48m, -2.29m, 7)).IsValid);
        Assert.False(validator.Validate(new CreateCommunityCommand("Salford", null, null, CommunityVisibility.Public, null, 53.48m, -2.29m, 10)).IsValid);
        Assert.False(validator.Validate(new CreateCommunityCommand("Paris", null, null, CommunityVisibility.Public, "Paris", 48.85m, 2.35m, 10)).IsValid);
    }

    // --- Joining ---

    [Fact]
    public async Task Joining_a_public_community_is_instant()
    {
        var community = Make(CommunityVisibility.Public);

        var dto = await Join().Handle(new JoinCommunityCommand(community.Slug), Ct);

        Assert.Equal(CommunityStanding.Member, dto.Standing);
        Assert.Equal(1, community.MemberCount);
    }

    [Fact]
    public async Task Private_communities_take_requests_from_adults_only()
    {
        var community = Make(CommunityVisibility.Private);

        var dto = await Join().Handle(new JoinCommunityCommand(community.Slug), Ct);
        Assert.Equal(CommunityStanding.Requested, dto.Standing);
        Assert.Equal(0, community.MemberCount);

        _communities.ClearReceivedCalls();
        People(adult: false);
        var error = await Assert.ThrowsAsync<ForbiddenException>(() => Join().Handle(new JoinCommunityCommand(community.Slug), Ct));
        Assert.Contains("Private communities are for adults", error.Message, StringComparison.Ordinal);
        _communities.DidNotReceive().Add(Arg.Any<CommunityMember>());
    }

    [Fact]
    public async Task Banned_people_cannot_join_again()
    {
        var community = Make(CommunityVisibility.Public);
        var banned = CommunityMember.Join(community.Id, UserId, Now);
        banned.Ban(Now);
        _communities.GetMemberAsync(community.Id, UserId, Arg.Any<CancellationToken>()).Returns(banned);

        await Assert.ThrowsAsync<ForbiddenException>(() => Join().Handle(new JoinCommunityCommand(community.Slug), Ct));
    }

    [Fact]
    public async Task Moderators_approve_requests()
    {
        var community = Make(CommunityVisibility.Private);
        Member(community, UserId, CommunityRole.Moderator);
        var asking = CommunityMember.Request(community.Id, OtherUserId, Now);
        _communities.GetMemberAsync(community.Id, OtherUserId, Arg.Any<CancellationToken>()).Returns(asking);

        await new DecideCommunityRequestCommandHandler(_communities, _caller, _clock).Handle(new DecideCommunityRequestCommand(community.Slug, OtherUserId, true), Ct);

        Assert.True(asking.IsMember);
        Assert.Equal(1, community.MemberCount);
    }

    [Fact]
    public async Task Plain_members_cannot_decide_requests()
    {
        var community = Make(CommunityVisibility.Private);
        Member(community, UserId, CommunityRole.Member);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => new DecideCommunityRequestCommandHandler(_communities, _caller, _clock).Handle(new DecideCommunityRequestCommand(community.Slug, OtherUserId, true), Ct));
    }

    [Fact]
    public async Task The_last_admin_cannot_leave()
    {
        var community = Make(CommunityVisibility.Public);
        Member(community, UserId, CommunityRole.Admin);
        _communities.CountAdminsAsync(community.Id, Arg.Any<CancellationToken>()).Returns(1);

        await Assert.ThrowsAsync<ValidationException>(() => new LeaveCommunityCommandHandler(_communities, _caller).Handle(new LeaveCommunityCommand(community.Slug), Ct));
    }

    [Fact]
    public async Task Under_18s_are_never_made_admins_or_moderators()
    {
        var community = Make(CommunityVisibility.Public);
        Member(community, UserId, CommunityRole.Admin);
        Member(community, OtherUserId, CommunityRole.Member);
        _profiles.GetSummariesAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(OtherUserId)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ProfileSummary> { [OtherUserId] = new(OtherUserId, "teen", "Teen", null, Now.AddDays(-90), IsAdult: false) });

        await Assert.ThrowsAsync<ValidationException>(
            () => new SetCommunityRoleCommandHandler(_communities, _profiles, _caller, _clock).Handle(new SetCommunityRoleCommand(community.Slug, OtherUserId, CommunityRole.Moderator), Ct));
    }

    // --- What's inside ---

    [Fact]
    public async Task Only_members_see_inside_a_private_community()
    {
        var community = Make(CommunityVisibility.Private);
        var views = new PostViews(_posts, _profiles, Substitute.For<BJK.Modules.Media.Contracts.IMediaDirectory>(), _caller);
        var handler = new ListCommunityPostsQueryHandler(_communities, _blocks, views, _caller);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new ListCommunityPostsQuery(community.Slug), Ct));

        Member(community, UserId, CommunityRole.Member);
        _blocks.GetBlockedEitherWayAsync(UserId, Arg.Any<CancellationToken>()).Returns([]);
        _communities.ListSharedPostsAsync(community.Id, Arg.Any<IReadOnlyCollection<Guid>>(), 1, 20, Arg.Any<CancellationToken>()).Returns((Array.Empty<Post>(), 0));
        Assert.Empty((await handler.Handle(new ListCommunityPostsQuery(community.Slug), Ct)).Items);
    }

    [Fact]
    public async Task Members_share_their_own_posts()
    {
        var community = Make(CommunityVisibility.Public);
        _communities.GetAsync(community.Id, Arg.Any<CancellationToken>()).Returns(community);
        var post = NewPost(UserId);
        _posts.GetAsync(post.Id, Arg.Any<CancellationToken>()).Returns(post);
        var handler = new SharePostToCommunitiesCommandHandler(_communities, _posts, _caller, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new SharePostToCommunitiesCommand(post.Id, [community.Id]), Ct));

        Member(community, UserId, CommunityRole.Member);
        await handler.Handle(new SharePostToCommunitiesCommand(post.Id, [community.Id]), Ct);
        _communities.Received(1).Add(Arg.Is<CommunityPost>(s => s.CommunityId == community.Id && s.PostId == post.Id && s.SharedBy == UserId));

        var theirs = NewPost(OtherUserId);
        _posts.GetAsync(theirs.Id, Arg.Any<CancellationToken>()).Returns(theirs);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new SharePostToCommunitiesCommand(theirs.Id, [community.Id]), Ct));
    }

    [Fact]
    public async Task Communities_covering_where_it_happened_are_recommended_first()
    {
        var salford = Make(CommunityVisibility.Public, new CommunityArea("Salford", 53.4875m, -2.2901m, 10), "Salford Road Watch");
        var cyclists = Make(CommunityVisibility.Public, null, "Cyclists UK");
        _communities.ListJoinedAsync(UserId, Arg.Any<CancellationToken>()).Returns([cyclists, salford]);

        var suggestions = await new SuggestCommunitiesQueryHandler(_communities, _posts, _caller).Handle(new SuggestCommunitiesQuery(53.50m, -2.31m), Ct);

        Assert.Equal(["Salford Road Watch", "Cyclists UK"], suggestions.Select(s => s.Name));
        Assert.True(suggestions[0].Recommended);
        Assert.False(suggestions[1].Recommended);
    }

    [Fact]
    public async Task Members_start_discussions_and_abusive_text_is_refused()
    {
        var community = Make(CommunityVisibility.Public);
        var handler = new StartCommunityDiscussionCommandHandler(_communities, _profiles, _screener, Limits(), _caller, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new StartCommunityDiscussionCommand(community.Slug, "Worsley roundabout", null), Ct));

        Member(community, UserId, CommunityRole.Member);
        var dto = await handler.Handle(new StartCommunityDiscussionCommand(community.Slug, "Worsley roundabout", "Nearly hit again"), Ct);
        Assert.Equal("Worsley roundabout", dto.Title);
        Assert.True(dto.CanRemove);

        _screener.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ScreeningResult(0.95, ["threat"], ScreeningVerdict.Hold));
        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new StartCommunityDiscussionCommand(community.Slug, "Find him", "and sort him out"), Ct));
    }

    [Fact]
    public async Task Replies_count_and_moderators_remove_them()
    {
        var community = Make(CommunityVisibility.Public);
        Member(community, UserId, CommunityRole.Member);
        var discussion = CommunityDiscussion.Start(community.Id, OtherUserId, "Worsley roundabout", null, false, Now);
        _communities.GetDiscussionAsync(community.Id, discussion.Id, Arg.Any<CancellationToken>()).Returns(discussion);

        var reply = await new ReplyToCommunityDiscussionCommandHandler(_communities, _profiles, _blocks, _screener, Limits(), _caller, _clock)
            .Handle(new ReplyToCommunityDiscussionCommand(community.Slug, discussion.Id, "Same here"), Ct);
        Assert.Equal(1, discussion.ReplyCount);

        // Someone else's reply: only moderators can remove it.
        var theirs = CommunityReply.Create(discussion, OtherUserId, "Hello", false, Now);
        _communities.FindReplyAsync(theirs.Id, Arg.Any<CancellationToken>()).Returns(theirs);
        var remove = new RemoveCommunityReplyCommandHandler(_communities, _caller, _clock);
        await Assert.ThrowsAsync<ForbiddenException>(() => remove.Handle(new RemoveCommunityReplyCommand(community.Slug, discussion.Id, theirs.Id), Ct));

        Member(community, UserId, CommunityRole.Moderator);
        await remove.Handle(new RemoveCommunityReplyCommand(community.Slug, discussion.Id, theirs.Id), Ct);
        Assert.Equal(CommunityContentStatus.Removed, theirs.Status);
        Assert.NotEqual(Guid.Empty, reply.Id);
    }

    private CreateCommunityCommandHandler Create() => new(_communities, _profiles, _plans, _screener, _caller, _clock);

    private JoinCommunityCommandHandler Join() => new(_communities, _profiles, _caller, _clock);

    private NewAccountLimits Limits() =>
        new(_profiles, _posts, Substitute.For<ICommentRepository>(), Options.Create(new SafetyOptions()), _clock);

    private void People(bool adult) =>
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ProfileSummary> { [UserId] = new(UserId, "roadhog", "Road Hog", null, Now.AddDays(-90), adult) });

    private Community Make(CommunityVisibility visibility, CommunityArea? area = null, string name = "Salford Road Watch")
    {
        var community = Community.Create(OtherUserId, Community.SlugFrom(name), new CommunityDetails(name, null, null, visibility, area), Now);
        _communities.GetBySlugAsync(community.Slug, Arg.Any<CancellationToken>()).Returns(community);
        return community;
    }

    private void Member(Community community, Guid userId, CommunityRole role) =>
        _communities.GetMemberAsync(community.Id, userId, Arg.Any<CancellationToken>()).Returns(CommunityMember.Join(community.Id, userId, Now, role));
}
