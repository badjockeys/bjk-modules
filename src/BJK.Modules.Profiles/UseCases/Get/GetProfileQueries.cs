using BJK.Modules.Media.Contracts;
using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Profiles.UseCases.Update;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using MediatR;

namespace BJK.Modules.Profiles.UseCases.Get;

/// <summary>The signed-in user's profile. 404 means they haven't created one yet.</summary>
public sealed record GetMyProfileQuery : IRequest<ProfileDto>;

public sealed class GetMyProfileQueryHandler(IProfileRepository profiles, IMediaDirectory media, ICurrentUser caller)
    : IRequestHandler<GetMyProfileQuery, ProfileDto>
{
    public async Task<ProfileDto> Handle(GetMyProfileQuery request, CancellationToken cancellationToken)
    {
        var profile = await MyProfile.GetAsync(profiles, caller, cancellationToken);
        if (profile.RecordAgeBand(caller.IsMinor))
        {
            await profiles.SaveChangesAsync(cancellationToken);
        }

        var dto = await Avatars.WithFollowsAsync(profiles, await Avatars.ToDtoAsync(media, profile, cancellationToken), profile.Id, cancellationToken);
        return await Avatars.WithBadgesAsync(profiles, dto, profile, cancellationToken);
    }
}

/// <summary>Anyone's public profile, by @handle. Open to anonymous callers.</summary>
public sealed record GetProfileQuery(string Handle) : IRequest<ProfileDto>;

public sealed class GetProfileQueryHandler(IProfileRepository profiles, IMediaDirectory media, ICurrentUser? caller = null)
    : IRequestHandler<GetProfileQuery, ProfileDto>
{
    public async Task<ProfileDto> Handle(GetProfileQuery request, CancellationToken cancellationToken)
    {
        var handle = Domain.Handle.Normalise(request.Handle);
        var profile = Domain.Handle.IsValid(handle)
            ? await profiles.GetByHandleAsync(handle, cancellationToken)
            : null;

        // A deactivated user's profile looks exactly like one that doesn't exist.
        return profile is not { IsActive: true }
            ? throw new NotFoundException("Profile", request.Handle)
            : await Avatars.WithBadgesAsync(
                profiles,
                await Avatars.WithFollowsAsync(
                    profiles,
                    await Avatars.ToDtoAsync(media, profile, cancellationToken, forOwner: false),
                    caller?.UserId(),
                    cancellationToken),
                profile,
                cancellationToken);
    }
}

/// <summary>Whether a handle can be taken, for checking as the user types. Open to anonymous callers.</summary>
public sealed record CheckHandleQuery(string Handle) : IRequest<HandleAvailability>;

public sealed class CheckHandleQueryHandler(IProfileRepository profiles, ICurrentUser caller)
    : IRequestHandler<CheckHandleQuery, HandleAvailability>
{
    public async Task<HandleAvailability> Handle(CheckHandleQuery request, CancellationToken cancellationToken)
    {
        var handle = Domain.Handle.Normalise(request.Handle);
        var problem = Domain.Handle.Problem(handle);
        if (problem is not null)
        {
            return new HandleAvailability(handle, false, problem);
        }

        var holder = await profiles.GetByHandleAsync(handle, cancellationToken);
        return holder is null || holder.IsOwnedBy(caller.UserId() ?? Guid.Empty)
            ? new HandleAvailability(handle, true, null)
            : new HandleAvailability(handle, false, "That handle is taken.");
    }
}
