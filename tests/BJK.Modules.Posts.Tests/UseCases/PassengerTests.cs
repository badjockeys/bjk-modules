using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Passengers;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class PassengerTests
{
    private static readonly Guid Sam = Guid.Parse("6b1c2d3e-0000-4000-8000-0000000000a1");
    private static readonly Guid Teen = Guid.Parse("6b1c2d3e-0000-4000-8000-0000000000a2");
    private static readonly Guid Blocker = Guid.Parse("6b1c2d3e-0000-4000-8000-0000000000a3");

    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly IProfileDirectory _profiles = Substitute.For<IProfileDirectory>();
    private readonly IBlockDirectory _blocks = Substitute.For<IBlockDirectory>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly List<PostPassenger> _tags = [];
    private readonly PassengerTagging _tagging;

    private static readonly IncidentCategory[] Driving = [IncidentCategory.Tailgating];

    public PassengerTests()
    {
        Person(UserId, "roadhog", adult: true);
        Person(Sam, "sam_j", adult: true);
        Person(Teen, "learner", adult: false);
        Person(Blocker, "grump", adult: true);
        _blocks.GetBlockedEitherWayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Guid> { Blocker });
        _posts.GetPassengersAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => _tags.ToList());
        _posts.When(p => p.AddPassenger(Arg.Any<PostPassenger>())).Do(c => _tags.Add(c.Arg<PostPassenger>()));
        _posts.When(p => p.RemovePassenger(Arg.Any<PostPassenger>())).Do(c => _tags.Remove(c.Arg<PostPassenger>()));
        _posts.GetStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PostStats>());
        _tagging = new PassengerTagging(_posts, _profiles, _blocks, _clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Adults_are_tagged_by_handle_and_wait_to_accept()
    {
        var ids = await _tagging.ResolveAsync(UserId, ["@Sam_J", "sam_j"], Driving, Ct);
        var post = NewPost(UserId);
        var added = await _tagging.ApplyAsync(post, ids, Ct);

        Assert.Equal(new[] { Sam }, added);
        var tag = Assert.Single(_tags);
        Assert.Equal(PassengerTagStatus.Pending, tag.Status);
    }

    [Theory]
    [InlineData("learner", "You can't tag @learner.")]
    [InlineData("grump", "You can't tag @grump.")]
    [InlineData("roadhog", "You can't tag yourself.")]
    [InlineData("nobody", "We couldn't find @nobody.")]
    public async Task Some_people_cant_be_tagged_and_the_reason_isnt_given(string handle, string message)
    {
        var error = await Assert.ThrowsAsync<ValidationException>(
            () => _tagging.ResolveAsync(UserId, [handle], Driving, Ct));

        var failure = Assert.Single(error.Errors);
        Assert.Equal("Passengers", failure.PropertyName);
        Assert.Equal(message, failure.ErrorMessage);
    }

    [Fact]
    public async Task Bad_parking_has_no_passengers()
    {
        var error = await Assert.ThrowsAsync<ValidationException>(
            () => _tagging.ResolveAsync(UserId, ["sam_j"], [IncidentCategory.BadParking], Ct));

        Assert.Equal(PassengerTagging.OnlyDrivingMessage, Assert.Single(error.Errors).ErrorMessage);

        // Bad parking alongside a driving type is fine.
        Assert.Single(await _tagging.ResolveAsync(UserId, ["sam_j"], [IncidentCategory.BadParking, IncidentCategory.RoadRage], Ct));
    }

    [Fact]
    public async Task Up_to_four_passengers()
    {
        var error = await Assert.ThrowsAsync<ValidationException>(
            () => _tagging.ResolveAsync(UserId, ["a1", "a2", "a3", "a4", "a5"], Driving, Ct));

        Assert.Equal("You can tag up to 4 passengers.", Assert.Single(error.Errors).ErrorMessage);
    }

    [Fact]
    public async Task Someone_who_declined_cant_be_tagged_on_that_post_again()
    {
        var post = NewPost(UserId);
        await _tagging.ApplyAsync(post, [Sam], Ct);
        _tags[0].Respond(accept: false, Now);

        await Assert.ThrowsAsync<ValidationException>(() => _tagging.ApplyAsync(post, [Sam], Ct));

        // Removing them from the list keeps the declined record.
        await _tagging.ApplyAsync(post, [], Ct);
        Assert.Equal(PassengerTagStatus.Declined, Assert.Single(_tags).Status);
    }

    [Fact]
    public async Task Names_show_once_accepted_and_the_author_sees_pending_ones()
    {
        var post = NewPost(UserId);
        _posts.GetAsync(post.Id, Arg.Any<CancellationToken>()).Returns(post);
        await _tagging.ApplyAsync(post, [Sam], Ct);
        var views = new PostViews(_posts, _profiles, Substitute.For<BJK.Modules.Media.Contracts.IMediaDirectory>(), _caller);

        Assert.Empty((await views.BuildAsync(post, viewerId: null, Ct)).Passengers!);
        Assert.Equal(PassengerTagStatus.Pending, Assert.Single((await views.BuildAsync(post, UserId, Ct)).Passengers!).Status);

        _caller.IdentityId.Returns(Sam.ToString());
        var dto = await new RespondToTagCommandHandler(_posts, views, _caller, _clock)
            .Handle(new RespondToTagCommand(post.Id, Accept: true), Ct);
        Assert.Equal("sam_j", Assert.Single(dto.Passengers!).Person.Handle);

        var seen = Assert.Single((await views.BuildAsync(post, viewerId: null, Ct)).Passengers!);
        Assert.Equal(PassengerTagStatus.Accepted, seen.Status);
    }

    [Fact]
    public async Task You_can_only_answer_your_own_tag()
    {
        var post = NewPost(UserId);
        _posts.GetAsync(post.Id, Arg.Any<CancellationToken>()).Returns(post);
        _caller.IdentityId.Returns(Teen.ToString());
        var views = new PostViews(_posts, _profiles, Substitute.For<BJK.Modules.Media.Contracts.IMediaDirectory>(), _caller);

        await Assert.ThrowsAsync<NotFoundException>(
            () => new RespondToTagCommandHandler(_posts, views, _caller, _clock).Handle(new RespondToTagCommand(post.Id, true), Ct));
    }

    private void Person(Guid id, string handle, bool adult)
    {
        var summary = new ProfileSummary(id, handle, handle, null, Now, IsAdult: adult);
        _profiles.FindByHandleAsync(Arg.Is<string>(h => h.TrimStart('@').Equals(handle, StringComparison.OrdinalIgnoreCase)), Arg.Any<CancellationToken>())
            .Returns(summary);
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<IReadOnlyCollection<Guid>>()
                .Where(_all.ContainsKey)
                .ToDictionary(x => x, x => _all[x]));
        _all[id] = summary;
    }

    private readonly Dictionary<Guid, ProfileSummary> _all = [];
}
