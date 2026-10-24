using BJK.Modules.Chat.UseCases;
using BJK.Modules.Shared.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Chat.API.Controllers;

/// <summary>Ask AI: questions about reported bad driving, answered from the incident data.</summary>
[ApiController]
[Route("api/chat")]
public sealed class ChatController(ISender sender) : ControllerBase
{
    /// <summary>
    /// Ask a question. Premium and Premium + only (403 for Free). Counts against the day's allowance: 403 with
    /// <c>code</c> = <c>daily_limit</c> once it's used up; 503 when the model can't be reached (the question doesn't
    /// count). Send the window's earlier turns in <c>history</c>; nothing is stored.
    /// </summary>
    [HttpPost("questions")]
    [Authorize(Policy = ModuleAuthentication.SignedInUserPolicy)]
    [ProducesResponseType<ChatReplyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ChatReplyDto> Ask(AskQuestionCommand command, CancellationToken cancellationToken) =>
        await sender.Send(command, cancellationToken);

    /// <summary>
    /// The caller's allowance today and example questions. Anyone may call: Free and logged-out callers get
    /// <c>canAsk</c> false, for the locked preview.
    /// </summary>
    [HttpGet("usage")]
    [Authorize(Policy = ChatModule.ReadPolicy)]
    [ProducesResponseType<ChatUsageDto>(StatusCodes.Status200OK)]
    public async Task<ChatUsageDto> GetUsage(CancellationToken cancellationToken) =>
        await sender.Send(new GetChatUsageQuery(), cancellationToken);
}
