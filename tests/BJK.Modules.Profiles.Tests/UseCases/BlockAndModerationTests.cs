using BJK.Modules.Media.Contracts;
using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Profiles.UseCases.Blocks;
using BJK.Modules.Profiles.UseCases.Create;
using BJK.Modules.Profiles.UseCases.Moderation;
using BJK.Modules.Profiles.UseCases.Update;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Safety;
using BJK.Modules.Shared.Security;
using BJK.Modules.Shared.Users;
using FluentValidation;
using NSubstitute;
using Xunit;
using static BJK.Modules.Profiles.Tests.TestData;

namespace BJK.Modules.Profiles.Tests.UseCases;

public sealed class BlockAndModerationTests
{
    private readonly IProfileRepository _profiles = Substitute.For<IProfileRepository>();
    private readonly IMediaDirectory _media = Substitute.For<IMediaDirectory>();
    private readonly IModerationAuditLog _audit = Substitute.For<IModerationAuditLog>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly Profile _other;

    public BlockAndModerationTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _other = Profile.Create(OtherUserId, "speedy", "Speedy", "Drives fast", "Bury", Now);
        _profiles.GetByHandleAsync("speedy", Arg.Any<CancellationToken>()).Returns(_other);
        _profiles.GetByIdAsync(OtherUserId, Arg.Any<CancellationToken>()).Returns(_other);
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<MediaSummary>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Blocking_someone_by_handle_records_the_block()
    {
        var handler = new BlockUserCommandHandler(_profiles, _media, _caller, _clock);

        var dto = await handler.Handle(new BlockUserCommand("@Speedy"), Ct);

        Assert.Equal(OtherUserId, dto.UserId);
        _profiles.Received(1).AddBlock(Arg.Is<Block>(b => b.BlockerId == UserId && b.BlockedId == OtherUserId));
        await _profiles.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Blocking_twice_does_nothing_more()
    {
        _profiles.GetBlockAsync(UserId, OtherUserId, Arg.Any<CancellationToken>()).Returns(Block.Create(UserId, OtherUserId, Now));
        var handler = new BlockUserCommandHandler(_profiles, _media, _caller, _clock);

        await handler.Handle(new BlockUserCommand(OtherUserId.ToString()), Ct);

        _profiles.DidNotReceive().AddBlock(Arg.Any<Block>());
    }

    [Fact]
    public async Task You_cant_block_yourself_or_someone_who_doesnt_exist()
    {
        var me = Profile.Create(UserId, "roadhog", "Road Hog", null, null, Now);
        _profiles.GetByHandleAsync("roadhog", Arg.Any<CancellationToken>()).Returns(me);
        var handler = new BlockUserCommandHandler(_profiles, _media, _caller, _clock);

        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new BlockUserCommand("roadhog"), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new BlockUserCommand("nobody_here"), Ct));
        Assert.Throws<ArgumentException>(() => Block.Create(UserId, UserId, Now));
    }

    [Fact]
    public async Task Unblocking_removes_the_block()
    {
        var block = Block.Create(UserId, OtherUserId, Now);
        _profiles.GetBlockAsync(UserId, OtherUserId, Arg.Any<CancellationToken>()).Returns(block);

        await new UnblockUserCommandHandler(_profiles, _caller).Handle(new UnblockUserCommand("speedy"), Ct);

        _profiles.Received(1).RemoveBlock(block);
    }

    [Fact]
    public async Task My_blocks_list_shows_who_I_blocked()
    {
        _profiles.ListBlocksAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Block> { Block.Create(UserId, OtherUserId, Now) });
        _profiles.GetManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<Profile> { _other });

        var blocks = await new ListMyBlocksQueryHandler(_profiles, _media, _caller).Handle(new ListMyBlocksQuery(), Ct);

        Assert.Equal("speedy", Assert.Single(blocks).Handle);
    }

    [Fact]
    public async Task Deleting_an_account_removes_its_blocks_both_ways()
    {
        var handler = new ProfilesUserLifecycleHandler(_profiles, _clock);

        await handler.HandleAsync(new UserLifecycleEvent(Guid.NewGuid(), OtherUserId, UserLifecycleEventType.Deleted, Now), Ct);

        await _profiles.Received(1).DeleteBlocksForUserAsync(OtherUserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Moderators_reset_a_profile_with_a_reason_and_it_is_audited()
    {
        var avatar = Guid.NewGuid();
        _other.SetAvatar(avatar, Now);
        _caller.HasScope(ProfilesModule.ManageScope).Returns(true);
        _caller.HasRole("Moderator").Returns(true);
        _caller.UsedMfa.Returns(true);
        var handler = new ResetProfileCommandHandler(_profiles, _media, _audit, _caller, _clock);

        var dto = await handler.Handle(new ResetProfileCommand("speedy", "Abusive bio"), Ct);

        Assert.Equal("speedy", dto.DisplayName);
        Assert.Null(dto.Bio);
        Assert.Null(dto.Location);
        await _media.Received(1).DeleteAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == avatar), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync(
            Arg.Is<ModerationAuditEntry>(e => e.Module == "Profiles" && e.Action == ModerationActions.ResetProfile && e.Reason == "Abusive bio"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resetting_profiles_needs_moderator_rights_and_a_reason()
    {
        var handler = new ResetProfileCommandHandler(_profiles, _media, _audit, _caller, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new ResetProfileCommand("speedy", "x"), Ct));
        Assert.False(new ResetProfileCommandValidator().Validate(new ResetProfileCommand("speedy", " ")).IsValid);
    }

    [Fact]
    public async Task Profile_moderation_contract_resets_and_deletes_the_old_avatar()
    {
        var avatar = Guid.NewGuid();
        _other.SetAvatar(avatar, Now);
        var moderation = new ProfileModeration(_profiles, _media, _clock);

        Assert.True(await moderation.ResetPublicDetailsAsync(OtherUserId, Ct));
        Assert.False(await moderation.ResetPublicDetailsAsync(Guid.NewGuid(), Ct));
        Assert.Null(_other.AvatarMediaId);
        await _media.Received(1).DeleteAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Profiles_cant_contain_personal_details()
    {
        var create = new CreateProfileCommandValidator();
        var update = new UpdateProfileCommandValidator();

        var bio = create.Validate(new CreateProfileCommand("roadhog", "Road Hog", "Find me on instagram.com/roadhog", null));
        var name = update.Validate(new UpdateProfileCommand("Call 07700 900123", null, null));

        Assert.Contains(bio.Errors, e => e.PropertyName == "Bio" && e.ErrorMessage == TextSafety.SocialHandleMessage);
        Assert.Contains(name.Errors, e => e.PropertyName == "DisplayName" && e.ErrorMessage == TextSafety.PhoneNumberMessage);
        Assert.True(update.Validate(new UpdateProfileCommand("Road Hog", "Dashcam fan, M62 commuter", "Bury")).IsValid);
    }
}
