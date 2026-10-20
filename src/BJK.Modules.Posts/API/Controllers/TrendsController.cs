using BJK.Modules.Posts.UseCases.Hashtags;
using BJK.Modules.Posts.UseCases.Heatmap;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Posts.API.Controllers;

/// <summary>Trends for the right-hand panel. Anyone can call these; what's shown depends on the plan.</summary>
[ApiController]
[Route("api/trends")]
[Authorize(Policy = PostsModule.ReadPolicy)]
public sealed class TrendsController(ISender sender) : ControllerBase
{
    /// <summary>
    /// What's trending: up to 10 hashtags and 5 towns with the most public posts in the last 24 hours compared with
    /// their usual level, each from at least 3 different people. Hidden, held and deleted posts never count.
    /// </summary>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet]
    [ProducesResponseType<TrendingDto>(StatusCodes.Status200OK)]
    public async Task<TrendingDto> Trending(CancellationToken cancellationToken = default) =>
        await sender.Send(new GetTrendingQuery(), cancellationToken);

    /// <summary>
    /// Every type of bad driving, busiest first, with a line of incidents per day (per week beyond 92 days, per month
    /// beyond two years), its total and the change on the previous period of the same length. Free and signed-out: the
    /// last 7 days. Premium: up to 12 months; Premium +: up to 5 years. Everyone sees the whole UK; Premium and Premium +
    /// can narrow it to an area with <paramref name="bbox"/>. Types with fewer than 3 incidents are suppressed.
    /// </summary>
    /// <param name="from">First UK day (inclusive). Default: as far back as the plan allows.</param>
    /// <param name="to">Last UK day (inclusive). Default: today.</param>
    /// <param name="bbox">minLon,minLat,maxLon,maxLat (Premium); ignored for other plans.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet("categories")]
    [ProducesResponseType<CategoryTrendsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<CategoryTrendsDto> Categories(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string? bbox = null,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new GetCategoryTrendsQuery(bbox, from, to), cancellationToken);
}
