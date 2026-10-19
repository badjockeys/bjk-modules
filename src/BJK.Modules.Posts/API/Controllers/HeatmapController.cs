using System.Text;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.UseCases.Heatmap;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Posts.API.Controllers;

/// <summary>
/// The incident heat map: aggregated, privacy-safe counts from posts' rounded pins. Signed-in users only (logged out:
/// 401). Free gets the last 7 days with no filters; Premium 12 months and filters; Premium + 5 years and CSV export.
/// </summary>
[ApiController]
[Route("api/heatmap")]
[Authorize(Policy = PostsModule.UsePolicy)]
public sealed class HeatmapController(ISender sender) : ControllerBase
{
    /// <summary>Incidents per grid cell in a box. Cells with fewer than 3 incidents are left out.</summary>
    /// <param name="bbox">minLon,minLat,maxLon,maxLat.</param>
    /// <param name="zoom">The map's zoom level (0–22): sets the cell size, about 500 m from zoom 14 to 10 km at 9 or less.</param>
    /// <param name="from">First UK day (inclusive). Default: as far back as the plan allows.</param>
    /// <param name="to">Last UK day (inclusive). Default: today.</param>
    /// <param name="categories">Comma-separated categories (Premium).</param>
    /// <param name="hours">UK time of day, e.g. 7-10 or 22-6 (Premium).</param>
    /// <param name="days">All, Weekdays or Weekends (Premium).</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet]
    [ProducesResponseType<HeatmapDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<HeatmapDto> Get(
        [FromQuery] string? bbox,
        [FromQuery] int zoom = 6,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string? categories = null,
        [FromQuery] string? hours = null,
        [FromQuery] HeatmapDays? days = null,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new GetHeatmapQuery(new HeatmapRequest(bbox, from, to, categories, hours, days), zoom), cancellationToken);

    /// <summary>
    /// Figures for an area: total, change on the previous period of the same length, busiest hour, top categories and
    /// places. Everything is hidden (<c>suppressed</c>) below 3 incidents.
    /// </summary>
    /// <param name="bbox">minLon,minLat,maxLon,maxLat.</param>
    /// <param name="from">First UK day (inclusive).</param>
    /// <param name="to">Last UK day (inclusive).</param>
    /// <param name="categories">Comma-separated categories (Premium).</param>
    /// <param name="hours">UK time of day, e.g. 7-10 (Premium).</param>
    /// <param name="days">All, Weekdays or Weekends (Premium).</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet("area")]
    [ProducesResponseType<AreaStatsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<AreaStatsDto> Area(
        [FromQuery] string? bbox,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string? categories = null,
        [FromQuery] string? hours = null,
        [FromQuery] HeatmapDays? days = null,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new GetAreaStatsQuery(new HeatmapRequest(bbox, from, to, categories, hours, days)), cancellationToken);

    /// <summary>Premium +: the heat map's cells as CSV (aggregated counts only). 403 for other plans.</summary>
    /// <param name="bbox">minLon,minLat,maxLon,maxLat.</param>
    /// <param name="zoom">Sets the cell size, as for the map.</param>
    /// <param name="from">First UK day (inclusive).</param>
    /// <param name="to">Last UK day (inclusive).</param>
    /// <param name="categories">Comma-separated categories.</param>
    /// <param name="hours">UK time of day, e.g. 7-10.</param>
    /// <param name="days">All, Weekdays or Weekends.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet("export.csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Export(
        [FromQuery] string? bbox,
        [FromQuery] int zoom = 10,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string? categories = null,
        [FromQuery] string? hours = null,
        [FromQuery] HeatmapDays? days = null,
        CancellationToken cancellationToken = default)
    {
        var csv = await sender.Send(
            new ExportHeatmapQuery(new HeatmapRequest(bbox, from, to, categories, hours, days), zoom),
            cancellationToken);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", "bad-jockeys-heatmap.csv");
    }
}
