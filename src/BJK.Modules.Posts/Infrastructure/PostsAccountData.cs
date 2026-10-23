using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Users;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure;

/// <summary>Hides a deactivated user's posts and comments, shows them again on return, and deletes them with the account.</summary>
internal sealed class PostsUserLifecycleHandler(
    IPostRepository posts,
    IDraftRepository drafts,
    ITimelineRepository? timelines = null,
    ICommunityRepository? communities = null,
    TimeProvider? clock = null,
    IHouseholdCarRepository? cars = null)
    : IUserLifecycleHandler
{
    public async Task HandleAsync(UserLifecycleEvent userEvent, CancellationToken cancellationToken)
    {
        switch (userEvent.Type)
        {
            case UserLifecycleEventType.Deactivated:
                await posts.SetAuthorDeactivatedAsync(userEvent.UserId, true, cancellationToken);
                break;
            case UserLifecycleEventType.Reactivated:
                await posts.SetAuthorDeactivatedAsync(userEvent.UserId, false, cancellationToken);
                break;
            case UserLifecycleEventType.Deleted:
                // Their uploads, drafts' included, are deleted by the Media module's own handler.
                await drafts.DeleteAllForUserAsync(userEvent.UserId, cancellationToken);
                if (timelines is not null)
                {
                    await timelines.DeleteForUserAsync(userEvent.UserId, cancellationToken);
                }

                if (communities is not null)
                {
                    await communities.DeleteAllForUserAsync(userEvent.UserId, (clock ?? TimeProvider.System).GetUtcNow(), cancellationToken);
                }

                if (cars is not null)
                {
                    await cars.DeleteAllForUserAsync(userEvent.UserId, cancellationToken);
                }

                await posts.DeleteAllForUserAsync(userEvent.UserId, cancellationToken);
                break;
        }
    }
}

/// <summary>The user's posts (including hidden and deleted ones still held), comments, reactions, reports and reel plays.</summary>
internal sealed class PostsPersonalDataExporter(PostsDbContext db) : IPersonalDataExporter
{
    public string Section => "posts";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken cancellationToken)
    {
        var posts = await db.Posts.AsNoTracking()
            .Where(p => p.AuthorId == userId)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new
            {
                p.Id,
                p.Title,
                p.Description,
                p.Category,
                p.TypeFlags,
                p.VehicleType,
                p.VehicleMake,
                p.VehicleModel,
                p.VehicleColour,
                p.ReportedToPolice,
                p.FilmedFrom,
                p.IncidentAt,
                p.Latitude,
                p.Longitude,
                p.PlaceName,
                p.Status,
                p.IsSensitive,
                p.HiddenReason,
                p.MediaState,
                p.MediaFailureReason,
                p.NotifyEmail,
                p.CreatedAt,
                p.UpdatedAt,
                p.DeletedAt,
                MediaIds = p.Media.OrderBy(m => m.Position).Select(m => m.MediaId).ToList(),
            })
            .ToListAsync(cancellationToken);

        var comments = await db.Comments.AsNoTracking()
            .Where(c => c.AuthorId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { c.Id, c.PostId, c.ParentCommentId, c.Body, c.Status, c.CreatedAt, c.UpdatedAt, c.DeletedAt })
            .ToListAsync(cancellationToken);

        var reactions = await db.Reactions.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new { r.PostId, r.Type, r.CreatedAt })
            .ToListAsync(cancellationToken);

        var commentReactions = await db.CommentReactions.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new { r.CommentId, r.Type, r.CreatedAt })
            .ToListAsync(cancellationToken);

        var reports = await db.Reports.AsNoTracking()
            .Where(r => r.ReporterId == userId)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new { r.Id, r.TargetType, r.TargetId, r.PostId, r.Reason, r.Details, r.ContactEmail, r.Status, r.CreatedAt, r.ResolvedAt })
            .ToListAsync(cancellationToken);

        var appeals = await db.Appeals.AsNoTracking()
            .Where(a => a.AppellantId == userId)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.TargetType, a.TargetId, a.PostId, a.Statement, a.ContactEmail, a.Status, a.CreatedAt, a.DecidedAt })
            .ToListAsync(cancellationToken);

        // Reels watched (kept 35 days), for the daily limit.
        var reelPlays = await db.ReelPlays.AsNoTracking()
            .Where(p => p.ViewerKey == userId && !p.IsAnonymous)
            .OrderBy(p => p.PlayedAt)
            .Select(p => new { p.PostId, p.Day, p.PlayedAt })
            .ToListAsync(cancellationToken);

        // Posts the user was tagged on as a passenger, and their answer.
        var passengerTags = await db.PostPassengers.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.TaggedAt)
            .Select(t => new { t.PostId, Status = t.Status.ToString(), t.TaggedAt, t.RespondedAt })
            .ToListAsync(cancellationToken);

        // Drafts not yet posted.
        var drafts = (await db.PostDrafts.AsNoTracking()
                .Include(d => d.Media)
                .Where(d => d.AuthorId == userId)
                .OrderBy(d => d.CreatedAt)
                .ToListAsync(cancellationToken))
            .Select(d => new
            {
                d.Id,
                d.Title,
                d.Description,
                Types = d.Types,
                d.IncidentAt,
                d.Latitude,
                d.Longitude,
                d.PlaceName,
                d.IsSensitive,
                d.Passengers,
                d.VehicleType,
                d.VehicleMake,
                d.VehicleModel,
                d.VehicleColour,
                d.ReportedToPolice,
                MediaIds = d.Media.OrderBy(m => m.Position).Select(m => m.MediaId).ToList(),
                d.CreatedAt,
                d.UpdatedAt,
            })
            .ToList();

        // Every type of bad driving on each post, by name.
        var postTypes = posts.Select(p => new { PostId = p.Id, Types = IncidentTypeFlags.ToList(p.TypeFlags, p.Category) }).ToList();

        // Custom timelines (their place is the owner's own choice).
        var timelines = (await db.Timelines.AsNoTracking()
                .Where(t => t.UserId == userId)
                .OrderBy(t => t.Position)
                .ToListAsync(cancellationToken))
            .Select(t => new { t.Id, t.Name, Types = t.Types.Select(x => x.ToString()).ToList(), t.PlaceName, t.Latitude, t.Longitude, t.RadiusKm, t.CreatedAt })
            .ToList();

        var votes = await db.PostVotes.AsNoTracking()
            .Where(v => v.UserId == userId)
            .OrderBy(v => v.CreatedAt)
            .Select(v => new { v.PostId, Choice = v.Choice.ToString(), v.CreatedAt, v.UpdatedAt })
            .ToListAsync(cancellationToken);
        var followedPosts = await db.PostFollows.AsNoTracking()
            .Where(f => f.UserId == userId)
            .OrderBy(f => f.CreatedAt)
            .Select(f => new { f.PostId, f.CreatedAt })
            .ToListAsync(cancellationToken);
        var bookmarks = await db.Bookmarks.AsNoTracking()
            .Where(b => b.UserId == userId)
            .OrderBy(b => b.CreatedAt)
            .Select(b => new { b.PostId, b.CreatedAt })
            .ToListAsync(cancellationToken);
        var postsViewed = await db.PostViews.AsNoTracking()
            .Where(v => v.ViewerKey == userId)
            .OrderBy(v => v.ViewedAt)
            .Select(v => new { v.PostId, v.ViewedAt })
            .ToListAsync(cancellationToken);
        var householdCars = await db.HouseholdCars.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { c.Id, c.Make, c.Model, c.Colour, c.Nickname, c.CreatedAt })
            .ToListAsync(cancellationToken);
        var preferences = await db.AuthorPreferences.AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => new { VoteDuration = a.VoteDuration.ToString(), a.UpdatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        return new
        {
            posts, postTypes, drafts, passengerTags, comments, reactions, commentReactions, reportsMade = reports, appeals,
            reelPlays, timelines, votes, followedPosts, bookmarks, postsViewed, postPreferences = preferences, householdCars,
        };
    }
}

/// <summary>The user's communities (memberships and roles), discussions and replies.</summary>
internal sealed class CommunitiesPersonalDataExporter(PostsDbContext db) : IPersonalDataExporter
{
    public string Section => "communities";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken cancellationToken) => new
    {
        Memberships = await (from m in db.CommunityMembers.AsNoTracking()
                             join c in db.Communities.AsNoTracking() on m.CommunityId equals c.Id
                             where m.UserId == userId
                             select new { c.Name, c.Slug, m.Status, m.Role, m.RequestedAt, m.JoinedAt, Owner = c.OwnerId == userId })
            .ToListAsync(cancellationToken),
        Discussions = await db.CommunityDiscussions.AsNoTracking()
            .Where(d => d.AuthorId == userId)
            .Select(d => new { d.Id, d.CommunityId, d.Title, d.Body, d.Status, d.CreatedAt, d.RemovedAt })
            .ToListAsync(cancellationToken),
        Replies = await db.CommunityReplies.AsNoTracking()
            .Where(r => r.AuthorId == userId)
            .Select(r => new { r.Id, r.DiscussionId, r.Body, r.Status, r.CreatedAt, r.RemovedAt })
            .ToListAsync(cancellationToken),
        SharedPosts = await db.CommunityPosts.AsNoTracking()
            .Where(s => s.SharedBy == userId)
            .Select(s => new { s.CommunityId, s.PostId, s.SharedAt })
            .ToListAsync(cancellationToken),
    };
}
