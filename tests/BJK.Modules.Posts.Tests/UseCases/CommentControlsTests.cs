using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Posts.UseCases.Moderation;
using BJK.Modules.Posts.UseCases.Reports;
using BJK.Modules.Posts.UseCases.Safety;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Safety;
using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

/// <summary>Comment edit history, the post author's comment controls, and report emails (UI overhaul step 3).</summary>
public sealed class CommentControlsTests
{
    private static readonly Guid Sam = Guid.Parse("6b1c2d3e-0000-4000-8000-0000000000c1");

    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly ICommentRepository _comments = Substitute.For<ICommentRepository>();
    private readonly ICommentReactionRepository _reactions = Substitute.For<ICommentReactionRepository>();
    private readonly IReportRepository _reports = Substitute.For<IReportRepository>();
    private readonly IProfileDirectory _profiles = Substitute.For<IProfileDirectory>();
    private readonly IBlockDirectory _blocks = Substitute.For<IBlockDirectory>();
    private readonly IContentScreener _screener = Substitute.For<IContentScreener>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);

    public CommentControlsTests()
    {
        SignedInAs(UserId);
        _caller.EmailVerified.Returns(true);
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ProfileSummary>
            {
                [UserId] = new(UserId, "roadhog", "Road Hog", null),
                [OtherUserId] = new(OtherUserId, "dashcam_dave", "Dashcam Dave", null),
                [Sam] = new(Sam, "sam_j", "Sam J", null),
            });
        _blocks.GetBlockedIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _reactions.GetStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, CommentReactionStats>());
        _comments.CountRepliesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _comments.ListRepliesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Comment>());
        _screener.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ScreeningResult.Clean);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Editing_keeps_the_earlier_text_and_marks_the_comment_edited()
    {
        var comment = Comment.Create(Guid.NewGuid(), UserId, "Red Astra, no indicator.", Now);

        var earlier = comment.Edit("Red Corsa, no indicator.", Now.AddMinutes(3));

        Assert.NotNull(earlier);
        Assert.Equal("Red Astra, no indicator.", earlier.Body);
        Assert.Equal(Now, earlier.WrittenAt);
        Assert.Equal(Now.AddMinutes(3), earlier.ReplacedAt);
        Assert.Equal(Now.AddMinutes(3), comment.EditedAt);
        Assert.Null(comment.Edit("  Red Corsa, no indicator. ", Now.AddMinutes(4)));
        Assert.Equal(Now.AddMinutes(3), comment.EditedAt);
    }

    [Fact]
    public async Task An_edit_is_saved_with_its_history()
    {
        var post = Existing(OtherUserId);
        var comment = Comment.Create(post.Id, UserId, "Red Astra.", Now);
        _comments.GetAsync(post.Id, comment.Id, Arg.Any<CancellationToken>()).Returns(comment);
        var handler = new EditCommentCommandHandler(_posts, _comments, _profiles, _screener, _caller, _clock);

        var edited = await handler.Handle(new EditCommentCommand(post.Id, comment.Id, "Red Corsa."), Ct);

        Assert.Equal(Now, edited.EditedAt);
        _comments.Received(1).AddEdit(Arg.Is<CommentEdit>(e => e.Body == "Red Astra." && e.CommentId == comment.Id));
    }

    [Fact]
    public async Task Anyone_can_see_a_comments_earlier_versions_newest_first()
    {
        var post = Existing(OtherUserId);
        var comment = Comment.Create(post.Id, Sam, "First go.", Now.AddHours(-2));
        var first = comment.Edit("Second go.", Now.AddHours(-1))!;
        var second = comment.Edit("Third go.", Now)!;
        _comments.GetAsync(post.Id, comment.Id, Arg.Any<CancellationToken>()).Returns(comment);
        _comments.ListEditsAsync(comment.Id, Arg.Any<CancellationToken>()).Returns(new List<CommentEdit> { second, first });

        var history = await new GetCommentHistoryQueryHandler(_posts, _comments, _blocks, _caller)
            .Handle(new GetCommentHistoryQuery(post.Id, comment.Id), Ct);

        Assert.Equal(new[] { "Third go.", "Second go.", "First go." }, history.Select(v => v.Body));
        Assert.True(history[0].IsCurrent);
        Assert.Equal(Now.AddHours(-2), history[2].WrittenAt);
    }

    [Fact]
    public async Task A_comment_hidden_by_the_post_author_is_a_placeholder_for_others_with_its_replies_hidden()
    {
        var post = Existing(OtherUserId);
        var comment = Comment.Create(post.Id, Sam, "Learn to drive.", Now);
        var reply = Comment.CreateReply(comment, UserId, "Calm down.", Now.AddMinutes(1));
        comment.HideByPostAuthor(Now);
        _comments.CountRepliesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [comment.Id] = 1 });
        _comments.ListRepliesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Comment> { reply });
        var threads = new CommentThreads(_comments, _reactions, _profiles);

        var others = Assert.Single(await threads.BuildTopLevelAsync([comment], UserId, post.AuthorId, [], Ct));
        var commenter = Assert.Single(await threads.BuildTopLevelAsync([comment], Sam, post.AuthorId, [], Ct));
        var postAuthor = Assert.Single(await threads.BuildTopLevelAsync([comment], OtherUserId, post.AuthorId, [], Ct));

        Assert.True(others.IsHiddenByPostAuthor);
        Assert.Null(others.Author);
        Assert.Equal(string.Empty, others.Body);
        Assert.Equal(0, others.ReplyCount);
        Assert.Empty(others.Replies!);
        Assert.True(commenter.IsHiddenByPostAuthor);
        Assert.Equal("Learn to drive.", commenter.Body);
        Assert.Equal("Learn to drive.", postAuthor.Body);
        Assert.Single(postAuthor.Replies!);
    }

    [Fact]
    public async Task Only_the_post_author_can_hide_comments_on_it()
    {
        var post = Existing(OtherUserId);
        var comment = Comment.Create(post.Id, Sam, "Learn to drive.", Now);
        _comments.GetAsync(post.Id, comment.Id, Arg.Any<CancellationToken>()).Returns(comment);
        var handler = new SetCommentHiddenByPostAuthorCommandHandler(_posts, _comments, _profiles, _caller, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => handler.Handle(new SetCommentHiddenByPostAuthorCommand(post.Id, comment.Id, true), Ct));

        SignedInAs(OtherUserId);
        var hidden = await handler.Handle(new SetCommentHiddenByPostAuthorCommand(post.Id, comment.Id, true), Ct);
        Assert.True(hidden.IsHiddenByPostAuthor);
        Assert.True(comment.IsHiddenByPostAuthor);

        await handler.Handle(new SetCommentHiddenByPostAuthorCommand(post.Id, comment.Id, false), Ct);
        Assert.False(comment.IsHiddenByPostAuthor);
    }

    [Fact]
    public async Task Nobody_else_can_reply_to_a_comment_the_post_author_hid()
    {
        var post = Existing(OtherUserId);
        var comment = Comment.Create(post.Id, Sam, "Learn to drive.", Now);
        comment.HideByPostAuthor(Now);
        _comments.GetAsync(post.Id, comment.Id, Arg.Any<CancellationToken>()).Returns(comment);

        await Assert.ThrowsAsync<NotFoundException>(
            () => AddComment().Handle(new AddCommentCommand(post.Id, "Agreed.", comment.Id), Ct));

        SignedInAs(Sam);
        await Assert.ThrowsAsync<ForbiddenException>(
            () => AddComment().Handle(new AddCommentCommand(post.Id, "Fair enough.", comment.Id), Ct));
    }

    [Fact]
    public async Task With_comments_off_only_the_author_can_comment()
    {
        var post = Existing(OtherUserId);
        post.SetCommentSetting(CommentSetting.Off, Now);

        var refused = await Assert.ThrowsAsync<ForbiddenException>(
            () => AddComment().Handle(new AddCommentCommand(post.Id, "Shocking."), Ct));
        Assert.Contains("turned comments off", refused.Message);
        _comments.DidNotReceive().Add(Arg.Any<Comment>());
    }

    [Fact]
    public async Task Only_the_author_changes_who_can_comment()
    {
        var post = Existing(OtherUserId);
        var handler = new SetCommentSettingCommandHandler(_posts, _caller, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => handler.Handle(new SetCommentSettingCommand(post.Id, CommentSetting.Off), Ct));

        SignedInAs(OtherUserId);
        await handler.Handle(new SetCommentSettingCommand(post.Id, CommentSetting.Off), Ct);
        Assert.Equal(CommentSetting.Off, post.CommentSetting);
    }

    [Fact]
    public void Impersonation_and_under_13_are_for_accounts_only()
    {
        Assert.False(new ReportPostCommandValidator().Validate(new ReportPostCommand(Guid.NewGuid(), ReportReason.Impersonation, null)).IsValid);
        Assert.False(new ReportCommentCommandValidator().Validate(new ReportCommentCommand(Guid.NewGuid(), Guid.NewGuid(), ReportReason.UnderThirteen, null)).IsValid);
        Assert.True(new ReportProfileCommandValidator().Validate(new ReportProfileCommand("sam_j", ReportReason.UnderThirteen, null)).IsValid);
        Assert.True(new ReportPostCommandValidator().Validate(new ReportPostCommand(Guid.NewGuid(), ReportReason.Spam, null)).IsValid);
    }

    [Fact]
    public async Task Every_report_is_emailed_to_the_support_inbox_with_a_link()
    {
        _profiles.FindByHandleAsync("sam_j", Arg.Any<CancellationToken>()).Returns(new ProfileSummary(Sam, "sam_j", "Sam J", null));
        var notifications = new ModerationNotifications(
            _email,
            Options.Create(new EmailOptions()),
            Options.Create(new PostsOptions { AppUrl = new Uri("https://badjockeys.com") }),
            _clock,
            NullLogger<ModerationNotifications>.Instance);
        var handler = new ReportProfileCommandHandler(_profiles, _reports, notifications, _caller, _clock);

        await handler.Handle(new ReportProfileCommand("sam_j", ReportReason.Impersonation, "Pretending to be the council."), Ct);

        await _email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m =>
                m.To == "support@badjockeys.com"
                && m.Subject.Contains("the account @sam_j")
                && m.Subject.Contains("impersonation")
                && (m.HtmlBody ?? string.Empty).Contains("https://badjockeys.com/u/sam_j")
                && (m.HtmlBody ?? string.Empty).Contains("Pretending to be the council.")),
            Arg.Any<CancellationToken>());
    }

    private AddCommentCommandHandler AddComment() =>
        new(
            _posts,
            _comments,
            _profiles,
            _blocks,
            _screener,
            new NewAccountLimits(_profiles, _posts, _comments, Options.Create(new SafetyOptions()), _clock),
            _caller,
            _clock);

    private void SignedInAs(Guid userId) => _caller.IdentityId.Returns(userId.ToString());

    private Post Existing(Guid authorId)
    {
        var post = NewPost(authorId);
        _posts.GetAsync(post.Id, Arg.Any<CancellationToken>()).Returns(post);
        return post;
    }
}
