using BJK.Modules.Notifications.Domain;
using BJK.Modules.Notifications.Infrastructure;
using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Notifications.UseCases;

/// <summary>
/// Your home area and area alerts. <c>PlaceName</c> and the position (to about 1 km) are null until you choose a home
/// area. <c>CanUseAlerts</c> is true on Premium and Premium +. Quiet hours are 22:00 to 07:00 UK time; Instant alerts
/// are capped at <c>InstantDailyCap</c> a day; summaries go at 08:00.
/// </summary>
public sealed record AreaSettingsDto(
    string? PlaceName,
    decimal? Latitude,
    decimal? Longitude,
    bool Enabled,
    int RadiusKm,
    AreaAlertMode Mode,
    bool Email,
    bool CanUseAlerts,
    IReadOnlyList<int> Radiuses,
    int InstantDailyCap)
{
    internal static AreaSettingsDto From(AreaSubscription? s, bool canUseAlerts) => s is null
        ? new(null, null, null, false, AreaSubscription.DefaultRadiusKm, AreaAlertMode.Digest, false, canUseAlerts, AreaSubscription.Radiuses, AreaSubscription.InstantDailyCap)
        : new(s.PlaceName, s.Latitude, s.Longitude, s.Enabled && canUseAlerts, s.RadiusKm, s.Mode, s.Email, canUseAlerts, AreaSubscription.Radiuses, AreaSubscription.InstantDailyCap);
}

internal static class Plans
{
    public static bool IsPremium(IPlanResolver? plans) =>
        plans?.CurrentPlan() is SubscriptionPlan.Premium or SubscriptionPlan.PremiumPlus;
}

/// <summary>The signed-in user's home area and alert settings.</summary>
public sealed record GetAreaSettingsQuery : IRequest<AreaSettingsDto>;

public sealed class GetAreaSettingsQueryHandler(IAreaAlertRepository areas, ICurrentUser caller, TimeProvider clock, IPlanResolver? plans = null)
    : IRequestHandler<GetAreaSettingsQuery, AreaSettingsDto>
{
    public async Task<AreaSettingsDto> Handle(GetAreaSettingsQuery request, CancellationToken cancellationToken)
    {
        var premium = Plans.IsPremium(plans);
        var subscription = await areas.GetAsync(caller.RequireUserId(), cancellationToken);
        if (subscription?.RecordPlan(premium, clock.GetUtcNow()) == true)
        {
            await areas.SaveChangesAsync(cancellationToken);
        }

        return AreaSettingsDto.From(subscription, premium);
    }
}

/// <summary>
/// Sets the signed-in user's home area: a place from <c>/api/places</c> (name and position, kept to about 1 km).
/// Anyone can set one; alerts need Premium.
/// </summary>
public sealed record SetHomeAreaCommand(string PlaceName, decimal Latitude, decimal Longitude) : IRequest<AreaSettingsDto>;

public sealed class SetHomeAreaCommandValidator : AbstractValidator<SetHomeAreaCommand>
{
    public SetHomeAreaCommandValidator()
    {
        RuleFor(c => c.PlaceName).NotEmpty().MaximumLength(AreaSubscription.PlaceNameMaxLength);

        // Great Britain and Northern Ireland, with a margin (the lookup only returns UK places).
        RuleFor(c => c.Latitude).InclusiveBetween(49m, 61m).WithMessage("Choose a place in the UK.");
        RuleFor(c => c.Longitude).InclusiveBetween(-9m, 2.5m).WithMessage("Choose a place in the UK.");
    }
}

public sealed class SetHomeAreaCommandHandler(IAreaAlertRepository areas, ICurrentUser caller, TimeProvider clock, IPlanResolver? plans = null)
    : IRequestHandler<SetHomeAreaCommand, AreaSettingsDto>
{
    public async Task<AreaSettingsDto> Handle(SetHomeAreaCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var now = clock.GetUtcNow();
        var premium = Plans.IsPremium(plans);
        var subscription = await areas.GetAsync(userId, cancellationToken);
        if (subscription is null)
        {
            subscription = AreaSubscription.Create(userId, request.PlaceName, request.Latitude, request.Longitude, now);
            areas.Add(subscription);
        }
        else
        {
            subscription.SetHome(request.PlaceName, request.Latitude, request.Longitude, now);
        }

        subscription.RecordPlan(premium, now);
        await areas.SaveChangesAsync(cancellationToken);
        return AreaSettingsDto.From(subscription, premium);
    }
}

/// <summary>
/// Changes the signed-in user's area alerts: on or off, radius (2, 5, 10 or 25 km), Digest (daily at 08:00) or
/// Instant, and email. Needs a home area first; switching them on needs Premium (403).
/// </summary>
public sealed record SetAreaAlertsCommand(bool Enabled, int RadiusKm = AreaSubscription.DefaultRadiusKm, AreaAlertMode Mode = AreaAlertMode.Digest, bool Email = false)
    : IRequest<AreaSettingsDto>;

public sealed class SetAreaAlertsCommandValidator : AbstractValidator<SetAreaAlertsCommand>
{
    public SetAreaAlertsCommandValidator()
    {
        RuleFor(c => c.RadiusKm).Must(r => AreaSubscription.Radiuses.Contains(r)).WithMessage("Choose 2, 5, 10 or 25 km.");
        RuleFor(c => c.Mode).IsInEnum();
    }
}

public sealed class SetAreaAlertsCommandHandler(IAreaAlertRepository areas, ICurrentUser caller, TimeProvider clock, IPlanResolver? plans = null)
    : IRequestHandler<SetAreaAlertsCommand, AreaSettingsDto>
{
    public async Task<AreaSettingsDto> Handle(SetAreaAlertsCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var premium = Plans.IsPremium(plans);
        if (request.Enabled && !premium)
        {
            throw new ForbiddenException("Area alerts are part of Premium.");
        }

        var subscription = await areas.GetAsync(userId, cancellationToken)
            ?? throw new ValidationException(new[] { new FluentValidation.Results.ValidationFailure("PlaceName", "Choose your home area first.") });
        var now = clock.GetUtcNow();
        subscription.SetAlerts(request.Enabled, request.RadiusKm, request.Mode, request.Email, now);
        subscription.RecordPlan(premium, now);
        await areas.SaveChangesAsync(cancellationToken);
        return AreaSettingsDto.From(subscription, premium);
    }
}

/// <summary>Forgets the signed-in user's home area and switches area alerts off.</summary>
public sealed record ClearHomeAreaCommand : IRequest;

public sealed class ClearHomeAreaCommandHandler(IAreaAlertRepository areas, ICurrentUser caller) : IRequestHandler<ClearHomeAreaCommand>
{
    public Task Handle(ClearHomeAreaCommand request, CancellationToken cancellationToken) =>
        areas.RemoveAsync(caller.RequireUserId(), cancellationToken);
}

/// <summary>UK places by name, postcode or postcode area, for choosing a home area.</summary>
public sealed record SearchPlacesQuery(string Q) : IRequest<IReadOnlyList<PlaceDto>>;

public sealed class SearchPlacesQueryValidator : AbstractValidator<SearchPlacesQuery>
{
    public SearchPlacesQueryValidator()
    {
        RuleFor(q => q.Q).NotEmpty().MinimumLength(2).MaximumLength(60);
    }
}

public sealed class SearchPlacesQueryHandler(IPlaceLookup places) : IRequestHandler<SearchPlacesQuery, IReadOnlyList<PlaceDto>>
{
    public Task<IReadOnlyList<PlaceDto>> Handle(SearchPlacesQuery request, CancellationToken cancellationToken) =>
        places.SearchAsync(request.Q, cancellationToken);
}
