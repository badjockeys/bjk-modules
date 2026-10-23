using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Safety;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Cars;

/// <summary>One of your household cars. No registration is ever stored.</summary>
public sealed record HouseholdCarDto(Guid Id, string Make, string? Model, VehicleColour? Colour, string Nickname);

/// <summary>Your household cars (owner, 22 Oct 2026), oldest first.</summary>
public sealed record ListMyCarsQuery : IRequest<IReadOnlyList<HouseholdCarDto>>;

/// <summary>
/// Add a car (no <c>Id</c>) or change one of yours. The make and model come from <c>GET /api/vehicles</c>; the
/// nickname defaults to "Make Model". Up to <see cref="HouseholdCar.MaxPerUser"/> cars. Renaming a car renames it on
/// the posts filmed from it.
/// </summary>
public sealed record SaveCarCommand(Guid? Id, string Make, string? Model, VehicleColour? Colour, string? Nickname) : IRequest<HouseholdCarDto>;

/// <summary>Delete one of your cars. Posts filmed from it keep its nickname.</summary>
public sealed record DeleteCarCommand(Guid Id) : IRequest;

public sealed class SaveCarCommandValidator : AbstractValidator<SaveCarCommand>
{
    public SaveCarCommandValidator()
    {
        RuleFor(c => c.Make)
            .NotEmpty().WithMessage("Choose the make.")
            .Must(make => VehicleCatalogue.Make(make) is not null).WithMessage("Choose a make from the list.");
        RuleFor(c => c.Model)
            .Must((c, model) => VehicleCatalogue.Model(c.Make, model) is not null)
            .When(c => !string.IsNullOrWhiteSpace(c.Model))
            .WithMessage("Choose a model from the list.");
        RuleFor(c => c.Colour).IsInEnum();
        RuleFor(c => c.Nickname)
            .MaximumLength(HouseholdCar.NicknameMaxLength).WithMessage($"Nicknames are {HouseholdCar.NicknameMaxLength} characters or fewer.")
            .NoPersonalDetails();
    }
}

internal static class CarViews
{
    public static HouseholdCarDto From(HouseholdCar c) => new(c.Id, c.Make, c.Model, c.Colour, c.Nickname);

    /// <summary>
    /// The car a post is filmed from: null if none was chosen, else one of the author's own (a 400 otherwise, which
    /// also covers a car deleted meanwhile).
    /// </summary>
    public static async Task<HouseholdCar?> FilmedFromAsync(IHouseholdCarRepository? cars, Guid authorId, Guid? carId, CancellationToken cancellationToken)
    {
        if (carId is not { } id || cars is null)
        {
            return null;
        }

        var car = await cars.GetAsync(id, cancellationToken);
        return car is not null && car.UserId == authorId
            ? car
            : throw PostAccess.Invalid("FilmedFromCarId", "Choose one of your own cars, or none.");
    }

    /// <summary>One of the caller's own cars, or a 404 (someone else's car looks the same as none).</summary>
    public static async Task<HouseholdCar> RequireOwnAsync(IHouseholdCarRepository cars, Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var car = await cars.GetAsync(id, cancellationToken);
        return car is not null && car.UserId == userId ? car : throw new NotFoundException("Car", id);
    }
}

public sealed class ListMyCarsQueryHandler(IHouseholdCarRepository cars, ICurrentUser caller)
    : IRequestHandler<ListMyCarsQuery, IReadOnlyList<HouseholdCarDto>>
{
    public async Task<IReadOnlyList<HouseholdCarDto>> Handle(ListMyCarsQuery request, CancellationToken cancellationToken) =>
        (await cars.ListAsync(caller.RequireUserId(), cancellationToken)).Select(CarViews.From).ToList();
}

public sealed class SaveCarCommandHandler(IHouseholdCarRepository cars, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<SaveCarCommand, HouseholdCarDto>
{
    public async Task<HouseholdCarDto> Handle(SaveCarCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var make = VehicleCatalogue.Make(request.Make)!;
        var model = string.IsNullOrWhiteSpace(request.Model) ? null : VehicleCatalogue.Model(make, request.Model);
        var now = clock.GetUtcNow();

        if (request.Id is { } id)
        {
            var car = await CarViews.RequireOwnAsync(cars, userId, id, cancellationToken);
            var before = car.Nickname;
            car.Change(make, model, request.Colour, request.Nickname, now);
            await cars.SaveChangesAsync(cancellationToken);
            if (car.Nickname != before)
            {
                await cars.RenameOnPostsAsync(car.Id, car.Nickname, cancellationToken);
            }

            return CarViews.From(car);
        }

        if ((await cars.ListAsync(userId, cancellationToken)).Count >= HouseholdCar.MaxPerUser)
        {
            throw PostAccess.Invalid("Cars", $"You can add up to {HouseholdCar.MaxPerUser} cars. Delete one first.");
        }

        var added = HouseholdCar.Create(userId, make, model, request.Colour, request.Nickname, now);
        cars.Add(added);
        await cars.SaveChangesAsync(cancellationToken);
        return CarViews.From(added);
    }
}

public sealed class DeleteCarCommandHandler(IHouseholdCarRepository cars, ICurrentUser caller) : IRequestHandler<DeleteCarCommand>
{
    public async Task Handle(DeleteCarCommand request, CancellationToken cancellationToken)
    {
        var car = await CarViews.RequireOwnAsync(cars, caller.RequireUserId(), request.Id, cancellationToken);
        cars.Remove(car);
        await cars.SaveChangesAsync(cancellationToken);
    }
}
