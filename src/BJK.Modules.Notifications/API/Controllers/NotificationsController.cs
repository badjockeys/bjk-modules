using BJK.Modules.Notifications.UseCases;
using BJK.Modules.Shared;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Notifications.API.Controllers;

/// <summary>The signed-in user's notifications (the bell) and notification settings.</summary>
[ApiController]
[Route("api/notifications")]
[Authorize(Policy = NotificationsModule.UsePolicy)]
public sealed class NotificationsController(ISender sender) : ControllerBase
{
    /// <summary>Your notifications, newest first.</summary>
    /// <param name="page">Page number, from 1.</param>
    /// <param name="pageSize">Up to 50.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet]
    [ProducesResponseType<PagedResult<NotificationDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<NotificationDto>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListNotificationsQuery(page, pageSize), cancellationToken);

    /// <summary>
    /// How many are unread, for the bell's badge. Also keeps the email address notifications go to up to date with
    /// your sign-in account (only a confirmed address is used).
    /// </summary>
    [HttpGet("unread-count")]
    [ProducesResponseType<UnreadCountDto>(StatusCodes.Status200OK)]
    public async Task<UnreadCountDto> UnreadCount(CancellationToken cancellationToken) =>
        await sender.Send(new GetUnreadCountQuery(), cancellationToken);

    /// <summary>Mark notifications read: the ids given, or all of them if there are none.</summary>
    [HttpPost("read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MarkRead(MarkNotificationsReadCommand command, CancellationToken cancellationToken)
    {
        await sender.Send(command, cancellationToken);
        return NoContent();
    }

    /// <summary>Your settings for each type of notification: in the bell, and by email.</summary>
    [HttpGet("settings")]
    [ProducesResponseType<IReadOnlyList<NotificationSettingDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<NotificationSettingDto>> Settings(CancellationToken cancellationToken) =>
        await sender.Send(new GetNotificationSettingsQuery(), cancellationToken);

    /// <summary>Change your settings for the types given.</summary>
    [HttpPut("settings")]
    [ProducesResponseType<IReadOnlyList<NotificationSettingDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IReadOnlyList<NotificationSettingDto>> UpdateSettings(
        UpdateNotificationSettingsCommand command,
        CancellationToken cancellationToken) =>
        await sender.Send(command, cancellationToken);
}
