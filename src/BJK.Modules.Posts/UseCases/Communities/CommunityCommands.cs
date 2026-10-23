using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Safety;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Communities;

/// <summary>What a community's admin sets, when creating or changing it.</summary>
public interface ICommunityRequest
{
    string Name { get; }

    string? Description { get; }

    string? Rules { get; }

    CommunityVisibility Visibility { get; }

    string? PlaceName { get; }

    decimal? Latitude { get; }

    decimal? Longitude { get; }

    int? RadiusKm { get; }
}

/// <summary>Start a community (Premium and Premium +, adults; up to 5 each). You're its admin.</summary>
public sealed record CreateCommunityCommand(
    string Name,
    string? Description,
    string? Rules,
    CommunityVisibility Visibility,
    string? PlaceName = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    int? RadiusKm = null) : IRequest<CommunityDto>, ICommunityRequest;

/// <summary>Change a community's details (admins). Its address stays the same.</summary>
public sealed record UpdateCommunityCommand(
    string Slug,
    string Name,
    string? Description,
    string? Rules,
    CommunityVisibility Visibility,
    string? PlaceName = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    int? RadiusKm = null) : IRequest<CommunityDto>, ICommunityRequest;

/// <summary>Delete a community for everyone (its admins). The posts shared into it stay on Bad Jockeys.</summary>
public sealed record DeleteCommunityCommand(string Slug) : IRequest;

/// <summary>Join a public community, or ask to join a private one (adults only).</summary>
public sealed record JoinCommunityCommand(string Slug) : IRequest<CommunityDto>;

/// <summary>Leave (or withdraw your request). The last admin can't leave: delete it, or make someone else admin.</summary>
public sealed record LeaveCommunityCommand(string Slug) : IRequest;

/// <summary>Approve or decline a request to join (admins and moderators).</summary>
public sealed record DecideCommunityRequestCommand(string Slug, Guid UserId, bool Approve) : IRequest;

/// <summary>Make a member an admin, a moderator or a plain member (admins). Admins and moderators must be adults.</summary>
public sealed record SetCommunityRoleCommand(string Slug, Guid UserId, CommunityRole Role) : IRequest;

/// <summary>Remove a member, optionally banning them (admins and moderators; moderators can't remove admins).</summary>
public sealed record RemoveCommunityMemberCommand(string Slug, Guid UserId, bool Ban) : IRequest;

/// <summary>Let a banned person join or ask again (admins and moderators).</summary>
public sealed record UnbanCommunityMemberCommand(string Slug, Guid UserId) : IRequest;

public abstract class CommunityRequestValidator<T> : AbstractValidator<T>
    where T : ICommunityRequest
{
    protected CommunityRequestValidator()
    {
        RuleFor(c => c.Name).NotEmpty().Length(Community.NameMinLength, Community.NameMaxLength);
        RuleFor(c => c.Description).MaximumLength(Community.DescriptionMaxLength);
        RuleFor(c => c.Rules).MaximumLength(Community.RulesMaxLength);
        RuleFor(c => c.Visibility).IsInEnum();
        When(c => c.PlaceName is not null || c.Latitude is not null || c.Longitude is not null || c.RadiusKm is not null, () =>
        {
            RuleFor(c => c.PlaceName).NotEmpty().MaximumLength(Community.PlaceNameMaxLength).WithMessage("Give the area a place name.");
            RuleFor(c => c.Latitude).NotNull().InclusiveBetween(49m, 61m).WithMessage("Choose a place in the UK.");
            RuleFor(c => c.Longitude).NotNull().InclusiveBetween(-9m, 2.5m).WithMessage("Choose a place in the UK.");
            RuleFor(c => c.RadiusKm).NotNull()
                .Must(r => r is { } km && Community.Radiuses.Contains(km))
                .WithMessage($"Choose a radius of {string.Join(", ", Community.Radiuses)} km.");
        });
    }
}

public sealed class CreateCommunityCommandValidator : CommunityRequestValidator<CreateCommunityCommand>;

public sealed class UpdateCommunityCommandValidator : CommunityRequestValidator<UpdateCommunityCommand>;

public sealed class SetCommunityRoleCommandValidator : AbstractValidator<SetCommunityRoleCommand>
{
    public SetCommunityRoleCommandValidator() => RuleFor(c => c.Role).IsInEnum();
}

internal static class CommunityRequests
{
    public static CommunityDetails Details(ICommunityRequest r) => new(
        r.Name,
        r.Description,
        r.Rules,
        r.Visibility,
        r.PlaceName is { } place && r.Latitude is { } lat && r.Longitude is { } lng && r.RadiusKm is { } km
            ? new CommunityArea(place, lat, lng, km)
            : null);

    /// <summary>Text people write in a community is screened like comments; what would be held is refused.</summary>
    public static async Task<bool> ScreenAsync(IContentScreener screener, string text, CancellationToken cancellationToken)
    {
        var result = await screener.ScreenAsync(text, cancellationToken);
        if (result.Verdict == ScreeningVerdict.Hold)
        {
            throw PostAccess.Invalid(
                "Text",
                "That looks like it breaks the community guidelines (threats, abuse or calls to confront someone). Please reword it.");
        }

        return result.Verdict == ScreeningVerdict.Flag;
    }
}

public sealed class CreateCommunityCommandHandler(
    ICommunityRepository communities,
    IProfileDirectory profiles,
    IPlanResolver plans,
    IContentScreener screener,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<CreateCommunityCommand, CommunityDto>
{
    public async Task<CommunityDto> Handle(CreateCommunityCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        PostAccess.RequireVerifiedEmail(caller, "starting a community");
        if (plans.CurrentPlan() is not (SubscriptionPlan.Premium or SubscriptionPlan.PremiumPlus))
        {
            throw new ForbiddenException("Starting a community needs Premium.");
        }

        var people = await profiles.GetSummariesAsync([userId], cancellationToken);
        if (!people.TryGetValue(userId, out var me))
        {
            throw PostAccess.Invalid("Profile", "Create your profile before starting a community.");
        }

        if (!me.IsAdult)
        {
            throw new ForbiddenException("Communities are run by adults.");
        }

        if (await communities.CountOwnedAsync(userId, cancellationToken) >= CommunityAccess.MaxOwned)
        {
            throw PostAccess.Invalid(nameof(request.Name), $"You can run up to {CommunityAccess.MaxOwned} communities.");
        }

        await CommunityRequests.ScreenAsync(screener, $"{request.Name}\n{request.Description}\n{request.Rules}", cancellationToken);

        // Its address from the name; a number on the end if that's taken.
        var baseSlug = Community.SlugFrom(request.Name);
        var slug = baseSlug;
        for (var n = 2; await communities.SlugTakenAsync(slug, cancellationToken); n++)
        {
            slug = $"{baseSlug}-{n}";
        }

        var now = clock.GetUtcNow();
        var community = Community.Create(userId, slug, CommunityRequests.Details(request), now);
        var admin = CommunityMember.Join(community.Id, userId, now, CommunityRole.Admin);
        community.CountMembers(1);
        communities.Add(community);
        communities.Add(admin);
        await communities.SaveChangesAsync(cancellationToken);
        return CommunityAccess.Detail(community, admin, people, viewerIsAdult: true);
    }
}

public sealed class UpdateCommunityCommandHandler(
    ICommunityRepository communities,
    IProfileDirectory profiles,
    IContentScreener screener,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<UpdateCommunityCommand, CommunityDto>
{
    public async Task<CommunityDto> Handle(UpdateCommunityCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        var admin = await CommunityAccess.RequireAdminAsync(communities, community, userId, cancellationToken);
        await CommunityRequests.ScreenAsync(screener, $"{request.Name}\n{request.Description}\n{request.Rules}", cancellationToken);
        community.Update(CommunityRequests.Details(request), clock.GetUtcNow());
        await communities.SaveChangesAsync(cancellationToken);
        var people = await profiles.GetSummariesAsync([community.OwnerId], cancellationToken);
        return CommunityAccess.Detail(community, admin, people, viewerIsAdult: true);
    }
}

public sealed class DeleteCommunityCommandHandler(ICommunityRepository communities, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<DeleteCommunityCommand>
{
    public async Task Handle(DeleteCommunityCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        await CommunityAccess.RequireAdminAsync(communities, community, userId, cancellationToken);
        community.Remove("Deleted by its admin.", clock.GetUtcNow());
        await communities.SaveChangesAsync(cancellationToken);
    }
}

public sealed class JoinCommunityCommandHandler(ICommunityRepository communities, IProfileDirectory profiles, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<JoinCommunityCommand, CommunityDto>
{
    public async Task<CommunityDto> Handle(JoinCommunityCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        var people = await profiles.GetSummariesAsync([userId, community.OwnerId], cancellationToken);
        if (!people.TryGetValue(userId, out var me))
        {
            throw PostAccess.Invalid("Profile", "Create your profile before joining a community.");
        }

        var member = await communities.GetMemberAsync(community.Id, userId, cancellationToken);
        if (member?.Status == CommunityMembershipStatus.Banned)
        {
            throw new ForbiddenException("You can't join this community.");
        }

        if (member is null)
        {
            var now = clock.GetUtcNow();
            if (community.Visibility == CommunityVisibility.Public)
            {
                member = CommunityMember.Join(community.Id, userId, now);
                community.CountMembers(1);
            }
            else if (me.IsAdult)
            {
                member = CommunityMember.Request(community.Id, userId, now);
            }
            else
            {
                // Owner, 23 Oct 2026: under-18s use public communities only.
                throw new ForbiddenException("Private communities are for adults. You can join public ones.");
            }

            communities.Add(member);
            await communities.SaveChangesAsync(cancellationToken);
        }

        return CommunityAccess.Detail(community, member, people, me.IsAdult);
    }
}

public sealed class LeaveCommunityCommandHandler(ICommunityRepository communities, ICurrentUser caller)
    : IRequestHandler<LeaveCommunityCommand>
{
    public async Task Handle(LeaveCommunityCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        var member = await communities.GetMemberAsync(community.Id, userId, cancellationToken);
        if (member is null || member.Status == CommunityMembershipStatus.Banned)
        {
            return;
        }

        if (member.IsAdmin && await communities.CountAdminsAsync(community.Id, cancellationToken) <= 1)
        {
            throw PostAccess.Invalid(nameof(request.Slug), "You're its only admin. Make someone else an admin first, or delete the community.");
        }

        if (member.IsMember)
        {
            community.CountMembers(-1);
        }

        communities.Remove(member);
        await communities.SaveChangesAsync(cancellationToken);
    }
}

public sealed class DecideCommunityRequestCommandHandler(ICommunityRepository communities, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<DecideCommunityRequestCommand>
{
    public async Task Handle(DecideCommunityRequestCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        await CommunityAccess.RequireModeratorAsync(communities, community, userId, cancellationToken);
        var asking = await communities.GetMemberAsync(community.Id, request.UserId, cancellationToken);
        if (asking?.Status != CommunityMembershipStatus.Requested)
        {
            throw new NotFoundException("Request to join", request.UserId);
        }

        if (request.Approve)
        {
            asking.Approve(clock.GetUtcNow());
            community.CountMembers(1);
        }
        else
        {
            communities.Remove(asking);
        }

        await communities.SaveChangesAsync(cancellationToken);
    }
}

public sealed class SetCommunityRoleCommandHandler(ICommunityRepository communities, IProfileDirectory profiles, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<SetCommunityRoleCommand>
{
    public async Task Handle(SetCommunityRoleCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        await CommunityAccess.RequireAdminAsync(communities, community, userId, cancellationToken);
        var member = await communities.GetMemberAsync(community.Id, request.UserId, cancellationToken);
        if (member is not { IsMember: true })
        {
            throw new NotFoundException("Member", request.UserId);
        }

        if (request.Role != CommunityRole.Member && !await CommunityAccess.IsAdultAsync(profiles, request.UserId, cancellationToken))
        {
            // Owner, 23 Oct 2026: under-18s are never admins (nor moderators).
            throw PostAccess.Invalid(nameof(request.Role), "Only adults can be admins or moderators.");
        }

        if (member.IsAdmin && request.Role != CommunityRole.Admin && await communities.CountAdminsAsync(community.Id, cancellationToken) <= 1)
        {
            throw PostAccess.Invalid(nameof(request.Role), "A community needs at least one admin.");
        }

        member.SetRole(request.Role, clock.GetUtcNow());
        await communities.SaveChangesAsync(cancellationToken);
    }
}

public sealed class RemoveCommunityMemberCommandHandler(ICommunityRepository communities, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<RemoveCommunityMemberCommand>
{
    public async Task Handle(RemoveCommunityMemberCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        var me = await CommunityAccess.RequireModeratorAsync(communities, community, userId, cancellationToken);
        if (request.UserId == userId)
        {
            throw PostAccess.Invalid(nameof(request.UserId), "To leave, use Leave.");
        }

        var member = await communities.GetMemberAsync(community.Id, request.UserId, cancellationToken)
            ?? throw new NotFoundException("Member", request.UserId);
        if (member.Role != CommunityRole.Member && !me.IsAdmin)
        {
            throw new ForbiddenException("Only admins can remove admins and moderators.");
        }

        if (member.IsAdmin && await communities.CountAdminsAsync(community.Id, cancellationToken) <= 1)
        {
            throw PostAccess.Invalid(nameof(request.UserId), "A community needs at least one admin.");
        }

        if (member.IsMember)
        {
            community.CountMembers(-1);
        }

        if (request.Ban)
        {
            member.Ban(clock.GetUtcNow());
        }
        else
        {
            communities.Remove(member);
        }

        await communities.SaveChangesAsync(cancellationToken);
    }
}

public sealed class UnbanCommunityMemberCommandHandler(ICommunityRepository communities, ICurrentUser caller)
    : IRequestHandler<UnbanCommunityMemberCommand>
{
    public async Task Handle(UnbanCommunityMemberCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        await CommunityAccess.RequireModeratorAsync(communities, community, userId, cancellationToken);
        var member = await communities.GetMemberAsync(community.Id, request.UserId, cancellationToken);
        if (member?.Status == CommunityMembershipStatus.Banned)
        {
            communities.Remove(member);
            await communities.SaveChangesAsync(cancellationToken);
        }
    }
}
