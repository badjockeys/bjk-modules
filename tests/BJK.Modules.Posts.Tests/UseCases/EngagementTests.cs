using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Posts.UseCases.Engagement;
using BJK.Modules.Posts.UseCases.Reels;
using BJK.Modules.Posts.UseCases.Safety;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.Safety;
using BJK.Modules.Shared.Security;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

/// <summary>Votes, following posts, bookmarks, views and shares, reach and vote results (engagement plan, Oct 2026).</summary>
public sealed class EngagementTests
{
    private static readonly Guid Sam = Guid.Parse("6b1c2d3e-0000-4000-8000-0000000000c1");

    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly IEngagementRepository _engagement = Substitute.For<IEngagementRepository>();
    private readonly ICommentRepository _comments = Substitute.For<ICommentRepository>();
    private readonly IProfileDirectory _profiles = Substitute.For<IProfileDirectory>();
    private readonly IBlockDirectory _blocks = Substitute.For<IBlockDirectory>();
    private readonly IContentScreener _screener = Substitute.For<IContentScreener>();
    private readonly INotificationSender _notifications = Substitute.For<INotificationSender>();
    private readonly IHomeAreas _homeAreas = Substitute.For<IHomeAreas>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly List<PostVote> _votes = [];

    public EngagementTests()
    {
        SignedInAs(OtherUserId);
        _caller.EmailVerified.Returns(true);
        _blocks.GetBlockedIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _engagement.When(e => e.AddVote(Arg.Any<PostVote>())).Do(c => _votes.Add(c.Arg<PostVote>()));
        _engagement.GetVoteTalliesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<IReadOnlyCollection<Guid>>().ToDictionary(
                id => id,
                _ => _votes.Count == 0
                    ? VoteTally.Empty
                    : new VoteTally(_votes.GroupBy(v => v.Choice).ToDictionary(g => g.Key, g => g.Count()), _votes[^1].Choice)) as IReadOnlyDictionary<Guid, VoteTally>);
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ProfileSummary>
            {
                [UserId] = new(UserId, "roadhog", "Road Hog", null),
                [OtherUserId] = new(OtherUserId, "dashcam_dave", "Dashcam Dave", null),
                [Sam] = new(Sam, "sam_j", "Sam J", null),
            });
        _screener.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ScreeningResult.Clean);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Incidents_between_road_users_ask_whos_at_fault_and_the_rest_ask_how_bad()
    {
        Assert.Equal(VoteKind.Fault, PostVoting.KindFor([IncidentCategory.Tailgating]));
        Assert.Equal(VoteKind.Fault, PostVoting.KindFor([IncidentCategory.BadParking, IncidentCategory.RoadRage]));
        Assert.Equal(VoteKind.Severity, PostVoting.KindFor([IncidentCategory.BadParking]));
        Assert.Contains(VoteChoice.OtherRoadUser, PostVoting.ChoicesFor(VoteKind.Fault));
        Assert.DoesNotContain(VoteChoice.Dangerous, PostVoting.ChoicesFor(VoteKind.Fault));
    }

    [Theory]
    [InlineData(VoteDuration.Hours48, 48.0)]
    [InlineData(VoteDuration.Days7, 168.0)]
    public void Votes_close_after_the_authors_chosen_time(VoteDuration duration, double hours)
    {
        var post = NewPost(UserId);
        post.SetVoteDuration(duration);

        Assert.Equal(post.CreatedAt.AddHours(hours), post.VoteClosesAt);
        Assert.False(post.IsVoteClosed(post.CreatedAt.AddHours(hours).AddSeconds(-1)));
        Assert.True(post.IsVoteClosed(post.CreatedAt.AddHours(hours)));
    }

    [Fact]
    public void A_vote_set_to_never_close_never_does()
    {
        var post = NewPost(UserId);
        post.SetVoteDuration(VoteDuration.Never);

        Assert.Null(post.VoteClosesAt);
        Assert.False(post.IsVoteClosed(Now.AddYears(5)));
    }

    [Fact]
    public void Results_stay_hidden_until_you_vote_except_from_the_author()
    {
        var post = NewPost(UserId);
        var tally = new VoteTally(new Dictionary<VoteChoice, int> { [VoteChoice.OtherRoadUser] = 3 }, null);

        var stranger = VoteSummaryDto.For(post, tally, OtherUserId, Now);
        Assert.Null(stranger.Results);
        Assert.True(stranger.CanVote);
        Assert.Equal(3, stranger.Total);

        var author = VoteSummaryDto.For(post, tally, UserId, Now);
        Assert.Equal(3, author.Results![VoteChoice.OtherRoadUser]);
        Assert.False(author.CanVote);

        var voted = VoteSummaryDto.For(post, tally with { Mine = VoteChoice.OtherRoadUser }, OtherUserId, Now);
        Assert.NotNull(voted.Results);
    }

    [Fact]
    public void Signed_out_visitors_see_results_once_there_are_ten_votes()
    {
        var post = NewPost(UserId);

        Assert.Null(VoteSummaryDto.For(post, new VoteTally(new Dictionary<VoteChoice, int> { [VoteChoice.Both] = 9 }, null), null, Now).Results);
        Assert.NotNull(VoteSummaryDto.For(post, new VoteTally(new Dictionary<VoteChoice, int> { [VoteChoice.Both] = 10 }, null), null, Now).Results);
    }

    [Fact]
    public async Task Voting_records_your_answer_and_shows_the_results()
    {
        var post = Existing(UserId);

        var result = await CastVote().Handle(new CastVoteCommand(post.Id, VoteChoice.OtherRoadUser), Ct);

        var vote = Assert.Single(_votes);
        Assert.Equal(OtherUserId, vote.UserId);
        Assert.Equal(VoteChoice.OtherRoadUser, result.MyChoice);
        Assert.Equal(1, result.Results![VoteChoice.OtherRoadUser]);
    }

    [Fact]
    public async Task You_can_change_your_vote_until_it_closes()
    {
        var post = Existing(UserId);
        var existing = PostVote.Create(post.Id, OtherUserId, VoteChoice.Both, Now.AddHours(-1));
        _engagement.GetVoteAsync(post.Id, OtherUserId, Arg.Any<CancellationToken>()).Returns(existing);

        await CastVote().Handle(new CastVoteCommand(post.Id, VoteChoice.Nobody), Ct);

        Assert.Equal(VoteChoice.Nobody, existing.Choice);
        Assert.Empty(_votes);
    }

    [Fact]
    public async Task You_cant_vote_on_your_own_post_a_closed_vote_or_with_the_wrong_kind_of_answer()
    {
        var mine = Existing(OtherUserId);
        await Assert.ThrowsAsync<ValidationException>(() => CastVote().Handle(new CastVoteCommand(mine.Id, VoteChoice.Both), Ct));

        var closed = Existing(UserId);
        closed.SetVoteDuration(VoteDuration.Hours48);
        var later = new CastVoteCommandHandler(_posts, _engagement, _blocks, _caller, new FixedTimeProvider(closed.CreatedAt.AddDays(3)));
        await Assert.ThrowsAsync<ValidationException>(() => later.Handle(new CastVoteCommand(closed.Id, VoteChoice.Both), Ct));

        var fault = Existing(UserId);
        await Assert.ThrowsAsync<ValidationException>(() => CastVote().Handle(new CastVoteCommand(fault.Id, VoteChoice.Shocking), Ct));

        Assert.Empty(_votes);
    }

    [Fact]
    public async Task Saving_is_capped_at_a_thousand_bookmarks()
    {
        var post = Existing(UserId);
        _engagement.ListBookmarkedAsync(OtherUserId, 1, 1, Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<Post>)new List<Post>(), SavePostCommandHandler.MaxBookmarks));

        await Assert.ThrowsAsync<ValidationException>(() =>
            new SavePostCommandHandler(_posts, _engagement, _caller, _clock).Handle(new SavePostCommand(post.Id), Ct));
        _engagement.DidNotReceiveWithAnyArgs().AddBookmark(null!);
    }

    [Fact]
    public async Task Views_count_public_posts_by_others_and_say_when_the_viewer_is_local()
    {
        var near = Existing(UserId);
        var mine = Existing(OtherUserId);
        _posts.GetManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<Post> { near, mine });
        _homeAreas.GetAsync(OtherUserId, Arg.Any<CancellationToken>()).Returns(new HomeArea("Bury", 53.59m, -2.29m, 10));
        IReadOnlyCollection<ViewToRecord>? recorded = null;
        _engagement.RecordViewsAsync(Arg.Do<IReadOnlyCollection<ViewToRecord>>(v => recorded = v), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(1);

        await new RecordViewsCommandHandler(_posts, _engagement, _caller, Options.Create(new ReelsOptions()), _clock, _homeAreas)
            .Handle(new RecordViewsCommand([near.Id, mine.Id], new ViewerContext(null, "203.0.113.9")), Ct);

        var view = Assert.Single(recorded!);
        Assert.Equal(near.Id, view.PostId);
        Assert.Equal(OtherUserId, view.ViewerKey);
        Assert.True(view.IsLocal);
    }

    [Fact]
    public void Distances_are_great_circle_kilometres()
    {
        // Bury to Manchester Piccadilly, about 13 km.
        var km = GeoDistance.Km(53.5933m, -2.2966m, 53.4774m, -2.2309m);

        Assert.InRange(km, 12.5, 14.5);
        Assert.Equal(0, GeoDistance.Km(53.5m, -2.2m, 53.5m, -2.2m), 6);
    }

    [Fact]
    public async Task Only_the_author_sees_a_posts_reach()
    {
        var post = Existing(UserId);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new GetPostReachQueryHandler(_posts, _engagement, _caller).Handle(new GetPostReachQuery(post.Id), Ct));
    }

    [Fact]
    public async Task Commenting_follows_the_post_and_tells_its_other_followers()
    {
        var post = Existing(UserId);
        _engagement.ListFollowerIdsAsync(post.Id, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { UserId, OtherUserId, Sam });

        await AddComment().Handle(new AddCommentCommand(post.Id, "Shocking overtake."), Ct);

        await _engagement.Received(1).EnsureFollowingAsync(post.Id, OtherUserId, Now, Arg.Any<CancellationToken>());
        await _notifications.Received(1).NotifyAsync(
            Arg.Is<NotificationRequest>(n => n.UserId == UserId && n.Type == NotificationType.CommentOnPost),
            Arg.Any<CancellationToken>());
        await _notifications.Received(1).NotifyAsync(
            Arg.Is<NotificationRequest>(n => n.UserId == Sam && n.Type == NotificationType.CommentOnFollowedPost),
            Arg.Any<CancellationToken>());
        await _notifications.DidNotReceive().NotifyAsync(
            Arg.Is<NotificationRequest>(n => n.UserId == OtherUserId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Closed_votes_are_announced_once_to_the_author_and_followers()
    {
        var post = NewPost(UserId);
        post.SetVoteDuration(VoteDuration.Hours48);
        _engagement.ListVotesToAnnounceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Post> { post });
        _votes.Add(PostVote.Create(post.Id, Sam, VoteChoice.OtherRoadUser, Now));
        _engagement.ListFollowerIdsAsync(post.Id, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Guid> { UserId, Sam });
        var announcer = new VoteResultAnnouncer(_engagement, new FixedTimeProvider(Now.AddDays(3)), NullLogger<VoteResultAnnouncer>.Instance, _notifications);

        Assert.Equal(1, await announcer.AnnounceAsync(Ct));

        Assert.NotNull(post.VoteResultSentAt);
        await _notifications.Received(2).NotifyAsync(
            Arg.Is<NotificationRequest>(n => n.Type == NotificationType.VoteClosed
                && n.Detail == "Most said the other road user was at fault (100% of 1 vote)."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Results_are_described_in_one_sentence()
    {
        Assert.Equal("Nobody voted.", VoteResultAnnouncer.Describe(VoteTally.Empty));
        Assert.Equal(
            "Most said it was dangerous (63% of 8 votes).",
            VoteResultAnnouncer.Describe(new VoteTally(new Dictionary<VoteChoice, int> { [VoteChoice.Dangerous] = 5, [VoteChoice.Careless] = 3 }, null)));
        Assert.Equal(
            "It was a tie (4 votes).",
            VoteResultAnnouncer.Describe(new VoteTally(new Dictionary<VoteChoice, int> { [VoteChoice.Both] = 2, [VoteChoice.Nobody] = 2 }, null)));
    }

    [Fact]
    public async Task Authors_choose_when_votes_on_their_new_posts_close()
    {
        AuthorPreferences? saved = null;
        _engagement.When(e => e.AddPreferences(Arg.Any<AuthorPreferences>())).Do(c => saved = c.Arg<AuthorPreferences>());

        var result = await new SetAuthorPreferencesCommandHandler(_engagement, _caller, _clock)
            .Handle(new SetAuthorPreferencesCommand(VoteDuration.Never), Ct);

        Assert.Equal(VoteDuration.Never, result.VoteDuration);
        Assert.Equal(VoteDuration.Never, saved!.VoteDuration);
        var defaults = await new GetAuthorPreferencesQueryHandler(Substitute.For<IEngagementRepository>(), _caller)
            .Handle(new GetAuthorPreferencesQuery(), Ct);
        Assert.Equal(VoteDuration.Days7, defaults.VoteDuration);
    }

    private CastVoteCommandHandler CastVote() => new(_posts, _engagement, _blocks, _caller, _clock);

    private AddCommentCommandHandler AddComment() =>
        new(
            _posts,
            _comments,
            _profiles,
            _blocks,
            _screener,
            new NewAccountLimits(_profiles, _posts, _comments, Options.Create(new SafetyOptions()), _clock),
            _caller,
            _clock,
            _notifications,
            engagement: _engagement);

    private void SignedInAs(Guid userId) => _caller.IdentityId.Returns(userId.ToString());

    private Post Existing(Guid authorId)
    {
        var post = NewPost(authorId);
        _posts.GetAsync(post.Id, Arg.Any<CancellationToken>()).Returns(post);
        return post;
    }
}
