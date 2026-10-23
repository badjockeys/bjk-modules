using BJK.Modules.Media.Contracts;
using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Profiles.UseCases.Follows;
using BJK.Modules.Shared.Security;
using NSubstitute;
using Xunit;
using static BJK.Modules.Profiles.Tests.TestData;

namespace BJK.Modules.Profiles.Tests.UseCases;

/// <summary>Tag suggestions as you type (owner, 23 Oct 2026: "like Facebook and Instagram").</summary>
public sealed class TagSuggestionTests
{
    private readonly IProfileRepository _profiles = Substitute.For<IProfileRepository>();
    private readonly IMediaDirectory _media = Substitute.For<IMediaDirectory>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly Profile _aaron = Person("aaron_g", "Aaron Greenhalgh");
    private readonly Profile _aaliyah = Person("aaliyah", "Aaliyah Khan");
    private readonly Profile _aadam = Person("aadam", "Aadam Shah");
    private readonly Profile _stranger = Person("aa_driver", "Aa Driver");

    public TagSuggestionTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _profiles.GetBlockedEitherWayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<MediaSummary>());
        _profiles.SearchConnectionsAsync(UserId, "Aa", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new TagCandidate(_aaron, FollowsYou: true, YouFollow: true),
            new TagCandidate(_aaliyah, FollowsYou: true, YouFollow: false),
            new TagCandidate(_aadam, FollowsYou: false, YouFollow: true),
        ]);
        _profiles.SearchAdultsAsync("Aa", Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([_stranger]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TagSuggestionsQueryHandler Handler() => new(_profiles, _media, _caller);

    [Fact]
    public async Task People_you_are_connected_to_come_first_then_other_members()
    {
        var people = await Handler().Handle(new TagSuggestionsQuery("@Aa"), Ct);

        Assert.Equal(["aaron_g", "aaliyah", "aadam", "aa_driver"], people.Select(p => p.Handle));
        Assert.Equal(
            [TagConnection.Mutual, TagConnection.FollowsYou, TagConnection.YouFollow, TagConnection.None],
            people.Select(p => p.Connection));
        await _profiles.Received(1).SearchAdultsAsync(
            "Aa",
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(UserId) && ids.Contains(_aaron.Id) && ids.Contains(_aadam.Id)),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task People_blocked_either_way_are_never_suggested()
    {
        _profiles.GetBlockedEitherWayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Guid> { _aaliyah.Id, _stranger.Id });

        var people = await Handler().Handle(new TagSuggestionsQuery("Aa"), Ct);

        Assert.Equal(["aaron_g", "aadam"], people.Select(p => p.Handle));
    }

    [Fact]
    public async Task A_full_list_of_connections_needs_no_one_else()
    {
        var people = await Handler().Handle(new TagSuggestionsQuery("Aa", Limit: 2), Ct);

        Assert.Equal(["aaron_g", "aaliyah"], people.Select(p => p.Handle));
        await _profiles.DidNotReceiveWithAnyArgs().SearchAdultsAsync(default!, default!, default, Ct);
    }

    [Fact]
    public async Task Under_18s_only_get_people_they_are_connected_to()
    {
        _caller.IsMinor.Returns(true);

        var people = await Handler().Handle(new TagSuggestionsQuery("Aa"), Ct);

        Assert.DoesNotContain(people, p => p.Connection == TagConnection.None);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("  @Aa ", "Aa")]
    [InlineData("Aa%_[", "Aa_")]
    [InlineData("O'Brien", "O'Brien")]
    public void What_you_typed_is_cleaned_before_searching(string typed, string searched) =>
        Assert.Equal(searched, TagSuggestionsQueryHandler.Clean(typed));

    [Fact]
    public async Task Nothing_typed_suggests_no_one()
    {
        Assert.Empty(await Handler().Handle(new TagSuggestionsQuery("@"), Ct));
        await _profiles.DidNotReceiveWithAnyArgs().SearchConnectionsAsync(default, default!, default, Ct);
    }

    private static Profile Person(string handle, string name) => Profile.Create(Guid.NewGuid(), handle, name, null, null, Now);
}
