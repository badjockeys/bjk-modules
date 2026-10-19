namespace BJK.Modules.Media.Domain;

/// <summary>
/// Storage keys: <c>{userId}/{mediaId}/{fileName}</c>. File names are always ours, never the uploader's,
/// so keys never contain personal details or unsafe characters.
/// </summary>
public static class MediaKeys
{
    public const string VideoPlaybackFile = "720p.mp4";
    public const string VideoThumbnailFile = "thumbnail.jpg";
    public const string ImageFile = "image.webp";
    public const string ImageThumbnailFile = "thumbnail.webp";

    /// <summary>Sub-folder, in the private originals bucket, for blurred files waiting for a moderator.</summary>
    public const string ReviewFolder = "review";

    /// <summary>Everything a user has uploaded sits under this prefix.</summary>
    public static string UserFolder(Guid userId) => $"{userId:D}/";

    public static string Folder(Guid userId, Guid mediaId) => $"{userId:D}/{mediaId:D}";

    /// <summary>The uploaded file, in the private originals bucket.</summary>
    public static string Original(Guid userId, Guid mediaId, string extension) =>
        $"{Folder(userId, mediaId)}/original.{extension}";

    /// <summary>
    /// The main processed file, in the public bucket. Each re-processing (<paramref name="revision"/> above 0) gets a
    /// new name, because published files are cached as immutable for a year.
    /// </summary>
    public static string Playback(Guid userId, Guid mediaId, MediaType type, int revision = 0) =>
        $"{Folder(userId, mediaId)}/{Revised(type == MediaType.Video ? VideoPlaybackFile : ImageFile, revision)}";

    public static string Thumbnail(Guid userId, Guid mediaId, MediaType type, int revision = 0) =>
        $"{Folder(userId, mediaId)}/{Revised(type == MediaType.Video ? VideoThumbnailFile : ImageThumbnailFile, revision)}";

    /// <summary>The blurred main file waiting for review, in the private originals bucket.</summary>
    public static string ReviewPlayback(Guid userId, Guid mediaId, MediaType type, int revision = 0) =>
        $"{Folder(userId, mediaId)}/{ReviewFolder}/{Revised(type == MediaType.Video ? VideoPlaybackFile : ImageFile, revision)}";

    /// <summary>The blurred thumbnail waiting for review, in the private originals bucket.</summary>
    public static string ReviewThumbnail(Guid userId, Guid mediaId, MediaType type, int revision = 0) =>
        $"{Folder(userId, mediaId)}/{ReviewFolder}/{Revised(type == MediaType.Video ? VideoThumbnailFile : ImageThumbnailFile, revision)}";

    /// <summary>Sub-folder, in the private originals bucket, for blurred video segments waiting to be joined.</summary>
    public const string SegmentsFolder = "segments";

    /// <summary>
    /// One blurred segment of a video being processed in parts, in the private originals bucket, e.g.
    /// <c>{userId}/{mediaId}/segments/r0/002.mp4</c>. Removed once the segments are joined (and by the 90-day rule).
    /// </summary>
    public static string Segment(Guid userId, Guid mediaId, int revision, int index) =>
        $"{Folder(userId, mediaId)}/{SegmentsFolder}/r{revision}/{index:D3}.mp4";

    /// <summary><c>720p.mp4</c> for revision 0, <c>720p-r2.mp4</c> for revision 2.</summary>
    private static string Revised(string fileName, int revision) =>
        revision <= 0 ? fileName : $"{Path.GetFileNameWithoutExtension(fileName)}-r{revision}{Path.GetExtension(fileName)}";
}
