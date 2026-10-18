using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Appeals;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Posts.UseCases.Create;
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

public sealed class OnlineSafetyTests
{
    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly IReactionRepository _reactions = Substitute.For<IReactionRepository>();
    private readonly ICommentRepository _comments = Substitute.For<ICommentRepository>();
    private readonly ICommentReactionRepository _commentReactions = Substitute.For<ICommentReactionRepository>();
    private readonly IReportRepository _reports = Substitute.For<IReportRepository>();
    private readonly IAppealRepository _appeals = Substitute.For<IAppealRepository>();
    private readonly IProfileDirectory _profiles = Substitute.For<IProfileDirectory>();
    private readonly IBlockDirectory _blocks = Substitute.For<IBlockDirectory>();
    private readonly IProfileModeration _profileModeration = Substitute.For<IProfileModeration>();
    private readonly IMediaDirectory _media = Substitute.For<IMediaDirectory>();
    private readonly IContentScreener _screener = Substitute.For<IContentScreener>();
    private readonly ICaptchaVerifier _captcha = Substitute.For<ICaptchaVerifier>();
    private readonly IModerationAuditLog _audit = Substitute.For<IModerationAuditLog>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly PostViews _views;

    public OnlineSafetyTests()
    {
        SignedInAs(UserId);
        _profiles.ExistsAsync(UserId, Arg.Any<CancellationToken>()).Returns(true);
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ProfileSummary>
            {
                [UserId] = new(UserId, "roadhog", "Road Hog", null, Now.AddYears(-1)),
            });
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<MediaSummary>());
        _posts.GetStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PostStats>());
        _posts.SearchAsync(Arg.Any<PostSearch>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new List<Post>(), 0));
        _comments.ListTopLevelAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new List<Comment>(), 0));
        _comments.ListRepliesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Comment>());
        _comments.CountRepliesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _commentReactions.GetStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, CommentReactionStats>());
        _screener.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ScreeningResult.Clean);
        _blocks.GetBlockedIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _captcha.VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(CaptchaResult.Passed);
        _views = new PostViews(_posts, _profiles, _media);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Sensitive posts and logged-out viewers

    [Fact]
    public async Task Sensitive_posts_look_missing_to_logged_out_viewers()
    {
        var post = Existing(OtherUserId, sensitive: true);
        SignedOut();

        await Assert.ThrowsAsync<NotFoundException>(() => GetHandler().Handle(new GetPostQuery(post.Id), Ct));
    }

    [Fact]
    public async Task Signed_in_viewers_see_sensitive_posts_marked_as_such()
    {
        var post = Existing(OtherUserId, sensitive: true);

        var dto = await GetHandler().Handle(new GetPostQuery(post.Id), Ct);

        Assert.True(dto.IsSensitive);
        Assert.False(dto.CommentsRequireSignIn);
    }

    [Fact]
    public async Task Logged_out_viewers_are_told_comments_need_sign_in()
    {
        var post = Existing(OtherUserId);
        SignedOut();

        var dto = await GetHandler().Handle(new GetPostQuery(post.Id), Ct);

        Assert.True(dto.CommentsRequireSignIn);
    }

    [Fact]
    public async Task The_feed_leaves_out_sensitive_posts_for_logged_out_viewers_only()
    {
        SignedOut();
        await FeedHandler().Handle(new ListPostsQuery(), Ct);
        await _posts.Received(1).SearchAsync(Arg.Is<PostSearch>(s => s.ExcludeSensitive), 1, 20, Arg.Any<CancellationToken>());

        _posts.ClearReceivedCalls();
        SignedInAs(UserId);
        await FeedHandler().Handle(new ListPostsQuery(), Ct);
        await _posts.Received(1).SearchAsync(Arg.Is<PostSearch>(s => !s.ExcludeSensitive), 1, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Logged_out_viewers_cant_list_comments()
    {
        var post = Existing(OtherUserId);
        SignedOut();

        await Assert.ThrowsAsync<ForbiddenException>(() => CommentsHandler().Handle(new ListCommentsQuery(post.Id), Ct));
    }

    [Fact]
    public async Task Authors_can_tag_their_post_sensitive_when_creating_it()
    {
        var mediaId = ReadyMedia();

        var dto = await CreateHandler().Handle(Command(mediaId) with { IsSensitive = true }, Ct);

        Assert.True(dto.IsSensitive);
    }

    // Blocking

    [Fact]
    public async Task Blocked_authors_posts_are_left_out_of_the_feed_and_look_missing()
    {
        var post = Existing(OtherUserId);
        _blocks.GetBlockedIdsAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Guid> { OtherUserId });
        _blocks.IsBlockedAsync(UserId, OtherUserId, Arg.Any<CancellationToken>()).Returns(true);

        await FeedHandler().Handle(new ListPostsQuery(), Ct);

        await _posts.Received(1).SearchAsync(
            Arg.Is<PostSearch>(s => s.ExcludeAuthorIds != null && s.ExcludeAuthorIds.Contains(OtherUserId)),
            1,
            20,
            Arg.Any<CancellationToken>());
        await Assert.ThrowsAsync<NotFoundException>(() => GetHandler().Handle(new GetPostQuery(post.Id), Ct));
    }

    [Fact]
    public async Task Blocked_authors_comments_are_left_out()
    {
        var post = Existing(OtherUserId);
        var blocked = Guid.NewGuid();
        _blocks.GetBlockedIdsAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Guid> { blocked });

        await CommentsHandler().Handle(new ListCommentsQuery(post.Id), Ct);

        await _comments.Received(1).ListTopLevelAsync(
            post.Id,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(blocked)),
            1,
            50,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_blocked_user_cant_react_to_or_comment_on_the_blockers_posts()
    {
        var post = Existing(OtherUserId);
        _blocks.IsBlockedAsync(OtherUserId, UserId, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => new ReactToPostCommandHandler(_posts, _reactions, _blocks, _caller, _clock).Handle(new ReactToPostCommand(post.Id, ReactionType.Like), Ct));
        await Assert.ThrowsAsync<ForbiddenException>(
            () => AddCommentHandler().Handle(new AddCommentCommand(post.Id, "Nice driving"), Ct));
        _reactions.DidNotReceive().Add(Arg.Any<Reaction>());
        _comments.DidNotReceive().Add(Arg.Any<Comment>());
    }

    // Replies and comment reactions

    [Fact]
    public async Task Replies_attach_to_a_top_level_comment()
    {
        var post = Existing(OtherUserId);
        var parent = ExistingComment(post, OtherUserId);

        var reply = await AddCommentHandler().Handle(new AddCommentCommand(post.Id, "Agreed, shocking", parent.Id), Ct);

        Assert.Equal(parent.Id, reply.ParentCommentId);
        _comments.Received(1).Add(Arg.Is<Comment>(c => c.ParentCommentId == parent.Id && c.PostId == post.Id));
    }

    [Fact]
    public async Task Replying_to_a_reply_is_refused()
    {
        var post = Existing(OtherUserId);
        var parent = ExistingComment(post, OtherUserId);
        var reply = Comment.CreateReply(parent, OtherUserId, "First reply", Now);
        _comments.GetAsync(post.Id, reply.Id, Arg.Any<CancellationToken>()).Returns(reply);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => AddCommentHandler().Handle(new AddCommentCommand(post.Id, "Reply to a reply", reply.Id), Ct));

        Assert.Contains(ex.Errors, e => e.PropertyName == nameof(AddCommentCommand.ParentCommentId));
        Assert.Throws<InvalidOperationException>(() => Comment.CreateReply(reply, UserId, "Nope", Now));
    }

    [Fact]
    public async Task Replies_are_screened_and_checked_like_comments()
    {
        var post = Existing(OtherUserId);
        var parent = ExistingComment(post, OtherUserId);
        Screening(0.95, ScreeningVerdict.Hold, "threat");

        var reply = await AddCommentHandler().Handle(new AddCommentCommand(post.Id, "I know where you live", parent.Id), Ct);

        Assert.Equal(CommentStatus.HeldForReview, reply.Status);
        Assert.False(new AddCommentCommandValidator().Validate(new AddCommentCommand(post.Id, "@roadhog on insta", parent.Id)).IsValid);
    }

    [Fact]
    public async Task A_removed_comment_keeps_its_replies_under_a_placeholder()
    {
        var post = Existing(OtherUserId);
        var parent = Comment.Create(post.Id, OtherUserId, "Something nasty", Now);
        var reply = Comment.CreateReply(parent, UserId, "That's out of order", Now.AddMinutes(1));
        parent.Hide("Abusive", Now);
        _comments.ListTopLevelAsync(post.Id, Arg.Any<IReadOnlyCollection<Guid>>(), 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<Comment> { parent }, 1));
        _comments.ListRepliesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Comment> { reply });
        _comments.CountRepliesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [parent.Id] = 1 });

        var page = await CommentsHandler().Handle(new ListCommentsQuery(post.Id), Ct);

        var shown = Assert.Single(page.Items);
        Assert.True(shown.IsRemoved);
        Assert.Null(shown.Author);
        Assert.Equal(string.Empty, shown.Body);
        Assert.Equal(1, shown.ReplyCount);
        Assert.Equal("That's out of order", Assert.Single(shown.Replies!).Body);
    }

    [Fact]
    public async Task Comment_lists_show_reaction_counts_and_my_reaction()
    {
        var post = Existing(OtherUserId);
        var comment = Comment.Create(post.Id, OtherUserId, "Great clip", Now);
        _comments.ListTopLevelAsync(post.Id, Arg.Any<IReadOnlyCollection<Guid>>(), 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<Comment> { comment }, 1));
        _commentReactions.GetStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, CommentReactionStats>
            {
                [comment.Id] = new(new Dictionary<ReactionType, int> { [ReactionType.Like] = 4, [ReactionType.Laugh] = 1 }, ReactionType.Like),
            });

        var shown = Assert.Single((await CommentsHandler().Handle(new ListCommentsQuery(post.Id), Ct)).Items);

        Assert.Equal(4, shown.Reactions![ReactionType.Like]);
        Assert.Equal(ReactionType.Like, shown.MyReaction);
        Assert.False(shown.IsRemoved);
    }

    [Fact]
    public async Task Logged_out_viewers_cant_list_replies()
    {
        var post = Existing(OtherUserId);
        SignedOut();
        var handler = new ListRepliesQueryHandler(_posts, _comments, Threads(), _blocks, _caller);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new ListRepliesQuery(post.Id, Guid.NewGuid()), Ct));
    }

    [Fact]
    public async Task Reacting_to_a_comment_again_changes_the_reaction()
    {
        var post = Existing(OtherUserId);
        var comment = ExistingComment(post, OtherUserId);
        var existing = CommentReaction.Create(comment.Id, UserId, ReactionType.Like, Now);
        _commentReactions.GetAsync(comment.Id, UserId, Arg.Any<CancellationToken>()).Returns(existing);

        await CommentReactHandler().Handle(new ReactToCommentCommand(post.Id, comment.Id, ReactionType.Shocked), Ct);

        Assert.Equal(ReactionType.Shocked, existing.Type);
        _commentReactions.DidNotReceive().Add(Arg.Any<CommentReaction>());
        await _commentReactions.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_blocked_user_cant_react_to_the_blockers_comment()
    {
        var post = Existing(Guid.NewGuid());
        var comment = ExistingComment(post, OtherUserId);
        _blocks.IsBlockedAsync(OtherUserId, UserId, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => CommentReactHandler().Handle(new ReactToCommentCommand(post.Id, comment.Id, ReactionType.Like), Ct));
        _commentReactions.DidNotReceive().Add(Arg.Any<CommentReaction>());
    }

    [Fact]
    public async Task Removing_a_comment_reaction()
    {
        var existing = CommentReaction.Create(Guid.NewGuid(), UserId, ReactionType.Angry, Now);
        _commentReactions.GetAsync(existing.CommentId, UserId, Arg.Any<CancellationToken>()).Returns(existing);

        await new RemoveCommentReactionCommandHandler(_commentReactions, _caller).Handle(new RemoveCommentReactionCommand(Guid.NewGuid(), existing.CommentId), Ct);

        _commentReactions.Received(1).Remove(existing);
    }

    // Content screening

    [Fact]
    public async Task High_scoring_posts_are_held_for_review()
    {
        var mediaId = ReadyMedia();
        Screening(0.95, ScreeningVerdict.Hold, "threat");

        var dto = await CreateHandler().Handle(Command(mediaId), Ct);

        Assert.Equal(PostStatus.HeldForReview, dto.Status);
        _posts.Received(1).Add(Arg.Is<Post>(p => p.IsFlagged && p.ScreeningLabels == "threat"));
    }

    [Fact]
    public async Task High_scoring_comments_are_held_and_medium_ones_flagged()
    {
        var post = Existing(OtherUserId);

        Screening(0.95, ScreeningVerdict.Hold, "threat");
        var held = await AddCommentHandler().Handle(new AddCommentCommand(post.Id, "I know where you live"), Ct);
        Screening(0.65, ScreeningVerdict.Flag, "insult");
        var flagged = await AddCommentHandler().Handle(new AddCommentCommand(post.Id, "What a wanker"), Ct);

        Assert.Equal(CommentStatus.HeldForReview, held.Status);
        Assert.Equal(CommentStatus.Visible, flagged.Status);
        _comments.Received(1).Add(Arg.Is<Comment>(c => c.Status == CommentStatus.Visible && c.IsFlagged));
    }

    [Fact]
    public async Task Held_posts_are_only_visible_to_their_author()
    {
        var post = Existing(OtherUserId);
        post.ApplyScreening(new ScreeningResult(0.9, ["threat"], ScreeningVerdict.Hold));

        await Assert.ThrowsAsync<NotFoundException>(() => GetHandler().Handle(new GetPostQuery(post.Id), Ct));

        SignedInAs(OtherUserId);
        Assert.Equal(PostStatus.HeldForReview, (await GetHandler().Handle(new GetPostQuery(post.Id), Ct)).Status);
    }

    [Fact]
    public async Task Moderators_approve_a_held_comment_and_it_is_audited()
    {
        var comment = Comment.Create(Guid.NewGuid(), OtherUserId, "Hmm", Now);
        comment.ApplyScreening(new ScreeningResult(0.9, ["threat"], ScreeningVerdict.Hold));
        _comments.FindAsync(comment.Id, Arg.Any<CancellationToken>()).Returns(comment);
        CallerIsModerator();
        var handler = new ReviewScreenedCommentCommandHandler(_comments, _profiles, _audit, _caller, _clock);

        var dto = await handler.Handle(new ReviewScreenedCommentCommand(comment.Id, ScreeningDecision.Approve, "Banter, not a threat"), Ct);

        Assert.Equal(CommentStatus.Visible, dto.Comment.Status);
        Assert.False(dto.IsFlagged);
        await _audit.Received(1).RecordAsync(
            Arg.Is<ModerationAuditEntry>(e => e.Action == ModerationActions.ApproveComment && e.Reason == "Banter, not a threat"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Moderators_remove_a_flagged_post_by_hiding_it()
    {
        var post = Existing(OtherUserId);
        post.ApplyScreening(new ScreeningResult(0.6, ["insult"], ScreeningVerdict.Flag));
        CallerIsModerator();
        var handler = new ReviewScreenedPostCommandHandler(_posts, _views, _audit, _caller, _clock);

        await handler.Handle(new ReviewScreenedPostCommand(post.Id, ScreeningDecision.Remove, "Abusive"), Ct);

        Assert.Equal(PostStatus.Hidden, post.Status);
        await _audit.Received(1).RecordAsync(Arg.Is<ModerationAuditEntry>(e => e.Action == ModerationActions.HidePost), Arg.Any<CancellationToken>());
    }

    // New-account limits

    [Fact]
    public async Task New_accounts_can_only_post_a_few_times_in_their_first_day()
    {
        NewAccount();
        _posts.CountCreatedSinceAsync(UserId, Now.AddHours(-24), Arg.Any<CancellationToken>()).Returns(3);

        var ex = await Assert.ThrowsAsync<TooManyRequestsException>(() => CreateHandler().Handle(Command(ReadyMedia()), Ct));

        Assert.Equal(NewAccountLimits.Code, ex.Code);
        _posts.DidNotReceive().Add(Arg.Any<Post>());
    }

    [Fact]
    public async Task New_accounts_can_only_comment_a_few_times_in_their_first_day()
    {
        var post = Existing(OtherUserId);
        NewAccount();
        _comments.CountCreatedSinceAsync(UserId, Now.AddHours(-24), Arg.Any<CancellationToken>()).Returns(20);

        await Assert.ThrowsAsync<TooManyRequestsException>(() => AddCommentHandler().Handle(new AddCommentCommand(post.Id, "Shocking"), Ct));
    }

    [Fact]
    public async Task Older_accounts_arent_limited()
    {
        _posts.CountCreatedSinceAsync(UserId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(50);

        await CreateHandler().Handle(Command(ReadyMedia()), Ct);

        _posts.Received(1).Add(Arg.Any<Post>());
    }

    [Fact]
    public async Task New_account_limits_are_configurable()
    {
        NewAccount();
        _posts.CountCreatedSinceAsync(UserId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(3);
        var limits = Limits(new SafetyOptions { NewAccounts = { MaxPosts = 5 } });

        await limits.EnsureCanPostAsync(UserId, Ct);
    }

    // Reports

    [Fact]
    public async Task Public_reports_need_a_valid_captcha()
    {
        _captcha.VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(CaptchaResult.Failed);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => PublicHandler().Handle(PublicReport(Guid.NewGuid().ToString()), Ct));

        Assert.Contains(ex.Errors, e => e.PropertyName == CaptchaOptions.HeaderName);
        _reports.DidNotReceive().Add(Arg.Any<Report>());
    }

    [Fact]
    public async Task Public_reports_say_so_when_the_captcha_service_is_down()
    {
        _captcha.VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(CaptchaResult.Unavailable);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => PublicHandler().Handle(PublicReport(Guid.NewGuid().ToString()), Ct));
    }

    [Fact]
    public async Task Illegal_content_reported_through_the_public_form_is_urgent_and_acknowledged()
    {
        var post = Existing(OtherUserId);

        var receipt = await PublicHandler().Handle(
            PublicReport($"https://badjockeys.com/posts/{post.Id}?utm=share", ReportReason.IllegalContent),
            Ct);

        Assert.Equal(ReportPriority.Urgent, receipt.Priority);
        Assert.Equal(Now + Report.UrgentResponseTime, receipt.RespondBy);
        Assert.True(receipt.AcknowledgementSent);
        Assert.StartsWith("R-", receipt.Reference);
        _reports.Received(1).Add(Arg.Is<Report>(r =>
            r.Source == ReportSource.PublicForm
            && r.ReporterId == null
            && r.ContactEmail == "sam@example.com"
            && r.TargetId == post.Id
            && r.AcknowledgedAt == Now));
        await _email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "sam@example.com" && m.Subject.Contains(receipt.Reference)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Im_in_this_clip_reports_are_normal_priority_with_a_48_hour_target()
    {
        var post = Existing(OtherUserId);

        var receipt = await PublicHandler().Handle(PublicReport(post.Id.ToString(), ReportReason.InThisClip), Ct);

        Assert.Equal(ReportPriority.Normal, receipt.Priority);
        Assert.Equal(Now.AddHours(48), receipt.RespondBy);
    }

    [Fact]
    public async Task Public_reports_need_a_link_to_a_real_post()
    {
        await Assert.ThrowsAsync<ValidationException>(() => PublicHandler().Handle(PublicReport("https://example.com/video"), Ct));
        await Assert.ThrowsAsync<ValidationException>(() => PublicHandler().Handle(PublicReport(Guid.NewGuid().ToString()), Ct));
    }

    [Fact]
    public void The_public_form_needs_contact_details_consent_and_a_public_reason()
    {
        var validator = new PublicReportCommandValidator();

        Assert.True(validator.Validate(PublicReport(Guid.NewGuid().ToString())).IsValid);
        Assert.False(validator.Validate(PublicReport(Guid.NewGuid().ToString()) with { Consent = false }).IsValid);
        Assert.False(validator.Validate(PublicReport(Guid.NewGuid().ToString()) with { ReporterEmail = "not an email" }).IsValid);
        Assert.False(validator.Validate(PublicReport(Guid.NewGuid().ToString()) with { Reason = ReportReason.Spam }).IsValid);
        Assert.False(validator.Validate(PublicReport(Guid.NewGuid().ToString()) with { CaptchaToken = null }).IsValid);
    }

    [Theory]
    [InlineData("0193a6a1-2f1e-7c3d-8e4f-123456789abc", true)]
    [InlineData("https://badjockeys.com/posts/0193a6a1-2f1e-7c3d-8e4f-123456789abc", true)]
    [InlineData("https://badjockeys.com/", false)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    public void Post_ids_are_found_in_links(string target, bool found)
    {
        Assert.Equal(found, PublicReportCommandHandler.ParsePostId(target) is not null);
    }

    [Fact]
    public async Task Signed_in_users_can_report_profiles_and_get_an_acknowledgement()
    {
        _profiles.FindByHandleAsync("speedy", Arg.Any<CancellationToken>()).Returns(new ProfileSummary(OtherUserId, "speedy", "Speedy", null));
        _caller.Email.Returns("rider@example.com");
        var handler = new ReportProfileCommandHandler(_profiles, _reports, Notifications(), _caller, _clock);

        await handler.Handle(new ReportProfileCommand("@speedy", ReportReason.HarassmentOrHate, "Abusive bio"), Ct);

        _reports.Received(1).Add(Arg.Is<Report>(r =>
            r.TargetType == ReportTargetType.Profile && r.TargetId == OtherUserId && r.PostId == null && r.ReporterId == UserId));
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.To == "rider@example.com"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Hiding_a_reported_profile_resets_it_audits_and_tells_the_reporter()
    {
        var report = Report.Create(UserId, ReportTargetType.Profile, OtherUserId, null, ReportReason.HarassmentOrHate, null, Now, "rider@example.com");
        _reports.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _reports.ListOpenForTargetAsync(ReportTargetType.Profile, OtherUserId, Arg.Any<CancellationToken>()).Returns(new List<Report> { report });
        _profileModeration.ResetPublicDetailsAsync(OtherUserId, Arg.Any<CancellationToken>()).Returns(true);
        CallerIsModerator();

        var dto = await ResolveHandler().Handle(new ResolveReportCommand(report.Id, ReportAction.HideContent, "Abusive bio"), Ct);

        Assert.Equal(ReportStatus.ContentHidden, dto.Status);
        Assert.Equal(Now, dto.OutcomeNotifiedAt);
        await _profileModeration.Received(1).ResetPublicDetailsAsync(OtherUserId, Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync(
            Arg.Is<ModerationAuditEntry>(e => e.Action == ModerationActions.ResolveReport && e.Reason == "Abusive bio" && e.After!.Contains("reset")),
            Arg.Any<CancellationToken>());
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.To == "rider@example.com"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_outcome_email_doesnt_undo_the_decision()
    {
        var post = Existing(OtherUserId);
        var report = Report.Create(UserId, ReportTargetType.Post, post.Id, post.Id, ReportReason.Spam, null, Now, "rider@example.com");
        _reports.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new BJK.Modules.Shared.Delivery.MessageDeliveryException("down", isTransient: true)));
        CallerIsModerator();

        var dto = await ResolveHandler().Handle(new ResolveReportCommand(report.Id, ReportAction.Dismiss, "Not spam"), Ct);

        Assert.Equal(ReportStatus.Dismissed, dto.Status);
        Assert.Null(dto.OutcomeNotifiedAt);
    }

    [Fact]
    public void Moderation_actions_need_a_reason()
    {
        Assert.False(new ResolveReportCommandValidator().Validate(new ResolveReportCommand(Guid.NewGuid(), ReportAction.Dismiss, " ")).IsValid);
        Assert.False(new UnhidePostCommandValidator().Validate(new UnhidePostCommand(Guid.NewGuid(), null)).IsValid);
        Assert.False(new SetPostSensitiveCommandValidator().Validate(new SetPostSensitiveCommand(Guid.NewGuid(), true, "")).IsValid);
        Assert.False(new DecideAppealCommandValidator().Validate(new DecideAppealCommand(Guid.NewGuid(), AppealDecision.Uphold, null)).IsValid);
        Assert.False(new BJK.Modules.Posts.UseCases.Delete.DeletePostCommandValidator()
            .Validate(new BJK.Modules.Posts.UseCases.Delete.DeletePostCommand(Guid.NewGuid(), AsModerator: true)).IsValid);
        Assert.True(new BJK.Modules.Posts.UseCases.Delete.DeletePostCommandValidator()
            .Validate(new BJK.Modules.Posts.UseCases.Delete.DeletePostCommand(Guid.NewGuid())).IsValid);
        Assert.False(new DeleteCommentCommandValidator().Validate(new DeleteCommentCommand(Guid.NewGuid(), Guid.NewGuid(), AsModerator: true)).IsValid);
    }

    // Appeals

    [Fact]
    public async Task Authors_can_appeal_a_hidden_post_once()
    {
        var post = Existing(UserId);
        post.Hide("Reported", Now);
        var handler = new AppealPostCommandHandler(_posts, _appeals, Notifications(), _caller, _clock);

        var dto = await handler.Handle(new AppealPostCommand(post.Id, "The plate was blurred"), Ct);

        Assert.Equal(AppealStatus.Open, dto.Status);
        _appeals.Received(1).Add(Arg.Is<Appeal>(a => a.TargetId == post.Id && a.AppellantId == UserId));

        _appeals.ExistsAsync(UserId, ReportTargetType.Post, post.Id, Arg.Any<CancellationToken>()).Returns(true);
        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new AppealPostCommand(post.Id, "Again"), Ct));
    }

    [Fact]
    public async Task Only_hidden_content_can_be_appealed_and_only_by_its_author()
    {
        var mine = Existing(UserId);
        var theirs = Existing(OtherUserId);
        theirs.Hide("Reported", Now);
        var handler = new AppealPostCommandHandler(_posts, _appeals, Notifications(), _caller, _clock);

        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new AppealPostCommand(mine.Id, "Why?"), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new AppealPostCommand(theirs.Id, "Why?"), Ct));
    }

    [Fact]
    public async Task Overturning_an_appeal_shows_the_post_again_and_is_audited()
    {
        var post = Existing(OtherUserId);
        post.Hide("Reported", Now);
        var appeal = Appeal.Create(OtherUserId, ReportTargetType.Post, post.Id, post.Id, "It was blurred", "owner@example.com", Now);
        _appeals.GetAsync(appeal.Id, Arg.Any<CancellationToken>()).Returns(appeal);
        CallerIsModerator();
        var handler = new DecideAppealCommandHandler(_appeals, _posts, _comments, _audit, Notifications(), _caller, _clock);

        var dto = await handler.Handle(new DecideAppealCommand(appeal.Id, AppealDecision.Overturn, "Plate is blurred"), Ct);

        Assert.Equal(AppealStatus.Overturned, dto.Status);
        Assert.Equal(PostStatus.Published, post.Status);
        await _audit.Received(1).RecordAsync(Arg.Is<ModerationAuditEntry>(e => e.Action == ModerationActions.DecideAppeal), Arg.Any<CancellationToken>());
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.To == "owner@example.com"), Arg.Any<CancellationToken>());
    }

    // Sensitive tag by moderators, and the audit log

    [Fact]
    public async Task Moderators_set_the_sensitive_tag_with_a_reason_and_it_is_audited()
    {
        var post = Existing(OtherUserId);
        CallerIsModerator();
        var handler = new SetPostSensitiveCommandHandler(_posts, _views, _audit, _caller, _clock);

        await handler.Handle(new SetPostSensitiveCommand(post.Id, true, "Serious injury visible"), Ct);

        Assert.True(post.IsSensitive);
        await _audit.Received(1).RecordAsync(
            Arg.Is<ModerationAuditEntry>(e => e.Action == ModerationActions.SetPostSensitive && e.Before == "Sensitive=False" && e.After == "Sensitive=True"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_admins_read_the_audit_log()
    {
        CallerIsModerator();
        var records = Substitute.For<IModerationAuditRepository>();
        var handler = new ListAuditLogQueryHandler(records, _caller);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new ListAuditLogQuery(), Ct));

        _caller.HasRole("Admin").Returns(true);
        records.SearchAsync(Arg.Any<AuditSearch>(), 1, 50, Arg.Any<CancellationToken>()).Returns((new List<ModerationAuditRecord>(), 0));
        Assert.Empty((await handler.Handle(new ListAuditLogQuery(Action: ModerationActions.HidePost), Ct)).Items);
        await records.Received(1).SearchAsync(Arg.Is<AuditSearch>(s => s.Action == ModerationActions.HidePost), 1, 50, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_audit_log_records_who_did_it_their_role_and_when()
    {
        var records = Substitute.For<IModerationAuditRepository>();
        CallerIsModerator();
        var log = new PostsModerationAuditLog(records, _caller, _clock);

        await log.RecordAsync(new ModerationAuditEntry("Posts", ModerationActions.HidePost, "Post", "p1", " Shows a face ", "Status=Published", "Status=Hidden"), Ct);

        records.Received(1).Add(Arg.Is<ModerationAuditRecord>(r =>
            r.ActorId == UserId && r.ActorRole == "Moderator" && r.OccurredAt == Now && r.Reason == "Shows a face" && r.After == "Status=Hidden"));
        await records.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // Text safety on posts and comments

    [Fact]
    public void Posts_and_comments_cant_contain_personal_details()
    {
        var create = new CreatePostCommandValidator(_clock);

        var plate = create.Validate(Command(Guid.NewGuid()) with { Title = "Close pass by AB12 CDE" });
        var place = create.Validate(Command(Guid.NewGuid()) with { PlaceName = "outside 12 Acacia Avenue" });
        var comment = new AddCommentCommandValidator().Validate(new AddCommentCommand(Guid.NewGuid(), "his number is 07700 900123"));

        Assert.Contains(plate.Errors, e => e.PropertyName == "Title" && e.ErrorMessage == TextSafety.NumberPlateMessage);
        Assert.Contains(place.Errors, e => e.PropertyName == "PlaceName" && e.ErrorMessage == TextSafety.StreetAddressMessage);
        Assert.Contains(comment.Errors, e => e.PropertyName == "Body" && e.ErrorMessage == TextSafety.PhoneNumberMessage);
        Assert.True(create.Validate(Command(Guid.NewGuid()) with { Title = "Undertaking on the M62 J26", PlaceName = "A road near Bury" }).IsValid);
    }

    // Helpers

    private void SignedInAs(Guid userId)
    {
        _caller.IdentityId.Returns(userId.ToString());
        _caller.EmailVerified.Returns(true);
        _caller.AgeVerified.Returns(true);
    }

    private void SignedOut()
    {
        _caller.IdentityId.Returns((string?)null);
        _caller.EmailVerified.Returns(false);
    }

    private void CallerIsModerator()
    {
        _caller.HasScope(PostsModule.ManageScope).Returns(true);
        _caller.HasRole("Moderator").Returns(true);
        _caller.UsedMfa.Returns(true);
    }

    private void NewAccount() =>
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ProfileSummary> { [UserId] = new(UserId, "roadhog", "Road Hog", null, Now.AddHours(-2)) });

    private void Screening(double score, ScreeningVerdict verdict, string label) =>
        _screener.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ScreeningResult(score, [label], verdict));

    private Guid ReadyMedia()
    {
        var id = Guid.NewGuid();
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MediaSummary>
            {
                new(id, UserId, MediaType.Image, MediaStatus.Ready, new Uri("https://media.example.com/a.webp"), null, null, 100, 100),
            });
        return id;
    }

    private Comment ExistingComment(Post post, Guid authorId)
    {
        var comment = Comment.Create(post.Id, authorId, "Shocking driving", Now);
        _comments.GetAsync(post.Id, comment.Id, Arg.Any<CancellationToken>()).Returns(comment);
        return comment;
    }

    private ReactToCommentCommandHandler CommentReactHandler() =>
        new(_posts, _comments, _commentReactions, _blocks, _caller, _clock);

    private Post Existing(Guid authorId, bool sensitive = false)
    {
        var post = Post.Create(authorId, Details(), [Guid.NewGuid()], Now, sensitive);
        _posts.GetAsync(post.Id, Arg.Any<CancellationToken>()).Returns(post);
        return post;
    }

    private static CreatePostCommand Command(Guid mediaId)
    {
        var d = Details();
        return new CreatePostCommand(d.Title, d.Description, d.Category, d.IncidentAt, d.Latitude, d.Longitude, d.PlaceName, [mediaId]);
    }

    private static PublicReportCommand PublicReport(string target, ReportReason reason = ReportReason.InThisClip) =>
        new(target, null, reason, "That's my car", "Sam", "sam@example.com", true, "XXXX.DUMMY.TOKEN.XXXX", "203.0.113.9");

    private GetPostQueryHandler GetHandler() => new(_posts, _views, _blocks, _caller);

    private ListPostsQueryHandler FeedHandler() => new(_posts, _profiles, _blocks, _views, _caller);

    private ListCommentsQueryHandler CommentsHandler() => new(_posts, _comments, Threads(), _blocks, _caller);

    private CommentThreads Threads() => new(_comments, _commentReactions, _profiles);

    private CreatePostCommandHandler CreateHandler() => new(_posts, _profiles, _media, _screener, Limits(), _views, _caller, _clock);

    private AddCommentCommandHandler AddCommentHandler() =>
        new(_posts, _comments, _profiles, _blocks, _screener, Limits(), _caller, _clock);

    private PublicReportCommandHandler PublicHandler() => new(_posts, _comments, _reports, _captcha, Notifications(), _clock);

    private ResolveReportCommandHandler ResolveHandler() =>
        new(_reports, _posts, _comments, _profileModeration, _audit, Notifications(), _caller, _clock);

    private NewAccountLimits Limits(SafetyOptions? options = null) =>
        new(_profiles, _posts, _comments, Options.Create(options ?? new SafetyOptions()), _clock);

    private ModerationNotifications Notifications() =>
        new(_email, Options.Create(new EmailOptions()), _clock, NullLogger<ModerationNotifications>.Instance);
}
