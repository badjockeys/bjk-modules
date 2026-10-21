using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Profiles.UseCases.Create;
using BJK.Modules.Profiles.UseCases.Moderation;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Security;
using NSubstitute;
using Xunit;
using static BJK.Modules.Profiles.Tests.TestData;

namespace BJK.Modules.Profiles.Tests.UseCases;

/// <summary>Quality badges: Founding member, Trusted reporter, Local regular (engagement plan, Oct 2026).</summary>
public sealed class BadgeTests
{
    private readonly IProfileRepository _profiles = Substitute.For<IProfileRepository>();
    private readonly IModerationAuditLog _audit = Substitute.For<IModerationAuditLog>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly Profile _other = Profile.Create(OtherUserId, "speedy", "Speedy", null, null, Now);

    public BadgeTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _profiles.GetByHandleAsync("speedy", Arg.Any<CancellationToken>()).Returns(_other);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_thousand_members_are_founding_members()
    {
        _profiles.CountBadgeAsync(Badge.FoundingMember, Arg.Any<CancellationToken>()).Returns(ProfileBadge.FoundingMemberLimit - 1);

        var dto = await new CreateProfileCommandHandler(_profiles, _caller, _clock)
            .Handle(new CreateProfileCommand("roadhog", "Road Hog", null, null), Ct);

        Assert.Equal([Badge.FoundingMember], dto.Badges);
        _profiles.Received(1).AddBadge(Arg.Is<ProfileBadge>(b => b.UserId == UserId && b.Badge == Badge.FoundingMember && b.AwardedBy == null));
    }

    [Fact]
    public async Task After_that_nobody_is()
    {
        _profiles.CountBadgeAsync(Badge.FoundingMember, Arg.Any<CancellationToken>()).Returns(ProfileBadge.FoundingMemberLimit);

        var dto = await new CreateProfileCommandHandler(_profiles, _caller, _clock)
            .Handle(new CreateProfileCommand("roadhog", "Road Hog", null, null), Ct);

        Assert.Empty(dto.Badges!);
        _profiles.DidNotReceiveWithAnyArgs().AddBadge(null!);
    }

    [Fact]
    public void Local_regular_is_only_shown_on_adults_profiles()
    {
        Badge[] held = [Badge.LocalRegular, Badge.FoundingMember];

        Assert.Equal([Badge.FoundingMember, Badge.LocalRegular], ProfileBadge.Visible(held, isMinor: false));
        Assert.Equal([Badge.FoundingMember], ProfileBadge.Visible(held, isMinor: true));
        Assert.Equal([Badge.FoundingMember], ProfileBadge.Visible(held, isMinor: null));
    }

    [Fact]
    public async Task Moderators_give_trusted_reporter_with_a_reason_in_the_audit_log()
    {
        ActAsModerator();
        _profiles.ListBadgesAsync(OtherUserId, Arg.Any<CancellationToken>()).Returns(new List<Badge> { Badge.TrustedReporter });

        var badges = await Handler().Handle(new SetBadgeCommand("speedy", Badge.TrustedReporter, true, "Ten clear posts, no upheld reports."), Ct);

        Assert.Equal([Badge.TrustedReporter], badges);
        _profiles.Received(1).AddBadge(Arg.Is<ProfileBadge>(b => b.UserId == OtherUserId && b.AwardedBy == UserId));
        await _audit.Received(1).RecordAsync(
            Arg.Is<ModerationAuditEntry>(e => e.Action == ModerationActions.AwardBadge),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Only_trusted_reporter_can_be_given_by_hand()
    {
        var validator = new SetBadgeCommandValidator();

        Assert.False(validator.Validate(new SetBadgeCommand("speedy", Badge.FoundingMember, true, "Because")).IsValid);
        Assert.True(validator.Validate(new SetBadgeCommand("speedy", Badge.FoundingMember, false, "Given in error")).IsValid);
        Assert.False(validator.Validate(new SetBadgeCommand("speedy", Badge.TrustedReporter, true, "")).IsValid);
    }

    [Fact]
    public async Task Members_cant_give_badges()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            Handler().Handle(new SetBadgeCommand("speedy", Badge.TrustedReporter, true, "Me"), Ct));
    }

    private SetBadgeCommandHandler Handler() => new(_profiles, _audit, _caller, _clock);

    private void ActAsModerator()
    {
        _caller.HasScope(ProfilesModule.ManageScope).Returns(true);
        _caller.HasRole(StaffRoles.Moderator).Returns(true);
        _caller.UsedMfa.Returns(true);
    }
}
