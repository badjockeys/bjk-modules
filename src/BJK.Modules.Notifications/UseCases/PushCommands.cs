using BJK.Modules.Notifications.Domain;
using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Notifications.Infrastructure.Push;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Notifications.UseCases;

/// <summary>
/// Whether push notifications are available, and the key browsers subscribe with (the VAPID public key). When
/// <c>Available</c> is false the app doesn't offer push.
/// </summary>
public sealed record PushSetupDto(bool Available, string? PublicKey);

public sealed record GetPushSetupQuery : IRequest<PushSetupDto>;

public sealed class GetPushSetupQueryHandler(IOptions<NotificationsOptions> options) : IRequestHandler<GetPushSetupQuery, PushSetupDto>
{
    public Task<PushSetupDto> Handle(GetPushSetupQuery request, CancellationToken cancellationToken)
    {
        var push = options.Value.Push;
        var valid = PushKeyChecks.IsPublicKey(push.PublicKey) && PushKeyChecks.IsPrivateKey(push.PrivateKey);
        return Task.FromResult(valid ? new PushSetupDto(true, push.PublicKey) : new PushSetupDto(false, null));
    }
}

/// <summary>
/// Turn push on for this browser: what the browser's <c>PushManager.subscribe</c> returned (its endpoint, and its
/// <c>p256dh</c> and <c>auth</c> keys, base64url). Subscribing the same browser again updates it, and it becomes the
/// signed-in user's. Someone with <see cref="PushSubscription.MaxPerUser"/> devices loses their oldest.
/// </summary>
public sealed record SubscribePushCommand(string Endpoint, string P256dh, string Auth) : IRequest;

public sealed class SubscribePushCommandValidator : AbstractValidator<SubscribePushCommand>
{
    public SubscribePushCommandValidator()
    {
        RuleFor(c => c.Endpoint)
            .Must(PushServices.IsAllowed)
            .WithMessage("That isn't a push address this browser's push service would give.");
        RuleFor(c => c.P256dh).Must(PushServices.IsPublicKey).WithMessage("The browser's key isn't valid.");
        RuleFor(c => c.Auth).Must(PushServices.IsAuthSecret).WithMessage("The browser's secret isn't valid.");
    }
}

public sealed class SubscribePushCommandHandler(INotificationRepository notifications, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<SubscribePushCommand>
{
    public async Task Handle(SubscribePushCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var now = clock.GetUtcNow();
        var existing = await notifications.GetPushSubscriptionAsync(request.Endpoint, cancellationToken);
        if (existing is not null)
        {
            existing.Renew(userId, request.P256dh, request.Auth, now);
        }
        else
        {
            var mine = await notifications.ListPushSubscriptionsAsync(userId, cancellationToken);
            foreach (var oldest in mine.Take(Math.Max(0, mine.Count - PushSubscription.MaxPerUser + 1)))
            {
                notifications.RemovePushSubscription(oldest);
            }

            notifications.AddPushSubscription(PushSubscription.Create(userId, request.Endpoint, request.P256dh, request.Auth, now));
        }

        await notifications.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Turn push off for this browser (its endpoint). Only your own devices; nothing happens otherwise.</summary>
public sealed record UnsubscribePushCommand(string Endpoint) : IRequest;

public sealed class UnsubscribePushCommandValidator : AbstractValidator<UnsubscribePushCommand>
{
    public UnsubscribePushCommandValidator() =>
        RuleFor(c => c.Endpoint).NotEmpty().MaximumLength(PushSubscription.EndpointMaxLength);
}

public sealed class UnsubscribePushCommandHandler(INotificationRepository notifications, ICurrentUser caller)
    : IRequestHandler<UnsubscribePushCommand>
{
    public async Task Handle(UnsubscribePushCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var existing = await notifications.GetPushSubscriptionAsync(request.Endpoint, cancellationToken);
        if (existing is not null && existing.UserId == userId)
        {
            notifications.RemovePushSubscription(existing);
            await notifications.SaveChangesAsync(cancellationToken);
        }
    }
}
