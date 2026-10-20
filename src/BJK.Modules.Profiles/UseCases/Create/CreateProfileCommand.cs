using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Profiles.UseCases.Create;

/// <summary>Creates the signed-in user's public profile. Each user has at most one, and needs one to post.</summary>
public sealed record CreateProfileCommand(string Handle, string DisplayName, string? Bio, string? Location)
    : IRequest<ProfileDto>, IProfileDetails;

public sealed class CreateProfileCommandValidator : ProfileDetailsValidator<CreateProfileCommand>
{
    public CreateProfileCommandValidator()
    {
        RuleFor(c => c.Handle).ValidHandle();
    }
}

public sealed class CreateProfileCommandHandler(
    IProfileRepository profiles,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<CreateProfileCommand, ProfileDto>
{
    public async Task<ProfileDto> Handle(CreateProfileCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();

        if (await profiles.GetByIdAsync(userId, cancellationToken) is not null)
        {
            throw ProfileRules.Invalid(string.Empty, "You already have a profile.");
        }

        var handle = Domain.Handle.Normalise(request.Handle);
        if (await profiles.GetByHandleAsync(handle, cancellationToken) is not null)
        {
            throw ProfileRules.Invalid(nameof(request.Handle), "That handle is taken.");
        }

        var profile = Profile.Create(userId, handle, request.DisplayName, request.Bio, request.Location, clock.GetUtcNow());
        profile.RecordAgeBand(caller.IsMinor);
        profiles.Add(profile);
        await profiles.SaveChangesAsync(cancellationToken);

        return ProfileDto.From(profile, avatarUrl: null);
    }
}
