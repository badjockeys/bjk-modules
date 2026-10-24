using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.UseCases.Dashboard;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Posts.API.Controllers;

/// <summary>
/// The member's dashboard (owner, 24 Oct 2026): the right-hand panel on desktop and the Dashboard tab on their own
/// profile on phones. The built-in cards (Category trends, Trending, Who to follow) can be moved or hidden by anyone
/// signed in; widgets (incidents in an area, one type's trend, when it happens, top roads) are Premium 3, Premium + 5.
/// Figures follow the heat map's privacy rules: rounded pins, public posts, nothing under 3 incidents.
/// </summary>
[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController(ISender sender) : ControllerBase
{
    /// <summary>Your dashboard in order, your plan's widget limit and how many you have.</summary>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<DashboardDto>(StatusCodes.Status200OK)]
    public async Task<DashboardDto> Get(CancellationToken cancellationToken) =>
        await sender.Send(new GetDashboardQuery(), cancellationToken);

    /// <summary>Adds a widget at the end. 403 on Free; 400 at the plan's limit.</summary>
    /// <param name="request">What it shows, the place, radius (1, 2, 5, 10 or 25 km), period (7, 30, 90 or 365 days) and any types.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpPost("widgets")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<DashboardDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<DashboardDto> CreateWidget(DashboardWidgetRequest request, CancellationToken cancellationToken) =>
        await sender.Send(
            new CreateDashboardWidgetCommand(request.Kind, request.PlaceName, request.Latitude, request.Longitude, request.RadiusKm, request.PeriodDays, request.Types),
            cancellationToken);

    /// <summary>Changes one of your widgets. 403 on Free.</summary>
    /// <param name="id">The widget.</param>
    /// <param name="request">Its new settings.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpPut("widgets/{id:guid}")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<DashboardDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<DashboardDto> UpdateWidget(Guid id, DashboardWidgetRequest request, CancellationToken cancellationToken) =>
        await sender.Send(
            new UpdateDashboardWidgetCommand(id, request.Kind, request.PlaceName, request.Latitude, request.Longitude, request.RadiusKm, request.PeriodDays, request.Types),
            cancellationToken);

    /// <summary>Deletes one of your widgets (any plan).</summary>
    /// <param name="id">The widget.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpDelete("widgets/{id:guid}")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<DashboardDto>(StatusCodes.Status200OK)]
    public async Task<DashboardDto> DeleteWidget(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new DeleteDashboardWidgetCommand(id), cancellationToken);

    /// <summary>Puts your dashboard in this order: every card's key once (built-in cards by kind, widgets by id).</summary>
    /// <param name="request">The keys, in the new order.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpPut("order")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<DashboardDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<DashboardDto> Reorder(DashboardOrderRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new ReorderDashboardCommand(request.Keys), cancellationToken);

    /// <summary>Hides or shows a built-in card (CategoryTrends, Trending or WhoToFollow).</summary>
    /// <param name="kind">The card.</param>
    /// <param name="request">Whether it's hidden.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpPut("cards/{kind}")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<DashboardDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<DashboardDto> SetCardHidden(DashboardKind kind, DashboardCardRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new SetDashboardCardHiddenCommand(kind, request.Hidden), cancellationToken);

    /// <summary>One of your widgets' figures. 403 on Free, or for a widget past your plan's limit.</summary>
    /// <param name="id">The widget.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet("widgets/{id:guid}/data")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<DashboardWidgetDataDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<DashboardWidgetDataDto> WidgetData(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new GetDashboardWidgetDataQuery(id), cancellationToken);

    /// <summary>The figures a widget would show, before saving it (the "Add a widget" preview). 403 on Free.</summary>
    /// <param name="request">The widget's settings.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpPost("preview")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<DashboardWidgetDataDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<DashboardWidgetDataDto> Preview(DashboardWidgetRequest request, CancellationToken cancellationToken) =>
        await sender.Send(
            new PreviewDashboardWidgetQuery(request.Kind, request.PlaceName, request.Latitude, request.Longitude, request.RadiusKm, request.PeriodDays, request.Types),
            cancellationToken);
}

/// <summary>A widget's settings.</summary>
public sealed record DashboardWidgetRequest(
    DashboardKind Kind,
    string PlaceName,
    decimal Latitude,
    decimal Longitude,
    int RadiusKm,
    int PeriodDays,
    IReadOnlyList<IncidentCategory>? Types);

/// <summary>Every card's key, in the new order.</summary>
public sealed record DashboardOrderRequest(IReadOnlyList<string> Keys);

/// <summary>Whether a built-in card is hidden.</summary>
public sealed record DashboardCardRequest(bool Hidden);
