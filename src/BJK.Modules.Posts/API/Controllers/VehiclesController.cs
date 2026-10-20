using BJK.Modules.Posts.Domain;
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
public sealed class VehiclesController : ControllerBase
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
}
