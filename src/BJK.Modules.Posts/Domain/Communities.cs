using System.Text;

namespace BJK.Modules.Posts.Domain;

/// <summary>Who can see inside a community.</summary>
public enum CommunityVisibility
{
    /// <summary>Anyone can see inside and join.</summary>
    Public,

    /// <summary>Listed by name and description, but people ask to join and only members see inside.</summary>
    Private,
}

/// <summary>What a member may do. Admins run the community; moderators help with requests and content.</summary>
public enum CommunityRole
{
    Member,
    Moderator,
    Admin,
}

public enum CommunityMembershipStatus
{
    /// <summary>Asked to join a private community; waiting for an admin or moderator.</summary>
    Requested,

    Member,

    /// <summary>Removed and kept out: can't join or ask again.</summary>
    Banned,
}

/// <summary>Discussions and their replies: shown, or taken down (by the author, the community or a moderator).</summary>
public enum CommunityContentStatus
{
    Visible,
    Removed,
}

/// <summary>
/// A community (owner, 23 Oct 2026, "forums branded Communities"): people around a place or a topic, with posts
/// shared into it and text discussions. Made by a Premium member, who is its admin. Public (anyone sees inside and
/// joins) or private (listed, but people ask to join and only members see inside). Optionally covers an area (a place
/// and radius): members posting something that happened there are offered it. Stored in <c>posts.Communities</c>.
/// </summary>
public sealed class Community
{
    public const int NameMinLength = 3;
    public const int NameMaxLength = 60;
    public const int DescriptionMaxLength = 500;
    public const int RulesMaxLength = 1000;
    public const int SlugMaxLength = 70;
    public const int PlaceNameMaxLength = 100;
    public const int RemovedReasonMaxLength = 500;

    /// <summary>The radiuses offered, in km.</summary>
    public static readonly IReadOnlyList<int> Radiuses = [5, 10, 15, 25, 50];

    // For EF Core.
    private Community()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Its address, from the name: <c>/c/salford-road-watch</c>. Never changes.</summary>
    public string Slug { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    /// <summary>The community's own rules, shown beside it (Bad Jockeys' guidelines apply anyway).</summary>
    public string? Rules { get; private set; }

    public CommunityVisibility Visibility { get; private set; }

    /// <summary>The area's place name, as chosen.</summary>
    public string? PlaceName { get; private set; }

    public decimal? Latitude { get; private set; }

    public decimal? Longitude { get; private set; }

    public int? RadiusKm { get; private set; }

    /// <summary>Who made it: always one of its admins.</summary>
    public Guid OwnerId { get; private set; }

    /// <summary>Members (not requests or bans), kept up to date as people join and leave.</summary>
    public int MemberCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Set when its admin deletes it or a Bad Jockeys moderator takes it down: it's gone for everyone.</summary>
    public DateTimeOffset? RemovedAt { get; private set; }

    public string? RemovedReason { get; private set; }

    public bool IsRemoved => RemovedAt is not null;

    public bool HasArea => Latitude is not null && Longitude is not null && RadiusKm is not null;

    public static Community Create(Guid ownerId, string slug, CommunityDetails details, DateTimeOffset now)
    {
        var community = new Community
        {
            Id = Guid.CreateVersion7(now),
            Slug = slug,
            OwnerId = ownerId,
            CreatedAt = now,
        };
        community.Update(details, now);
        return community;
    }

    public void Update(CommunityDetails details, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(details.Name);
        Name = details.Name.Trim();
        Description = (details.Description ?? string.Empty).Trim();
        Rules = string.IsNullOrWhiteSpace(details.Rules) ? null : details.Rules.Trim();
        Visibility = details.Visibility;
        if (details.Area is { } area)
        {
            PlaceName = area.PlaceName.Trim();
            Latitude = Math.Round(area.Latitude, 2, MidpointRounding.AwayFromZero);
            Longitude = Math.Round(area.Longitude, 2, MidpointRounding.AwayFromZero);
            RadiusKm = area.RadiusKm;
        }
        else
        {
            (PlaceName, Latitude, Longitude, RadiusKm) = (null, null, null, null);
        }

        UpdatedAt = now;
    }

    public void Remove(string reason, DateTimeOffset now)
    {
        RemovedAt ??= now;
        RemovedReason = reason.Length <= RemovedReasonMaxLength ? reason : reason[..RemovedReasonMaxLength];
    }

    public void CountMembers(int change) => MemberCount = Math.Max(0, MemberCount + change);

    /// <summary>True if the community has an area and the point is inside it.</summary>
    public bool Covers(decimal latitude, decimal longitude) =>
        HasArea && GeoDistance.Km(Latitude!.Value, Longitude!.Value, latitude, longitude) <= RadiusKm!.Value;

    /// <summary>"Salford Road Watch!" → "salford-road-watch": lower-case letters, digits and single dashes.</summary>
    public static string SlugFrom(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                slug.Append(c);
            }
            else if (char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark && slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var text = slug.ToString().Trim('-');
        if (text.Length > SlugMaxLength - 4)
        {
            text = text[..(SlugMaxLength - 4)].Trim('-');
        }

        return text.Length == 0 ? "community" : text;
    }
}

/// <summary>A community's area: a place, about 1 km of precision, and a radius in km.</summary>
public sealed record CommunityArea(string PlaceName, decimal Latitude, decimal Longitude, int RadiusKm);

/// <summary>What a community's admin sets.</summary>
public sealed record CommunityDetails(
    string Name,
    string? Description,
    string? Rules,
    CommunityVisibility Visibility,
    CommunityArea? Area = null);

/// <summary>
/// Someone's place in a community (<c>posts.CommunityMembers</c>): asking to join, a member (with a role), or banned.
/// </summary>
public sealed class CommunityMember
{
    // For EF Core.
    private CommunityMember()
    {
    }

    public Guid CommunityId { get; private set; }

    public Guid UserId { get; private set; }

    public CommunityMembershipStatus Status { get; private set; }

    public CommunityRole Role { get; private set; }

    public DateTimeOffset? RequestedAt { get; private set; }

    public DateTimeOffset? JoinedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsMember => Status == CommunityMembershipStatus.Member;

    /// <summary>Admins and moderators: approve requests, remove members and content.</summary>
    public bool CanModerate => IsMember && Role is CommunityRole.Admin or CommunityRole.Moderator;

    public bool IsAdmin => IsMember && Role == CommunityRole.Admin;

    public static CommunityMember Join(Guid communityId, Guid userId, DateTimeOffset now, CommunityRole role = CommunityRole.Member) =>
        new()
        {
            CommunityId = communityId,
            UserId = userId,
            Status = CommunityMembershipStatus.Member,
            Role = role,
            JoinedAt = now,
            UpdatedAt = now,
        };

    public static CommunityMember Request(Guid communityId, Guid userId, DateTimeOffset now) =>
        new()
        {
            CommunityId = communityId,
            UserId = userId,
            Status = CommunityMembershipStatus.Requested,
            Role = CommunityRole.Member,
            RequestedAt = now,
            UpdatedAt = now,
        };

    public void Approve(DateTimeOffset now)
    {
        Status = CommunityMembershipStatus.Member;
        JoinedAt = now;
        UpdatedAt = now;
    }

    public void Ban(DateTimeOffset now)
    {
        Status = CommunityMembershipStatus.Banned;
        Role = CommunityRole.Member;
        UpdatedAt = now;
    }

    public void SetRole(CommunityRole role, DateTimeOffset now)
    {
        Role = role;
        UpdatedAt = now;
    }
}

/// <summary>A post shared into a community (<c>posts.CommunityPosts</c>). The post stays public everywhere.</summary>
public sealed class CommunityPost
{
    // For EF Core.
    private CommunityPost()
    {
    }

    public Guid CommunityId { get; private set; }

    public Guid PostId { get; private set; }

    public Guid SharedBy { get; private set; }

    public DateTimeOffset SharedAt { get; private set; }

    public static CommunityPost Share(Guid communityId, Guid postId, Guid sharedBy, DateTimeOffset now) =>
        new() { CommunityId = communityId, PostId = postId, SharedBy = sharedBy, SharedAt = now };
}

/// <summary>A text discussion in a community (<c>posts.CommunityDiscussions</c>), with replies.</summary>
public sealed class CommunityDiscussion
{
    public const int TitleMaxLength = 120;
    public const int BodyMaxLength = 2000;

    // For EF Core.
    private CommunityDiscussion()
    {
    }

    public Guid Id { get; private set; }

    public Guid CommunityId { get; private set; }

    public Guid AuthorId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public CommunityContentStatus Status { get; private set; }

    /// <summary>Flagged by content screening for a moderator to look at; still shown.</summary>
    public bool IsFlagged { get; private set; }

    /// <summary>Visible replies.</summary>
    public int ReplyCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When it was started or last replied to: discussions are listed by this.</summary>
    public DateTimeOffset LastActivityAt { get; private set; }

    public DateTimeOffset? RemovedAt { get; private set; }

    public static CommunityDiscussion Start(Guid communityId, Guid authorId, string title, string? body, bool flagged, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            CommunityId = communityId,
            AuthorId = authorId,
            Title = title.Trim(),
            Body = (body ?? string.Empty).Trim(),
            Status = CommunityContentStatus.Visible,
            IsFlagged = flagged,
            CreatedAt = now,
            LastActivityAt = now,
        };

    public void Replied(DateTimeOffset now)
    {
        ReplyCount++;
        LastActivityAt = now;
    }

    public void ReplyRemoved() => ReplyCount = Math.Max(0, ReplyCount - 1);

    public void Remove(DateTimeOffset now)
    {
        Status = CommunityContentStatus.Removed;
        RemovedAt ??= now;
    }
}

/// <summary>A reply to a discussion (<c>posts.CommunityReplies</c>).</summary>
public sealed class CommunityReply
{
    public const int BodyMaxLength = 1000;

    // For EF Core.
    private CommunityReply()
    {
    }

    public Guid Id { get; private set; }

    public Guid DiscussionId { get; private set; }

    public Guid CommunityId { get; private set; }

    public Guid AuthorId { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public CommunityContentStatus Status { get; private set; }

    public bool IsFlagged { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RemovedAt { get; private set; }

    public static CommunityReply Create(CommunityDiscussion discussion, Guid authorId, string body, bool flagged, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            DiscussionId = discussion.Id,
            CommunityId = discussion.CommunityId,
            AuthorId = authorId,
            Body = body.Trim(),
            Status = CommunityContentStatus.Visible,
            IsFlagged = flagged,
            CreatedAt = now,
        };

    public void Remove(DateTimeOffset now)
    {
        Status = CommunityContentStatus.Removed;
        RemovedAt ??= now;
    }
}
