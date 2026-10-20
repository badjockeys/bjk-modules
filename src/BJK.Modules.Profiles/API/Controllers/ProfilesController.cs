using BJK.Modules.Profiles.UseCases;
using BJK.Modules.Profiles.UseCases.Blocks;
using BJK.Modules.Profiles.UseCases.Create;
using BJK.Modules.Profiles.UseCases.Follows;
using BJK.Modules.Profiles.UseCases.Get;
using BJK.Modules.Profiles.UseCases.Update;
using BJK.Modules.Shared;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Profiles.API.Controllers;

[ApiController]
[Route("api/profiles")]
public sealed class ProfilesController(ISender sender) : ControllerBase
{
    /// <summary>Create the signed-in user's profile. They need one before they can post or comment.</summary>
    [HttpPost]
    [Authorize(Policy = ProfilesModule.UsePolicy)]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreateProfileCommand command, CancellationToken cancellationToken)
    {
        var profile = await sender.Send(command, cancellationToken);
        return CreatedAtAction(nameof(GetByHandle), new { handle = profile.Handle }, profile);
    }

    /// <summary>The signed-in user's profile. 404 if they haven't created one yet.</summary>
    [HttpGet("me")]
    [Authorize(Policy = ProfilesModule.UsePolicy)]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ProfileDto> GetMine(CancellationToken cancellationToken) =>
        await sender.Send(new GetMyProfileQuery(), cancellationToken);

    [HttpPut("me")]
    [Authorize(Policy = ProfilesModule.UsePolicy)]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ProfileDto> Update(UpdateProfileCommand command, CancellationToken cancellationToken) =>
        await sender.Send(command, cancellationToken);

    [HttpPut("me/handle")]
    [Authorize(Policy = ProfilesModule.UsePolicy)]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ProfileDto> ChangeHandle(ChangeHandleCommand command, CancellationToken cancellationToken) =>
        await sender.Send(command, cancellationToken);

    /// <summary>Set the avatar to one of your Ready photos (upload it through /api/media first), or send null to remove it.</summary>
    [HttpPut("me/avatar")]
    [Authorize(Policy = ProfilesModule.UsePolicy)]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ProfileDto> SetAvatar(SetAvatarCommand command, CancellationToken cancellationToken) =>
        await sender.Send(command, cancellationToken);

    /// <summary>
    /// Say how you use the road (Driver, Cyclist or Both, or null to not say). Optional and private unless
    /// <c>show</c> is true, when it appears on your profile and posts.
    /// </summary>
    [HttpPut("me/road-user")]
    [Authorize(Policy = ProfilesModule.UsePolicy)]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ProfileDto> SetRoadUser(SetRoadUserCommand command, CancellationToken cancellationToken) =>
        await sender.Send(command, cancellationToken);

    /// <summary>Everyone you've blocked, most recent first.</summary>
    [HttpGet("me/blocks")]
    [Authorize(Policy = ProfilesModule.UsePolicy)]
    [ProducesResponseType<IReadOnlyList<BlockDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<BlockDto>> ListBlocks(CancellationToken cancellationToken) =>
        await sender.Send(new ListMyBlocksQuery(), cancellationToken);

    /// <summary>
    /// Block someone, by @handle or user id. You won't see their posts or comments, and they can't comment on or react to yours.
    /// </summary>
    [HttpPut("me/blocks/{user}")]
    [Authorize(Policy = ProfilesModule.UsePolicy)]
    [ProducesResponseType<BlockDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<BlockDto> Block(string user, CancellationToken cancellationToken) =>
        await sender.Send(new BlockUserCommand(user), cancellationToken);

    /// <summary>Unblock someone, by @handle or user id.</summary>
    [HttpDelete("me/blocks/{user}")]
    [Authorize(Policy = ProfilesModule.UsePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unblock(string user, CancellationToken cancellationToken)
    {
        await sender.Send(new UnblockUserCommand(user), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Follow someone, by @handle or user id: their posts show in your Following timeline. You can't follow across a
    /// block. Needs your own profile.
    /// </summary>
    [HttpPut("me/following/{user}")]
    [Authorize(Policy = ProfilesModule.UsePolicy)]
    [ProducesResponseType<FollowStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<FollowStateDto> Follow(string user, CancellationToken cancellationToken) =>
        await sender.Send(new FollowUserCommand(user), cancellationToken);

    /// <summary>Stop following someone, by @handle or user id.</summary>
    [HttpDelete("me/following/{user}")]
    [Authorize(Policy = ProfilesModule.UsePolicy)]
    [ProducesResponseType<FollowStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<FollowStateDto> Unfollow(string user, CancellationToken cancellationToken) =>
        await sender.Send(new UnfollowUserCommand(user), cancellationToken);

    /// <summary>
    /// Who to follow: up to <paramref name="limit"/> adults whose home area is near yours, nearest first. Empty, with
    /// <c>hasHomeArea</c> false, until you choose a home area.
    /// </summary>
    [HttpGet("me/suggestions")]
    [Authorize(Policy = ProfilesModule.UsePolicy)]
    [ProducesResponseType<WhoToFollowDto>(StatusCodes.Status200OK)]
    public async Task<WhoToFollowDto> Suggestions([FromQuery] int limit = 3, CancellationToken cancellationToken = default) =>
        await sender.Send(new WhoToFollowQuery(limit), cancellationToken);

    /// <summary>Someone's followers, most recent first. Anyone can call this.</summary>
    [HttpGet("{handle}/followers")]
    [Authorize(Policy = ProfilesModule.ReadPolicy)]
    [ProducesResponseType<PagedResult<FollowProfileDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<PagedResult<FollowProfileDto>> Followers(
        string handle,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListFollowsQuery(handle, Followers: true, page, pageSize), cancellationToken);

    /// <summary>The people someone follows, most recent first. Anyone can call this.</summary>
    [HttpGet("{handle}/following")]
    [Authorize(Policy = ProfilesModule.ReadPolicy)]
    [ProducesResponseType<PagedResult<FollowProfileDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<PagedResult<FollowProfileDto>> Following(
        string handle,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListFollowsQuery(handle, Followers: false, page, pageSize), cancellationToken);

    /// <summary>Whether a handle is free. Anyone can call this.</summary>
    [HttpGet("handles/{handle}/availability")]
    [Authorize(Policy = ProfilesModule.ReadPolicy)]
    [ProducesResponseType<HandleAvailability>(StatusCodes.Status200OK)]
    public async Task<HandleAvailability> CheckHandle(string handle, CancellationToken cancellationToken) =>
        await sender.Send(new CheckHandleQuery(handle), cancellationToken);

    /// <summary>Anyone's public profile. Anyone can call this.</summary>
    [HttpGet("{handle}")]
    [Authorize(Policy = ProfilesModule.ReadPolicy)]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ProfileDto> GetByHandle(string handle, CancellationToken cancellationToken) =>
        await sender.Send(new GetProfileQuery(handle), cancellationToken);
}
