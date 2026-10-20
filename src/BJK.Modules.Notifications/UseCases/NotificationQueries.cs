using BJK.Modules.Notifications.Domain;
using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Notifications.UseCases;

/// <summary>Who did it. <c>Handle</c> is null if their profile has gone ("Former member").</summary>
public sealed record NotificationActorDto(Guid Id, string? Handle, string DisplayName, Uri? AvatarUrl);

/// <summary>
/// One notification for the bell. <c>Text</c> is ready to show ("Sam J commented on your post “...”"); <c>At</c> is
/// when it last changed (a grouped reaction); <c>PostId</c> is where tapping it goes.
/// </summary>
public sealed record NotificationDto(
    Guid Id,
    NotificationType Type,
    NotificationActorDto? Actor,
    Guid? PostId,
    Guid? CommentId,
    string? PostTitle,
    int Count,
    string Text,
    DateTimeOffset At,
    bool IsRead);

/// <summary>The signed-in user's notifications, newest first.</summary>
public sealed record ListNotificationsQuery(int Page = 1, int PageSize = 20) : IRequest<PagedResult<NotificationDto>>;

public sealed class ListNotificationsQueryValidator : AbstractValidator<ListNotificationsQuery>
{
    public ListNotificationsQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 50);
    }
}

public sealed class ListNotificationsQueryHandler(INotificationRepository notifications, IProfileDirectory profiles, ICurrentUser caller)
    : IRequestHandler<ListNotificationsQuery, PagedResult<NotificationDto>>
{
    public async Task<PagedResult<NotificationDto>> Handle(ListNotificationsQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var (items, total) = await notifications.ListAsync(userId, request.Page, request.PageSize, cancellationToken);
        var actorIds = items.Select(n => n.ActorId).OfType<Guid>().Distinct().ToList();
        var people = actorIds.Count == 0
            ? new Dictionary<Guid, ProfileSummary>()
            : await profiles.GetSummariesAsync(actorIds, cancellationToken);

        var dtos = items.Select(n =>
        {
            NotificationActorDto? actor = null;
            if (n.ActorId is { } actorId)
            {
                actor = people.TryGetValue(actorId, out var p)
                    ? new NotificationActorDto(p.UserId, p.Handle, p.DisplayName, p.AvatarUrl)
                    : new NotificationActorDto(actorId, null, "Former member", null);
            }

            return new NotificationDto(
                n.Id,
                n.Type,
                actor,
                n.PostId,
                n.CommentId,
                n.PostTitle,
                n.Count,
                NotificationTexts.Sentence(n.Type, actor?.DisplayName, n.Count, n.PostTitle),
                n.UpdatedAt,
                n.IsRead);
        }).ToList();

        return new PagedResult<NotificationDto>(dtos, request.Page, request.PageSize, total);
    }
}

/// <summary>
/// How many unread notifications the signed-in user has, for the bell's badge. Also records the confirmed email
/// address from their token, for notification emails (and forgets it if it's no longer confirmed).
/// </summary>
public sealed record GetUnreadCountQuery : IRequest<UnreadCountDto>;

public sealed record UnreadCountDto(int Unread);

public sealed class GetUnreadCountQueryHandler(
    INotificationRepository notifications,
    ICurrentUser caller,
    TimeProvider clock,
    IAreaAlertRepository? areas = null,
    IPlanResolver? plans = null)
    : IRequestHandler<GetUnreadCountQuery, UnreadCountDto>
{
    public async Task<UnreadCountDto> Handle(GetUnreadCountQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        await RecordContactAsync(userId, cancellationToken);

        // Keeps area alerts in step with the plan in the user's token (alerts stop soon after Premium lapses).
        if (areas is not null && await areas.GetAsync(userId, cancellationToken) is { } area
            && area.RecordPlan(Plans.IsPremium(plans), clock.GetUtcNow()))
        {
            await areas.SaveChangesAsync(cancellationToken);
        }

        return new UnreadCountDto(await notifications.CountUnreadAsync(userId, cancellationToken));
    }

    private async Task RecordContactAsync(Guid userId, CancellationToken cancellationToken)
    {
        var address = caller.EmailVerified ? caller.Email?.Trim() : null;
        if (address is { Length: > NotificationContact.EmailMaxLength })
        {
            address = null;
        }

        var contact = await notifications.GetContactAsync(userId, cancellationToken);
        var changed = false;
        if (string.IsNullOrEmpty(address))
        {
            if (contact is not null)
            {
                notifications.RemoveContact(contact);
                changed = true;
            }
        }
        else if (contact is null)
        {
            notifications.AddContact(NotificationContact.Create(userId, address, clock.GetUtcNow()));
            changed = true;
        }
        else
        {
            changed = contact.Change(address, clock.GetUtcNow());
        }

        if (changed)
        {
            await notifications.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>Marks the given notifications read, or all of them when <c>Ids</c> is null or empty.</summary>
public sealed record MarkNotificationsReadCommand(IReadOnlyList<Guid>? Ids = null) : IRequest;

public sealed class MarkNotificationsReadCommandValidator : AbstractValidator<MarkNotificationsReadCommand>
{
    public MarkNotificationsReadCommandValidator()
    {
        RuleFor(c => c.Ids).Must(ids => ids is null || ids.Count <= 200).WithMessage("Up to 200 at a time.");
    }
}

public sealed class MarkNotificationsReadCommandHandler(INotificationRepository notifications, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<MarkNotificationsReadCommand>
{
    public Task Handle(MarkNotificationsReadCommand request, CancellationToken cancellationToken) =>
        notifications.MarkReadAsync(
            caller.RequireUserId(),
            request.Ids is { Count: > 0 } ids ? ids : null,
            clock.GetUtcNow(),
            cancellationToken);
}

/// <summary>One type's setting: show it in the bell, and email it.</summary>
public sealed record NotificationSettingDto(NotificationType Type, string Label, bool InApp, bool Email);

/// <summary>The signed-in user's notification settings, every type, defaults filled in.</summary>
public sealed record GetNotificationSettingsQuery : IRequest<IReadOnlyList<NotificationSettingDto>>;

public sealed class GetNotificationSettingsQueryHandler(INotificationRepository notifications, ICurrentUser caller)
    : IRequestHandler<GetNotificationSettingsQuery, IReadOnlyList<NotificationSettingDto>>
{
    public async Task<IReadOnlyList<NotificationSettingDto>> Handle(GetNotificationSettingsQuery request, CancellationToken cancellationToken) =>
        Build(await notifications.GetSettingsAsync(caller.RequireUserId(), cancellationToken));

    internal static IReadOnlyList<NotificationSettingDto> Build(IReadOnlyList<NotificationSetting> chosen) =>
        NotificationDefaults.Configurable.Select(type =>
        {
            var setting = chosen.FirstOrDefault(s => s.Type == type);
            var (inApp, email) = setting is null ? NotificationDefaults.For(type) : (setting.InApp, setting.Email);
            return new NotificationSettingDto(type, NotificationTexts.Label(type), inApp, email);
        }).ToList();
}

/// <summary>A change to one type's setting.</summary>
public sealed record NotificationSettingChange(NotificationType Type, bool InApp, bool Email);

/// <summary>Changes the signed-in user's settings for the types given; others stay as they are.</summary>
public sealed record UpdateNotificationSettingsCommand(IReadOnlyList<NotificationSettingChange> Settings)
    : IRequest<IReadOnlyList<NotificationSettingDto>>;

public sealed class UpdateNotificationSettingsCommandValidator : AbstractValidator<UpdateNotificationSettingsCommand>
{
    public UpdateNotificationSettingsCommandValidator()
    {
        RuleFor(c => c.Settings).NotNull();
        RuleForEach(c => c.Settings)
            .Must(s => NotificationDefaults.Configurable.Contains(s.Type))
            .WithMessage("That notification can't be changed here.");
    }
}

public sealed class UpdateNotificationSettingsCommandHandler(INotificationRepository notifications, ICurrentUser caller)
    : IRequestHandler<UpdateNotificationSettingsCommand, IReadOnlyList<NotificationSettingDto>>
{
    public async Task<IReadOnlyList<NotificationSettingDto>> Handle(
        UpdateNotificationSettingsCommand request,
        CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var current = (await notifications.GetSettingsAsync(userId, cancellationToken)).ToList();
        foreach (var change in request.Settings)
        {
            var setting = current.FirstOrDefault(s => s.Type == change.Type);
            if (setting is null)
            {
                setting = NotificationSetting.Create(userId, change.Type, change.InApp, change.Email);
                notifications.AddSetting(setting);
                current.Add(setting);
            }
            else
            {
                setting.Change(change.InApp, change.Email);
            }
        }

        await notifications.SaveChangesAsync(cancellationToken);
        return GetNotificationSettingsQueryHandler.Build(current);
    }
}
