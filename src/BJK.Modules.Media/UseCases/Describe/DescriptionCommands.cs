using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Describe;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Messaging;
using BJK.Modules.Shared.Security;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.UseCases.Describe;

/// <summary>The vehicle the post is about, as filled in so far.</summary>
public sealed record DescribeVehicle(string? Type, string? Make, string? Model, string? Colour);

/// <summary>
/// "Suggest a description": queue a draft title and description for the caller's media (once all of it is ready),
/// using what they've filled in so far. At most <c>Media:Describe:MaxPerDay</c> a UK day. Poll
/// <see cref="GetDescriptionQuery"/> for the result.
/// </summary>
/// <param name="MediaIds">The post's photos or video, in order.</param>
/// <param name="Types">The incident types chosen so far, e.g. "Tailgating".</param>
/// <param name="PlaceName">Road and town, if known.</param>
/// <param name="IncidentAt">When it happened, if known.</param>
/// <param name="IsDark">Dark or daylight, if known.</param>
/// <param name="Vehicle">The vehicle, if known.</param>
public sealed record RequestDescriptionCommand(
    IReadOnlyList<Guid> MediaIds,
    IReadOnlyList<string>? Types,
    string? PlaceName,
    DateTimeOffset? IncidentAt,
    bool? IsDark,
    DescribeVehicle? Vehicle) : IRequest<DescriptionDto>;

/// <summary>A draft, or where it is.</summary>
/// <param name="Id">The request.</param>
/// <param name="Status">Queued, Ready or Failed.</param>
/// <param name="Title">The draft title, once ready.</param>
/// <param name="Description">The draft description, once ready.</param>
/// <param name="Message">Why there's no draft, when it failed.</param>
/// <param name="RemainingToday">Requests left today.</param>
public sealed record DescriptionDto(Guid Id, DescriptionStatus Status, string? Title, string? Description, string? Message, int RemainingToday);

public sealed class RequestDescriptionCommandValidator : AbstractValidator<RequestDescriptionCommand>
{
    public RequestDescriptionCommandValidator()
    {
        RuleFor(c => c.MediaIds)
            .NotEmpty().WithMessage("Add a photo or video first.")
            .Must(ids => ids is null || ids.Count <= 10).WithMessage("A post has at most 10 photos.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count).WithMessage("Each photo can only be included once.");
        RuleFor(c => c.Types).Must(t => t is null || (t.Count <= 12 && t.All(x => x is not null && x.Length <= 40)));
        RuleFor(c => c.PlaceName).MaximumLength(200);
        RuleFor(c => c.Vehicle!.Type).MaximumLength(20).When(c => c.Vehicle is not null);
        RuleFor(c => c.Vehicle!.Make).MaximumLength(40).When(c => c.Vehicle is not null);
        RuleFor(c => c.Vehicle!.Model).MaximumLength(40).When(c => c.Vehicle is not null);
        RuleFor(c => c.Vehicle!.Colour).MaximumLength(20).When(c => c.Vehicle is not null);
    }
}

public sealed class RequestDescriptionCommandHandler(
    IMediaRepository media,
    IDescriptionStore descriptions,
    IMessageSender sender,
    ICurrentUser caller,
    IOptions<DescribeOptions> options,
    IOptions<MessagingOptions> queues,
    TimeProvider clock)
    : IRequestHandler<RequestDescriptionCommand, DescriptionDto>
{
    public async Task<DescriptionDto> Handle(RequestDescriptionCommand request, CancellationToken cancellationToken)
    {
        var userId = MediaAccess.RequireUserId(caller);
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var used = await descriptions.CountSinceAsync(userId, UkDay.StartOf(now), cancellationToken);
        if (used >= settings.MaxPerDay)
        {
            throw new TooManyRequestsException(
                $"You've used today's {settings.MaxPerDay} description suggestions. You can ask again from midnight, or write your own.",
                "description_limit");
        }

        var items = await media.GetUntrackedAsync(request.MediaIds.ToList(), cancellationToken);
        if (items.Count != request.MediaIds.Count || items.Any(m => !m.IsOwnedBy(userId)))
        {
            throw new ForbiddenException("You can only describe your own photos and videos.");
        }

        if (items.Any(m => m.Status != MediaStatus.Ready))
        {
            throw Invalid("Wait until your photos or video have finished processing.");
        }

        if (items.Any(m => m.MediaType == MediaType.Video) && items.Count > 1)
        {
            throw Invalid("A post has up to 10 photos or one video.");
        }

        var vehicle = request.Vehicle;
        var context = new DescriptionContext(
            request.Types ?? [],
            request.PlaceName,
            request.IncidentAt,
            request.IsDark,
            vehicle?.Type,
            vehicle?.Make,
            vehicle?.Model,
            vehicle?.Colour);
        var description = MediaDescription.Create(userId, request.MediaIds, context.ToJson(), now);
        descriptions.Add(description);
        await descriptions.SaveChangesWithMessagesAsync(
            ct => sender.SendAsync(
                queues.Value.MediaDescriptions,
                OutgoingMessage.Create(description.Id.ToString("N"), nameof(DescriptionJob), new DescriptionJob(description.Id)),
                ct),
            cancellationToken);

        return DescriptionDtos.From(description, settings.MaxPerDay - used - 1);
    }

    private static ValidationException Invalid(string message) =>
        new([new ValidationFailure(nameof(RequestDescriptionCommand.MediaIds), message)]);
}

/// <summary>A "Suggest a description" request, for the person who made it.</summary>
public sealed record GetDescriptionQuery(Guid Id) : IRequest<DescriptionDto>;

public sealed class GetDescriptionQueryHandler(
    IDescriptionStore descriptions,
    ICurrentUser caller,
    IOptions<DescribeOptions> options,
    TimeProvider clock)
    : IRequestHandler<GetDescriptionQuery, DescriptionDto>
{
    public async Task<DescriptionDto> Handle(GetDescriptionQuery request, CancellationToken cancellationToken)
    {
        var userId = MediaAccess.RequireUserId(caller);
        var description = await descriptions.GetAsync(request.Id, cancellationToken);
        if (description is null || description.UserId != userId)
        {
            throw new NotFoundException("Description", request.Id);
        }

        var used = await descriptions.CountSinceAsync(userId, UkDay.StartOf(clock.GetUtcNow()), cancellationToken);
        return DescriptionDtos.From(description, options.Value.MaxPerDay - used);
    }
}

internal static class DescriptionDtos
{
    public static DescriptionDto From(MediaDescription d, int remaining) => new(
        d.Id,
        d.Status,
        d.Status == DescriptionStatus.Ready ? d.Title : null,
        d.Status == DescriptionStatus.Ready ? d.Text : null,
        d.Status == DescriptionStatus.Failed ? d.FailureReason : null,
        Math.Max(0, remaining));
}

/// <summary>UK calendar days (Europe/London, following British Summer Time), for the daily limit.</summary>
internal static class UkDay
{
    private static readonly Lazy<TimeZoneInfo> Zone = new(() =>
    {
        foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        throw new InvalidOperationException("The Europe/London time zone isn't available (install tzdata in the container).");
    });

    /// <summary>Midnight UK time at the start of the day <paramref name="now"/> is in.</summary>
    public static DateTimeOffset StartOf(DateTimeOffset now)
    {
        var midnight = TimeZoneInfo.ConvertTime(now, Zone.Value).Date;
        return new DateTimeOffset(midnight, Zone.Value.GetUtcOffset(midnight));
    }
}
