using BJK.Modules.Posts.UseCases.Hashtags;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Posts.API.Controllers;

/// <summary>
/// Hashtags: suggestions as # is typed, and a tag's page. Anyone can call these. A tag's posts are
/// <c>GET /api/posts?hashtag=</c>. Tags come from posts' titles and descriptions only, never comments.
/// </summary>
[ApiController]
[Route("api/hashtags")]
[Authorize(Policy = PostsModule.ReadPolicy)]
public sealed class HashtagsController(ISender sender) : ControllerBase
{
    /// <summary>Tags starting with what's been typed after the # (any case), most used first.</summary>
    /// <param name="prefix">A letter, then letters, numbers or underscores, e.g. <c>m6</c>.</param>
    /// <param name="limit">How many, 1 to 10 (default 8).</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<HashtagSuggestionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IReadOnlyList<HashtagSuggestionDto>> Suggest(
        [FromQuery] string? prefix,
        [FromQuery] int limit = 8,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new SuggestHashtagsQuery(prefix, limit), cancellationToken);

    /// <summary>A tag as first written and how many posts have used it. 404 if no post has.</summary>
    /// <param name="tag">The tag, with or without the # (any case).</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet("{tag}")]
    [ProducesResponseType<HashtagDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<HashtagDto> Get(string tag, CancellationToken cancellationToken) =>
        await sender.Send(new GetHashtagQuery(tag), cancellationToken);
}
