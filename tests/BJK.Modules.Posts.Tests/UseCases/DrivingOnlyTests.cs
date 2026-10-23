using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Moderation;
using BJK.Modules.Posts.UseCases.Publishing;
using BJK.Modules.Posts.UseCases.Reports;
using BJK.Modules.Posts.UseCases.Safety;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Safety;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

/// <summary>Keeping posts about driving (owner, 23 Oct 2026): no vehicle, off-topic reports and pausing posting.</summary>
public sealed class DrivingOnlyTests
{
    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly ICommentRepository _comments = Substitute.For<ICommentRepository>();
    private readonly IReportRepository _reports = Substitute.For<IReportRepository>();
    private readonly IProfileDirectory _profiles = Substitute.For<IProfileDirectory>();
    private readonly IProfileBadges _badges = Substitute.For<IProfileBadges>();
    private readonly IProfileModeration _profileModeration = Substitute.For<IProfileModeration>();
    private readonly IMediaDirectory _media = Substitute.For<IMediaDirectory>();
    private readonly IModerationAuditLog _audit = Substitute.For<IModerationAuditLog>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly List<Report> _open = [];

    public DrivingOnlyTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _posts.GetPassengersAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<PostPassenger>());
        _reports.ListOpenForTargetAsync(ReportTargetType.Post, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => _open.ToList());
        _reports.When(r => r.Add(Arg.Any<Report>())).Do(c => _open.Add(c.Arg<Report>()));
        _badges.TrustedReportersAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- No vehicle in sight ---

    [Fact]
    public async Task A_post_with_no_vehicle_in_any_photo_is_held_for_a_moderator()
    {
        var post = Pending(2);
        MediaAre(post, false, false);

        await Publisher().RefreshAsync(post, Ct);

        Assert.Equal(PostMediaState.Ready, post.MediaState);
        Assert.Equal(PostStatus.HeldForReview, post.Status);
        Assert.True(post.IsFlagged);
        Assert.Contains(PostHoldReasons.NoVehicle, post.ScreeningLabels, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(null, false)]
    [InlineData(null, null)]
    public async Task One_vehicle_or_an_unchecked_photo_is_enough_to_publish(bool? first, bool? second)
    {
        var post = Pending(2);
        MediaAre(post, first, second);

        await Publisher().RefreshAsync(post, Ct);

        Assert.Equal(PostStatus.Published, post.Status);
        Assert.False(post.IsFlagged);
    }

    [Fact]
    public void A_hold_survives_the_text_being_screened_again_and_approval_ends_it()
    {
        var post = NewPost(UserId);
        Assert.True(post.HoldForReview(PostHoldReasons.NoVehicle, Now));

        post.ApplyScreening(new ScreeningResult(0.4, ["insult"], ScreeningVerdict.Flag));
        Assert.Equal(PostStatus.HeldForReview, post.Status);
        Assert.Equal("insult,no_vehicle", post.ScreeningLabels);
        Assert.True(post.WasHeldAsOffTopic);

        post.Approve(OtherUserId, Now);
        Assert.Equal(PostStatus.Published, post.Status);
        Assert.False(post.HoldForReview(PostHoldReasons.OffTopicReports, Now));
        Assert.Equal(PostStatus.Published, post.Status);
    }

    // --- "Not about driving" reports ---

    [Fact]
    public async Task Three_established_members_reporting_it_as_off_topic_hold_it()
    {
        var post = Existing(OtherUserId);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        People((first, Now.AddMonths(-3)), (second, Now.AddMonths(-3)), (UserId, Now.AddMonths(-3)));
        _open.Add(Report.Create(first, ReportTargetType.Post, post.Id, post.Id, ReportReason.NotADrivingIncident, null, Now));

        await ReportHandler().Handle(new ReportPostCommand(post.Id, ReportReason.Spam, null), Ct);
        Assert.Equal(PostStatus.Published, post.Status);

        _open.Add(Report.Create(second, ReportTargetType.Post, post.Id, post.Id, ReportReason.NotADrivingIncident, null, Now));
        _open.RemoveAll(r => r.ReporterId == UserId);
        await ReportHandler().Handle(new ReportPostCommand(post.Id, ReportReason.NotADrivingIncident, null), Ct);

        Assert.Equal(PostStatus.HeldForReview, post.Status);
        Assert.Contains(PostHoldReasons.OffTopicReports, post.ScreeningLabels, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_accounts_and_other_reasons_dont_count()
    {
        var post = Existing(OtherUserId);
        var newcomer = Guid.NewGuid();
        var other = Guid.NewGuid();
        People((newcomer, Now.AddHours(-2)), (other, Now.AddMonths(-3)), (UserId, Now.AddMonths(-3)));
        _open.Add(Report.Create(newcomer, ReportTargetType.Post, post.Id, post.Id, ReportReason.NotADrivingIncident, null, Now));
        _open.Add(Report.Create(other, ReportTargetType.Post, post.Id, post.Id, ReportReason.PersonalInformation, null, Now));

        await ReportHandler().Handle(new ReportPostCommand(post.Id, ReportReason.NotADrivingIncident, null), Ct);

        Assert.Equal(PostStatus.Published, post.Status);
    }

    [Fact]
    public async Task One_report_from_a_trusted_reporter_holds_it()
    {
        var post = Existing(OtherUserId);
        People((UserId, Now.AddMonths(-3)));
        _badges.TrustedReportersAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(UserId)), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { UserId });

        await ReportHandler().Handle(new ReportPostCommand(post.Id, ReportReason.NotADrivingIncident, null), Ct);

        Assert.Equal(PostStatus.HeldForReview, post.Status);
    }

    // --- Removed as off-topic, and pausing posting ---

    [Fact]
    public async Task Hiding_a_post_reported_as_off_topic_counts_against_its_author()
    {
        var post = Existing(OtherUserId);
        var report = Report.Create(UserId, ReportTargetType.Post, post.Id, post.Id, ReportReason.NotADrivingIncident, null, Now);
        _reports.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _open.Add(report);
        CallerIsModerator();

        await new ResolveReportCommandHandler(_reports, _posts, _comments, _profileModeration, _audit, Notifications(), _caller, _clock)
            .Handle(new ResolveReportCommand(report.Id, ReportAction.HideContent, null), Ct);

        Assert.Equal(Now, post.OffTopicRemovedAt);
        post.Unhide(Now);
        Assert.Null(post.OffTopicRemovedAt);
    }

    [Fact]
    public async Task Removing_a_post_held_for_no_vehicle_counts_against_its_author()
    {
        var post = Existing(OtherUserId);
        post.HoldForReview(PostHoldReasons.NoVehicle, Now);
        CallerIsModerator();
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<MediaSummary>());
        _posts.GetStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, PostStats>());
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, ProfileSummary>());

        await new ReviewScreenedPostCommandHandler(_posts, new PostViews(_posts, _profiles, _media, _caller), _audit, _caller, _clock)
            .Handle(new ReviewScreenedPostCommand(post.Id, ScreeningDecision.Remove, "A selfie, not driving"), Ct);

        Assert.Equal(PostStatus.Hidden, post.Status);
        Assert.Equal(Now, post.OffTopicRemovedAt);
    }

    [Fact]
    public async Task Two_off_topic_removals_in_30_days_pause_posting_for_a_week()
    {
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ProfileSummary> { [UserId] = new(UserId, "roadhog", "Road Hog", null, Now.AddYears(-1)) });
        var limits = new NewAccountLimits(_profiles, _posts, _comments, Options.Create(new SafetyOptions()), _clock);

        _posts.GetOffTopicRemovalsSinceAsync(UserId, Now.AddDays(-30), Arg.Any<CancellationToken>()).Returns((1, Now.AddDays(-1)));
        await limits.EnsureCanPostAsync(UserId, Ct);

        _posts.GetOffTopicRemovalsSinceAsync(UserId, Now.AddDays(-30), Arg.Any<CancellationToken>()).Returns((2, Now.AddDays(-2)));
        var paused = await Assert.ThrowsAsync<TooManyRequestsException>(() => limits.EnsureCanPostAsync(UserId, Ct));
        Assert.Equal(NewAccountLimits.PausedCode, paused.Code);
        Assert.Contains("paused until 23 October", paused.Message, StringComparison.Ordinal);

        _posts.GetOffTopicRemovalsSinceAsync(UserId, Now.AddDays(-30), Arg.Any<CancellationToken>()).Returns((2, Now.AddDays(-8)));
        await limits.EnsureCanPostAsync(UserId, Ct);
    }

    private ReportPostCommandHandler ReportHandler() =>
        new(_posts, _reports, Notifications(), _caller, _clock, Options.Create(new SafetyOptions()), _profiles, _badges);

    private PostMediaPublisher Publisher() =>
        new(_posts, _media, Notifications(), _clock, NullLogger<PostMediaPublisher>.Instance, _audit);

    private ModerationNotifications Notifications() =>
        new(_email, Options.Create(new EmailOptions()), Options.Create(new PostsOptions { ReportsInbox = "" }), _clock, NullLogger<ModerationNotifications>.Instance);

    private void CallerIsModerator()
    {
        _caller.HasScope(BJK.Modules.Posts.PostsModule.ManageScope).Returns(true);
        _caller.HasRole("Moderator").Returns(true);
        _caller.UsedMfa.Returns(true);
    }

    private void People(params (Guid Id, DateTimeOffset Joined)[] people) =>
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(people.ToDictionary(p => p.Id, p => new ProfileSummary(p.Id, "member", "Member", null, p.Joined)));

    private void MediaAre(Post post, params bool?[] vehicles) =>
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(
            post.Media.OrderBy(m => m.Position).Zip(vehicles).Select(x => new MediaSummary(
                x.First.MediaId,
                post.AuthorId,
                MediaType.Image,
                MediaStatus.Ready,
                new Uri($"https://media.example.com/{x.First.MediaId:D}.jpg"),
                new Uri($"https://media.example.com/{x.First.MediaId:D}-thumb.jpg"),
                null,
                1600,
                900,
                VehicleSeen: x.Second)).ToList());

    private static Post Pending(int mediaCount) =>
        Post.Create(UserId, Details(), Enumerable.Range(0, mediaCount).Select(_ => Guid.NewGuid()).ToList(), Now, mediaPending: true);

    private Post Existing(Guid authorId)
    {
        var post = NewPost(authorId);
        _posts.GetAsync(post.Id, Arg.Any<CancellationToken>()).Returns(post);
        return post;
    }
}
