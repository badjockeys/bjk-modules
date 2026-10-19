using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Posts.UseCases.Create;
using BJK.Modules.Posts.UseCases.Delete;
using BJK.Modules.Posts.UseCases.Get;
using BJK.Modules.Posts.UseCases.Moderation;
using BJK.Modules.Posts.UseCases.Reactions;
using BJK.Modules.Posts.UseCases.Reports;
using BJK.Modules.Posts.UseCases.Safety;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Safety;
using BJK.Modules.Shared.Security;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class PostHandlerTests
{
    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly IReactionRepository _reactions = Substitute.For<IReactionRepository>();
    private readonly ICommentRepository _comments = Substitute.For<ICommentRepository>();
    private readonly IReportRepository _reports = Substitute.For<IReportRepository>();
    private readonly IProfileDirectory _profiles = Substitute.For<IProfileDirectory>();
    private readonly IMediaDirectory _media = Substitute.For<IMediaDirectory>();
    private readonly IBlockDirectory _blocks = Substitute.For<IBlockDirectory>();
    private readonly IProfileModeration _profileModeration = Substitute.For<IProfileModeration>();
    private readonly IContentScreener _screener = Substitute.For<IContentScreener>();
    private readonly IModerationAuditLog _audit = Substitute.For<IModerationAuditLog>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly PostViews _views;

    public PostHandlerTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _caller.EmailVerified.Returns(true);
        _profiles.ExistsAsync(UserId, Arg.Any<CancellationToken>()).Returns(true);
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ProfileSummary>
            {
                [UserId] = new(UserId, "roadhog", "Road Hog", null),
            });
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MediaSummary>());
        _posts.GetStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PostStats>());
        _screener.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ScreeningResult.Clean);
        _blocks.GetBlockedIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _views = new PostViews(_posts, _profiles, _media);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_publishes_a_post_with_the_callers_ready_media()
    {
        var mediaId = Guid.NewGuid();
        MediaAre(Ready(mediaId, UserId));
        var handler = CreateHandler();

        var dto = await handler.Handle(Command(mediaId), Ct);

        Assert.Equal(PostStatus.Published, dto.Status);
        Assert.Equal("roadhog", dto.Author.Handle);
        Assert.Single(dto.Media);
        _posts.Received(1).Add(Arg.Is<Post>(p => p.AuthorId == UserId && p.Media.Single().MediaId == mediaId));
        await _posts.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_needs_a_profile()
    {
        _profiles.ExistsAsync(UserId, Arg.Any<CancellationToken>()).Returns(false);
        var handler = CreateHandler();

        var ex = await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(Command(Guid.NewGuid()), Ct));

        Assert.Contains(ex.Errors, e => e.PropertyName == "Profile");
    }

    [Fact]
    public async Task Create_rejects_someone_elses_media()
    {
        var mediaId = Guid.NewGuid();
        MediaAre(Ready(mediaId, OtherUserId));
        var handler = CreateHandler();

        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(Command(mediaId), Ct));
        _posts.DidNotReceive().Add(Arg.Any<Post>());
    }

    [Fact]
    public async Task Create_rejects_media_still_processing()
    {
        var mediaId = Guid.NewGuid();
        MediaAre(new MediaSummary(mediaId, UserId, MediaType.Video, MediaStatus.Processing, null, null, null, null, null));
        var handler = CreateHandler();

        var ex = await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(Command(mediaId), Ct));

        Assert.Contains("Processing", ex.Errors.Single().ErrorMessage);
    }

    [Fact]
    public async Task Create_rejects_media_already_in_a_post()
    {
        var mediaId = Guid.NewGuid();
        MediaAre(Ready(mediaId, UserId));
        _posts.AnyUseMediaAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);
        var handler = CreateHandler();

        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(Command(mediaId), Ct));
    }

    [Fact]
    public async Task Hidden_posts_look_missing_to_other_users()
    {
        var post = Existing(OtherUserId);
        post.Hide("Reported", Now);
        var handler = new GetPostQueryHandler(_posts, _views, _blocks, _caller);

        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new GetPostQuery(post.Id), Ct));
    }

    [Fact]
    public async Task Hidden_posts_are_still_shown_to_their_author()
    {
        var post = Existing(UserId);
        post.Hide("Reported", Now);
        var handler = new GetPostQueryHandler(_posts, _views, _blocks, _caller);

        var dto = await handler.Handle(new GetPostQuery(post.Id), Ct);

        Assert.Equal(PostStatus.Hidden, dto.Status);
    }

    [Fact]
    public async Task Delete_by_someone_else_is_forbidden()
    {
        var post = Existing(OtherUserId);
        var handler = DeleteHandler();

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new DeletePostCommand(post.Id), Ct));
        Assert.True(post.IsPublished);
    }

    [Fact]
    public async Task Delete_takes_the_post_down_and_removes_its_media()
    {
        var post = Existing(UserId, mediaCount: 2);
        var handler = DeleteHandler();

        await handler.Handle(new DeletePostCommand(post.Id), Ct);

        Assert.Equal(PostStatus.Deleted, post.Status);
        await _media.Received(1).DeleteAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Moderators_can_delete_any_post_on_the_admin_endpoint()
    {
        var post = Existing(OtherUserId);
        CallerIsModerator();
        var handler = DeleteHandler();

        await handler.Handle(new DeletePostCommand(post.Id, AsModerator: true), Ct);

        Assert.Equal(PostStatus.Deleted, post.Status);
    }

    [Fact]
    public async Task Moderators_cant_use_the_public_endpoint_on_others_posts()
    {
        var post = Existing(OtherUserId);
        CallerIsModerator();
        var handler = DeleteHandler();

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new DeletePostCommand(post.Id), Ct));
    }

    [Fact]
    public async Task The_admin_endpoint_needs_two_factor_sign_in()
    {
        var post = Existing(OtherUserId);
        CallerIsModerator();
        _caller.UsedMfa.Returns(false);
        var handler = DeleteHandler();

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new DeletePostCommand(post.Id, AsModerator: true), Ct));
        Assert.True(post.IsPublished);
    }

    [Fact]
    public async Task Posting_needs_a_confirmed_email()
    {
        _caller.EmailVerified.Returns(false);
        var handler = CreateHandler();

        var ex = await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(Command(Guid.NewGuid()), Ct));

        Assert.Contains(ex.Errors, e => e.PropertyName == "Email");
    }

    [Fact]
    public async Task Reacting_again_changes_the_reaction()
    {
        var post = Existing(OtherUserId);
        var existing = Reaction.Create(post.Id, UserId, ReactionType.Like, Now);
        _reactions.GetAsync(post.Id, UserId, Arg.Any<CancellationToken>()).Returns(existing);
        var handler = new ReactToPostCommandHandler(_posts, _reactions, _blocks, _caller, _clock);

        await handler.Handle(new ReactToPostCommand(post.Id, ReactionType.Angry), Ct);

        Assert.Equal(ReactionType.Angry, existing.Type);
        _reactions.DidNotReceive().Add(Arg.Any<Reaction>());
        await _reactions.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cant_react_to_a_hidden_post()
    {
        var post = Existing(OtherUserId);
        post.Hide("Reported", Now);
        var handler = new ReactToPostCommandHandler(_posts, _reactions, _blocks, _caller, _clock);

        await Assert.ThrowsAsync<NotFoundException>(
            () => handler.Handle(new ReactToPostCommand(post.Id, ReactionType.Like), Ct));
    }

    [Fact]
    public async Task Only_the_author_can_edit_a_comment()
    {
        var post = Existing(OtherUserId);
        var comment = Comment.Create(post.Id, OtherUserId, "Shocking driving", Now);
        _comments.GetAsync(post.Id, comment.Id, Arg.Any<CancellationToken>()).Returns(comment);
        var handler = new EditCommentCommandHandler(_posts, _comments, _profiles, _screener, _caller, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => handler.Handle(new EditCommentCommand(post.Id, comment.Id, "Edited"), Ct));
    }

    [Fact]
    public async Task Reporting_twice_is_refused()
    {
        var post = Existing(OtherUserId);
        _reports.ExistsAsync(UserId, ReportTargetType.Post, post.Id, Arg.Any<CancellationToken>()).Returns(true);
        var handler = new ReportPostCommandHandler(_posts, _reports, Notifications(), _caller, _clock);

        await Assert.ThrowsAsync<ValidationException>(
            () => handler.Handle(new ReportPostCommand(post.Id, ReportReason.Spam, null), Ct));
        _reports.DidNotReceive().Add(Arg.Any<Report>());
    }

    [Fact]
    public async Task Hiding_reported_content_closes_every_open_report_about_it()
    {
        var post = Existing(OtherUserId);
        var report = Report.Create(UserId, ReportTargetType.Post, post.Id, post.Id, ReportReason.PersonalInformation, null, Now);
        var another = Report.Create(Guid.NewGuid(), ReportTargetType.Post, post.Id, post.Id, ReportReason.Other, null, Now);
        _reports.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _reports.ListOpenForTargetAsync(ReportTargetType.Post, post.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Report> { report, another });
        CallerIsModerator();
        var handler = ResolveHandler();

        await handler.Handle(new ResolveReportCommand(report.Id, ReportAction.HideContent, "Shows a face"), Ct);

        Assert.Equal(PostStatus.Hidden, post.Status);
        Assert.Equal("Shows a face", post.HiddenReason);
        Assert.Equal(ReportStatus.ContentHidden, report.Status);
        Assert.Equal(ReportStatus.ContentHidden, another.Status);
        await _reports.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_admins_resolve_reports()
    {
        var handler = ResolveHandler();

        await Assert.ThrowsAsync<ForbiddenException>(
            () => handler.Handle(new ResolveReportCommand(Guid.NewGuid(), ReportAction.Dismiss, null), Ct));
    }

    private void CallerIsModerator()
    {
        _caller.HasScope(BJK.Modules.Posts.PostsModule.ManageScope).Returns(true);
        _caller.HasRole("Moderator").Returns(true);
        _caller.UsedMfa.Returns(true);
    }

    private CreatePostCommandHandler CreateHandler() =>
        new(_posts, _profiles, _media, _screener, Limits(), _views, _caller, _clock);

    private DeletePostCommandHandler DeleteHandler() =>
        new(_posts, _media, _audit, _caller, _clock, NullLogger<DeletePostCommandHandler>.Instance);

    private ResolveReportCommandHandler ResolveHandler() =>
        new(_reports, _posts, _comments, _profileModeration, _audit, Notifications(), _caller, _clock);

    private NewAccountLimits Limits(SafetyOptions? options = null) =>
        new(_profiles, _posts, _comments, Options.Create(options ?? new SafetyOptions()), _clock);

    private ModerationNotifications Notifications() =>
        new(_email, Options.Create(new EmailOptions()), _clock, NullLogger<ModerationNotifications>.Instance);

    private static CreatePostCommand Command(params Guid[] mediaIds)
    {
        var d = Details();
        return new CreatePostCommand(d.Title, d.Description, d.Category, d.IncidentAt, d.Latitude, d.Longitude, d.PlaceName, mediaIds);
    }

    private static MediaSummary Ready(Guid id, Guid owner) => new(
        id,
        owner,
        MediaType.Video,
        MediaStatus.Ready,
        new Uri($"https://media.example.com/{owner:D}/{id:D}/720p.mp4"),
        new Uri($"https://media.example.com/{owner:D}/{id:D}/thumbnail.jpg"),
        12.5m,
        1280,
        720);

    private void MediaAre(params MediaSummary[] items) =>
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(items.ToList());

    private Post Existing(Guid authorId, int mediaCount = 1)
    {
        var post = NewPost(authorId, mediaCount);
        _posts.GetAsync(post.Id, Arg.Any<CancellationToken>()).Returns(post);
        return post;
    }
}
