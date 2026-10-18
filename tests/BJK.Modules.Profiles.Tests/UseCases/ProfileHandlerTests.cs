using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Profiles.UseCases.Create;
using BJK.Modules.Profiles.UseCases.Get;
using BJK.Modules.Profiles.UseCases.Update;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using NSubstitute;
using Xunit;
using static BJK.Modules.Profiles.Tests.TestData;

namespace BJK.Modules.Profiles.Tests.UseCases;

public sealed class ProfileHandlerTests
{
    private readonly IProfileRepository _profiles = Substitute.For<IProfileRepository>();
    private readonly IMediaDirectory _media = Substitute.For<IMediaDirectory>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);

    public ProfileHandlerTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<MediaSummary>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_saves_a_profile_with_the_callers_id()
    {
        var handler = new CreateProfileCommandHandler(_profiles, _caller, _clock);

        var dto = await handler.Handle(new CreateProfileCommand("@RoadHog", "Road Hog", null, "Bury"), Ct);

        Assert.Equal(UserId, dto.Id);
        Assert.Equal("roadhog", dto.Handle);
        _profiles.Received(1).Add(Arg.Is<Profile>(p => p.Id == UserId && p.Handle == "roadhog"));
        await _profiles.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_needs_a_user_token()
    {
        _caller.IdentityId.Returns((string?)null);
        var handler = new CreateProfileCommandHandler(_profiles, _caller, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => handler.Handle(new CreateProfileCommand("roadhog", "Road Hog", null, null), Ct));
    }

    [Fact]
    public async Task Create_refuses_a_second_profile()
    {
        Existing(UserId, "roadhog");
        var handler = new CreateProfileCommandHandler(_profiles, _caller, _clock);

        await Assert.ThrowsAsync<ValidationException>(
            () => handler.Handle(new CreateProfileCommand("another", "Road Hog", null, null), Ct));
        _profiles.DidNotReceive().Add(Arg.Any<Profile>());
    }

    [Fact]
    public async Task Create_refuses_a_taken_handle()
    {
        Existing(OtherUserId, "roadhog");
        var handler = new CreateProfileCommandHandler(_profiles, _caller, _clock);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => handler.Handle(new CreateProfileCommand("RoadHog", "Road Hog", null, null), Ct));

        Assert.Contains(ex.Errors, e => e.PropertyName == "Handle");
    }

    [Fact]
    public async Task SetAvatar_accepts_own_ready_image_and_deletes_the_old_one()
    {
        var profile = Existing(UserId, "roadhog");
        var oldAvatar = Guid.NewGuid();
        profile.SetAvatar(oldAvatar, Now);
        var newAvatar = Guid.NewGuid();
        MediaIs(newAvatar, UserId, MediaType.Image, MediaStatus.Ready);
        var handler = new SetAvatarCommandHandler(_profiles, _media, _caller, _clock);

        await handler.Handle(new SetAvatarCommand(newAvatar), Ct);

        Assert.Equal(newAvatar, profile.AvatarMediaId);
        await _media.Received(1).DeleteAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == oldAvatar),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, MediaType.Image, MediaStatus.Ready)]
    [InlineData(true, MediaType.Video, MediaStatus.Ready)]
    [InlineData(true, MediaType.Image, MediaStatus.Processing)]
    public async Task SetAvatar_rejects_media_that_isnt_your_ready_photo(bool own, MediaType type, MediaStatus status)
    {
        var profile = Existing(UserId, "roadhog");
        var mediaId = Guid.NewGuid();
        MediaIs(mediaId, own ? UserId : OtherUserId, type, status);
        var handler = new SetAvatarCommandHandler(_profiles, _media, _caller, _clock);

        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new SetAvatarCommand(mediaId), Ct));
        Assert.Null(profile.AvatarMediaId);
    }

    [Fact]
    public async Task GetMine_without_a_profile_is_not_found()
    {
        var handler = new GetMyProfileQueryHandler(_profiles, _media, _caller);

        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new GetMyProfileQuery(), Ct));
    }

    [Fact]
    public async Task CheckHandle_reports_taken_reserved_and_your_own()
    {
        Existing(OtherUserId, "taken");
        Existing(UserId, "mine");
        var handler = new CheckHandleQueryHandler(_profiles, _caller);

        Assert.False((await handler.Handle(new CheckHandleQuery("@Taken"), Ct)).Available);
        Assert.False((await handler.Handle(new CheckHandleQuery("admin"), Ct)).Available);
        Assert.True((await handler.Handle(new CheckHandleQuery("mine"), Ct)).Available);
        Assert.True((await handler.Handle(new CheckHandleQuery("free_one"), Ct)).Available);
    }

    private Profile Existing(Guid userId, string handle)
    {
        var profile = Profile.Create(userId, handle, "Someone", null, null, Now);
        _profiles.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(profile);
        _profiles.GetByHandleAsync(handle, Arg.Any<CancellationToken>()).Returns(profile);
        return profile;
    }

    private void MediaIs(Guid id, Guid owner, MediaType type, MediaStatus status) =>
        _media.GetAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(id)), Arg.Any<CancellationToken>())
            .Returns(new List<MediaSummary> { new(id, owner, type, status, null, null, null, null, null) });
}
