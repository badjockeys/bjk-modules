using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Communities;
using BJK.Modules.Posts.UseCases.Reports;
using BJK.Modules.Shared;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Posts.API.Controllers;

/// <summary>
/// Communities (owner, 23 Oct 2026): people around a place or a topic, with posts shared into them and text
/// discussions. Premium members start them and are their admins. Public (anyone joins and sees inside) or private
/// (listed; people ask to join, and only members see inside). Under-18s use public communities only and are never
/// admins or moderators. Signed-in members only.
/// </summary>
[ApiController]
[Route("api/communities")]
[Authorize(Policy = PostsModule.UsePolicy)]
public sealed class CommunitiesController(ISender sender) : ControllerBase
{
    /// <summary>Communities near you (covering your home area), yours, about topics (no area), or matching a search.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<CommunitySummaryDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<CommunitySummaryDto>> List(
        [FromQuery] CommunityListKind kind = CommunityListKind.Near,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListCommunitiesQuery(kind, search, page, pageSize), cancellationToken);

    /// <summary>Start a community (Premium and Premium +, adults, up to 5). You're its admin.</summary>
    [HttpPost]
    [ProducesResponseType<CommunityDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(CommunityRequest request, CancellationToken cancellationToken)
    {
        var created = await sender.Send(
            new CreateCommunityCommand(request.Name, request.Description, request.Rules, request.Visibility, request.PlaceName, request.Latitude, request.Longitude, request.RadiusKm),
            cancellationToken);
        return CreatedAtAction(nameof(Get), new { slug = created.Slug }, created);
    }

    /// <summary>
    /// Your communities to share a post into, those covering where it happened first (recommended). Give the place, or
    /// an existing post of yours (which also marks the communities it's in).
    /// </summary>
    [HttpGet("suggestions")]
    [ProducesResponseType<IReadOnlyList<CommunitySuggestionDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<CommunitySuggestionDto>> Suggestions(
        [FromQuery] decimal? latitude = null,
        [FromQuery] decimal? longitude = null,
        [FromQuery] Guid? postId = null,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new SuggestCommunitiesQuery(latitude, longitude, postId), cancellationToken);

    /// <summary>One community, with what you may do in it.</summary>
    [HttpGet("{slug}")]
    [ProducesResponseType<CommunityDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<CommunityDto> Get(string slug, CancellationToken cancellationToken) =>
        await sender.Send(new GetCommunityQuery(slug), cancellationToken);

    /// <summary>Change a community's details (admins). Its address stays the same.</summary>
    [HttpPut("{slug}")]
    [ProducesResponseType<CommunityDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<CommunityDto> Update(string slug, CommunityRequest request, CancellationToken cancellationToken) =>
        await sender.Send(
            new UpdateCommunityCommand(slug, request.Name, request.Description, request.Rules, request.Visibility, request.PlaceName, request.Latitude, request.Longitude, request.RadiusKm),
            cancellationToken);

    /// <summary>Delete a community for everyone (admins). Posts shared into it stay on Bad Jockeys.</summary>
    [HttpDelete("{slug}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete(string slug, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteCommunityCommand(slug), cancellationToken);
        return NoContent();
    }

    /// <summary>Join a public community, or ask to join a private one (adults only).</summary>
    [HttpPost("{slug}/join")]
    [ProducesResponseType<CommunityDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<CommunityDto> Join(string slug, CancellationToken cancellationToken) =>
        await sender.Send(new JoinCommunityCommand(slug), cancellationToken);

    /// <summary>Leave, or withdraw your request to join.</summary>
    [HttpDelete("{slug}/membership")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Leave(string slug, CancellationToken cancellationToken)
    {
        await sender.Send(new LeaveCommunityCommand(slug), cancellationToken);
        return NoContent();
    }

    /// <summary>Members (anyone who can see inside), or requests and bans (admins and moderators).</summary>
    [HttpGet("{slug}/members")]
    [ProducesResponseType<PagedResult<CommunityMemberDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<PagedResult<CommunityMemberDto>> Members(
        string slug,
        [FromQuery] CommunityMembershipStatus status = CommunityMembershipStatus.Member,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 30,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListCommunityMembersQuery(slug, status, page, pageSize), cancellationToken);

    /// <summary>Approve or decline someone's request to join (admins and moderators).</summary>
    [HttpPost("{slug}/requests/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DecideRequest(string slug, Guid userId, CommunityRequestDecision request, CancellationToken cancellationToken)
    {
        await sender.Send(new DecideCommunityRequestCommand(slug, userId, request.Approve), cancellationToken);
        return NoContent();
    }

    /// <summary>Make a member an admin, a moderator or a member (admins). Admins and moderators must be adults.</summary>
    [HttpPut("{slug}/members/{userId:guid}/role")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SetRole(string slug, Guid userId, CommunityRoleRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new SetCommunityRoleCommand(slug, userId, request.Role), cancellationToken);
        return NoContent();
    }

    /// <summary>Remove a member, optionally banning them (admins and moderators).</summary>
    [HttpDelete("{slug}/members/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveMember(string slug, Guid userId, [FromQuery] bool ban = false, CancellationToken cancellationToken = default)
    {
        await sender.Send(new RemoveCommunityMemberCommand(slug, userId, ban), cancellationToken);
        return NoContent();
    }

    /// <summary>Lift a ban (admins and moderators).</summary>
    [HttpDelete("{slug}/bans/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Unban(string slug, Guid userId, CancellationToken cancellationToken)
    {
        await sender.Send(new UnbanCommunityMemberCommand(slug, userId), cancellationToken);
        return NoContent();
    }

    /// <summary>Posts shared into the community, newest share first.</summary>
    [HttpGet("{slug}/posts")]
    [ProducesResponseType<PagedResult<PostDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<PagedResult<PostDto>> Posts(string slug, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default) =>
        await sender.Send(new ListCommunityPostsQuery(slug, page, pageSize), cancellationToken);

    /// <summary>Take a post out of the community (whoever shared it, or admins and moderators).</summary>
    [HttpDelete("{slug}/posts/{postId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Unshare(string slug, Guid postId, CancellationToken cancellationToken)
    {
        await sender.Send(new UnsharePostCommand(slug, postId), cancellationToken);
        return NoContent();
    }

    /// <summary>The community's discussions, most recently active first.</summary>
    [HttpGet("{slug}/discussions")]
    [ProducesResponseType<PagedResult<CommunityDiscussionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<PagedResult<CommunityDiscussionDto>> Discussions(string slug, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default) =>
        await sender.Send(new ListCommunityDiscussionsQuery(slug, page, pageSize), cancellationToken);

    /// <summary>Start a discussion (members).</summary>
    [HttpPost("{slug}/discussions")]
    [ProducesResponseType<CommunityDiscussionDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> StartDiscussion(string slug, DiscussionRequest request, CancellationToken cancellationToken)
    {
        var discussion = await sender.Send(new StartCommunityDiscussionCommand(slug, request.Title, request.Body), cancellationToken);
        return CreatedAtAction(nameof(Discussion), new { slug, discussionId = discussion.Id }, discussion);
    }

    /// <summary>A discussion and a page of its replies, oldest first.</summary>
    [HttpGet("{slug}/discussions/{discussionId:guid}")]
    [ProducesResponseType<CommunityDiscussionDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<CommunityDiscussionDetailDto> Discussion(string slug, Guid discussionId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default) =>
        await sender.Send(new GetCommunityDiscussionQuery(slug, discussionId, page, pageSize), cancellationToken);

    /// <summary>Remove a discussion (its author, or admins and moderators).</summary>
    [HttpDelete("{slug}/discussions/{discussionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveDiscussion(string slug, Guid discussionId, CancellationToken cancellationToken)
    {
        await sender.Send(new RemoveCommunityDiscussionCommand(slug, discussionId), cancellationToken);
        return NoContent();
    }

    /// <summary>Reply to a discussion (members).</summary>
    [HttpPost("{slug}/discussions/{discussionId:guid}/replies")]
    [ProducesResponseType<CommunityReplyDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Reply(string slug, Guid discussionId, DiscussionReplyRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await sender.Send(new ReplyToCommunityDiscussionCommand(slug, discussionId, request.Body), cancellationToken));

    /// <summary>Remove a reply (its author, or admins and moderators).</summary>
    [HttpDelete("{slug}/discussions/{discussionId:guid}/replies/{replyId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveReply(string slug, Guid discussionId, Guid replyId, CancellationToken cancellationToken)
    {
        await sender.Send(new RemoveCommunityReplyCommand(slug, discussionId, replyId), cancellationToken);
        return NoContent();
    }

    /// <summary>Report a community (its name, description or rules) to Bad Jockeys' moderators.</summary>
    [HttpPost("{slug}/report")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Report(string slug, ReportRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new ReportCommunityCommand(slug, request.Reason, request.Details), cancellationToken);
        return NoContent();
    }

    /// <summary>Report a discussion to Bad Jockeys' moderators.</summary>
    [HttpPost("discussions/{discussionId:guid}/report")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ReportDiscussion(Guid discussionId, ReportRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new ReportCommunityDiscussionCommand(discussionId, request.Reason, request.Details), cancellationToken);
        return NoContent();
    }

    /// <summary>Report a reply to Bad Jockeys' moderators.</summary>
    [HttpPost("replies/{replyId:guid}/report")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ReportReply(Guid replyId, ReportRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new ReportCommunityReplyCommand(replyId, request.Reason, request.Details), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Share your own post into communities you're a member of (offered when posting). It stays public everywhere.
    /// Returns the communities it's in now.
    /// </summary>
    [HttpPost("/api/posts/{postId:guid}/communities")]
    [ProducesResponseType<IReadOnlyList<Guid>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IReadOnlyList<Guid>> SharePost(Guid postId, SharePostRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new SharePostToCommunitiesCommand(postId, request.CommunityIds), cancellationToken);
}

public sealed record CommunityRequest(
    string Name,
    string? Description,
    string? Rules,
    CommunityVisibility Visibility,
    string? PlaceName = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    int? RadiusKm = null);

public sealed record CommunityRequestDecision(bool Approve);

public sealed record CommunityRoleRequest(CommunityRole Role);

public sealed record DiscussionRequest(string Title, string? Body);

public sealed record DiscussionReplyRequest(string Body);

public sealed record SharePostRequest(IReadOnlyList<Guid> CommunityIds);
