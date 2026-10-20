using BJK.Modules.Notifications.Infrastructure;
using BJK.Modules.Notifications.UseCases;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Notifications.API.Controllers;

/// <summary>Your home area (private, to about 1 km) and Premium area alerts.</summary>
[ApiController]
[Route("api/notifications/area")]
[Authorize(Policy = NotificationsModule.UsePolicy)]
public sealed class AreaController(ISender sender) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<AreaSettingsDto>(StatusCodes.Status200OK)]
    public async Task<AreaSettingsDto> Get(CancellationToken cancellationToken) =>
        await sender.Send(new GetAreaSettingsQuery(), cancellationToken);

    /// <summary>Set your home area: a place from <c>/api/places</c>.</summary>
    [HttpPut("home")]
    [ProducesResponseType<AreaSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<AreaSettingsDto> SetHome(SetHomeAreaCommand command, CancellationToken cancellationToken) =>
        await sender.Send(command, cancellationToken);

    /// <summary>Area alerts: on or off (Premium), radius, Digest or Instant, and email.</summary>
    [HttpPut("alerts")]
    [ProducesResponseType<AreaSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<AreaSettingsDto> SetAlerts(SetAreaAlertsCommand command, CancellationToken cancellationToken) =>
        await sender.Send(command, cancellationToken);

    /// <summary>Forget your home area (alerts stop).</summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Clear(CancellationToken cancellationToken)
    {
        await sender.Send(new ClearHomeAreaCommand(), cancellationToken);
        return NoContent();
    }
}

/// <summary>UK places, for choosing a home area or where an incident happened. Signed-in users.</summary>
[ApiController]
[Route("api/places")]
[Authorize(Policy = NotificationsModule.UsePolicy)]
public sealed class PlacesController(ISender sender) : ControllerBase
{
    /// <summary>Towns, villages, postcodes or postcode areas matching <paramref name="q"/>, positions to about 1 km.</summary>
    /// <param name="q">At least 2 characters, e.g. "Rainhill", "WA10" or "WA10 1AA".</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PlaceDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IReadOnlyList<PlaceDto>> Search([FromQuery] string q, CancellationToken cancellationToken) =>
        await sender.Send(new SearchPlacesQuery(q), cancellationToken);
}
