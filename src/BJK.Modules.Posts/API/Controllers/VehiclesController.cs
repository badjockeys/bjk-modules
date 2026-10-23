using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.UseCases.Cars;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Posts.API.Controllers;

/// <summary>A make and its models, as listed.</summary>
public sealed record VehicleMakeDto(string Name, IReadOnlyList<string> Models);

/// <summary>The lists a post's vehicle details are chosen from (and the heat map's vehicle filters use).</summary>
public sealed record VehicleCatalogueDto(
    IReadOnlyList<VehicleType> Types,
    IReadOnlyList<VehicleColour> Colours,
    IReadOnlyList<VehicleMakeDto> Makes);

/// <summary>Vehicle lists. Anyone can call this; it's the same for everyone and can be cached.</summary>
[ApiController]
[Route("api/vehicles")]
public sealed class VehiclesController(ISender sender) : ControllerBase
{
    private static readonly VehicleCatalogueDto Catalogue = new(
        Enum.GetValues<VehicleType>(),
        Enum.GetValues<VehicleColour>(),
        VehicleCatalogue.Makes.Select(m => new VehicleMakeDto(m.Make, m.Models)).ToList());

    /// <summary>Vehicle types, colours, and makes with their models.</summary>
    [HttpGet]
    [Authorize(Policy = PostsModule.ReadPolicy)]
    [ResponseCache(Duration = 86400)]
    [ProducesResponseType<VehicleCatalogueDto>(StatusCodes.Status200OK)]
    public VehicleCatalogueDto Get() => Catalogue;

    /// <summary>Your household cars, to choose the one you filmed from when posting. Private to you.</summary>
    [HttpGet("mine")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<IReadOnlyList<HouseholdCarDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<HouseholdCarDto>> MyCars(CancellationToken cancellationToken) =>
        await sender.Send(new ListMyCarsQuery(), cancellationToken);

    /// <summary>Add a household car: a make and model from the lists, a colour and a nickname (no registration).</summary>
    [HttpPost("mine")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<HouseholdCarDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<HouseholdCarDto> AddCar(HouseholdCarRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new SaveCarCommand(null, request.Make, request.Model, request.Colour, request.Nickname), cancellationToken);

    /// <summary>Change one of your cars. A new nickname shows on the posts filmed from it.</summary>
    [HttpPut("mine/{id:guid}")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<HouseholdCarDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<HouseholdCarDto> UpdateCar(Guid id, HouseholdCarRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new SaveCarCommand(id, request.Make, request.Model, request.Colour, request.Nickname), cancellationToken);

    /// <summary>Delete one of your cars. Posts filmed from it keep its nickname.</summary>
    [HttpDelete("mine/{id:guid}")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCar(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteCarCommand(id), cancellationToken);
        return NoContent();
    }
}

/// <summary>A household car: a make (and model) from <c>GET /api/vehicles</c>, a colour, and a nickname.</summary>
public sealed record HouseholdCarRequest(string Make, string? Model, VehicleColour? Colour, string? Nickname);
