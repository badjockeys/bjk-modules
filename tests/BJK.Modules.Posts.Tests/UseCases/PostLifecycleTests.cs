using BJK.Modules.Media.Contracts;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Get;
using BJK.Modules.Posts.UseCases.Reactions;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using BJK.Modules.Shared.Users;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class PostLifecycleTests
{
    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(UserLifecycleEventType.Deactivated, true)]
    [InlineData(UserLifecycleEventType.Reactivated, false)]
    public async Task Deactivation_hides_and_reactivation_shows_the_users_posts(UserLifecycleEventType type, bool hidden)
    {
        var handler = new PostsUserLifecycleHandler(_posts);

        await handler.HandleAsync(new UserLifecycleEvent(Guid.NewGuid(), UserId, type, Now), Ct);

        await _posts.Received(1).SetAuthorDeactivatedAsync(UserId, hidden, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_the_account_deletes_everything_in_posts()
    {
        var handler = new PostsUserLifecycleHandler(_posts);

        await handler.HandleAsync(new UserLifecycleEvent(Guid.NewGuid(), UserId, UserLifecycleEventType.Deleted, Now), Ct);

        await _posts.Received(1).DeleteAllForUserAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_deactivated_authors_post_looks_missing_and_cant_be_reacted_to()
    {
        var post = NewPost(OtherUserId);
        typeof(Post).GetProperty(nameof(Post.IsAuthorDeactivated))!.SetValue(post, true);
        _posts.GetAsync(post.Id, Arg.Any<CancellationToken>()).Returns(post);
        _caller.IdentityId.Returns(UserId.ToString());
        var views = new PostViews(_posts, Substitute.For<IProfileDirectory>(), Substitute.For<IMediaDirectory>(), _caller);

        await Assert.ThrowsAsync<NotFoundException>(
            () => new GetPostQueryHandler(_posts, views, Substitute.For<IBlockDirectory>(), _caller).Handle(new GetPostQuery(post.Id), Ct));
        await Assert.ThrowsAsync<NotFoundException>(
            () => new ReactToPostCommandHandler(_posts, Substitute.For<IReactionRepository>(), Substitute.For<IBlockDirectory>(), _caller, new FixedTimeProvider(Now))
                .Handle(new ReactToPostCommand(post.Id, ReactionType.Like), Ct));
    }
}
