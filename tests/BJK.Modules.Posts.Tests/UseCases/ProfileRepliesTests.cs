using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Security;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class ProfileRepliesTests
{
    private static readonly Guid Sam = Guid.Parse("6b1c2d3e-0000-4000-8000-0000000000b1");

    private readonly ICommentRepository _comments = Substitute.For<ICommentRepository>();
    private readonly ICommentReactionRepository _reactions = Substitute.For<ICommentReactionRepository>();
    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly IProfileDirectory _profiles = Substitute.For<IProfileDirectory>();
    private readonly IBlockDirectory _blocks = Substitute.For<IBlockDirectory>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();

    public ProfileRepliesTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _profiles.FindByHandleAsync("sam_j", Arg.Any<CancellationToken>()).Returns(new ProfileSummary(Sam, "sam_j", "Sam J", null));
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ProfileSummary>
            {
                [Sam] = new(Sam, "sam_j", "Sam J", null),
                [OtherUserId] = new(OtherUserId, "dashcam_dave", "Dashcam Dave", null),
            });
        _blocks.GetBlockedEitherWayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _blocks.GetBlockedIdsAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _reactions.GetStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, CommentReactionStats>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Replies_come_with_their_post_and_who_they_answer()
    {
        var post = NewPost(OtherUserId);
        _posts.GetAsync(post.Id, Arg.Any<CancellationToken>()).Returns(post);
        var daves = Comment.Create(post.Id, OtherUserId, "Terrible overtake.", Now);
        var samsReply = Comment.CreateReply(daves, Sam, "Agreed, saw it too.", Now.AddMinutes(5));
        var samsComment = Comment.Create(post.Id, Sam, "Same car every morning.", Now.AddMinutes(1));
        _comments.FindAsync(daves.Id, Arg.Any<CancellationToken>()).Returns(daves);
        _comments.ListByAuthorAsync(Sam, Arg.Any<IReadOnlyCollection<Guid>>(), false, 1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<Comment> { samsReply, samsComment }, 2));

        var page = await Handler().Handle(new ListProfileRepliesQuery("@sam_j"), Ct);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal("Agreed, saw it too.", page.Items[0].Comment.Body);
        Assert.Equal("dashcam_dave", page.Items[0].ReplyingTo);
        Assert.Equal(post.Title, page.Items[0].Post.Title);
        Assert.Equal("dashcam_dave", page.Items[0].Post.Author.Handle);
        Assert.Null(page.Items[1].ReplyingTo);
    }

    [Fact]
    public async Task People_who_have_blocked_each_other_see_nothing()
    {
        _blocks.GetBlockedEitherWayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Guid> { Sam });

        var page = await Handler().Handle(new ListProfileRepliesQuery("sam_j"), Ct);

        Assert.Empty(page.Items);
        await _comments.DidNotReceive().ListByAuthorAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task People_see_their_own_held_and_hidden_comments()
    {
        _caller.IdentityId.Returns(Sam.ToString());
        _blocks.GetBlockedEitherWayAsync(Sam, Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _blocks.GetBlockedIdsAsync(Sam, Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _comments.ListByAuthorAsync(Sam, Arg.Any<IReadOnlyCollection<Guid>>(), true, 1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<Comment>(), 0));

        await Handler().Handle(new ListProfileRepliesQuery("sam_j"), Ct);

        await _comments.Received(1).ListByAuthorAsync(Sam, Arg.Any<IReadOnlyCollection<Guid>>(), true, 1, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_handle_has_no_replies()
    {
        var page = await Handler().Handle(new ListProfileRepliesQuery("nobody"), Ct);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    private ListProfileRepliesQueryHandler Handler() =>
        new(_comments, _posts, new CommentThreads(_comments, _reactions, _profiles), _profiles, _blocks, _caller);
}
