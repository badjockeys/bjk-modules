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

    /// <summary>Photos only: the feed-size copy (1080 px longest side by default), loaded in feeds; the full image opens on tap.</summary>
    public const string FeedImageFile = "feed.webp";

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

    /// <summary>A photo's feed-size copy, in the public bucket (or the sensitive bucket, under <see cref="Sensitive"/>).</summary>
    /// <param name="userId">The uploader.</param>
    /// <param name="mediaId">The media.</param>
    /// <param name="revision">The media revision.</param>
    public static string Feed(Guid userId, Guid mediaId, int revision = 0) =>
        $"{Folder(userId, mediaId)}/{Revised(FeedImageFile, revision)}";

    /// <summary>A photo's blurred feed-size copy waiting for review, in the private originals bucket.</summary>
    /// <param name="userId">The uploader.</param>
    /// <param name="mediaId">The media.</param>
    /// <param name="revision">The media revision.</param>
    public static string ReviewFeed(Guid userId, Guid mediaId, int revision = 0) =>
        $"{Folder(userId, mediaId)}/{ReviewFolder}/{Revised(FeedImageFile, revision)}";

    /// <summary>The blurred main file waiting for review, in the private originals bucket.</summary>
    public static string ReviewPlayback(Guid userId, Guid mediaId, MediaType type, int revision = 0) =>
        $"{Folder(userId, mediaId)}/{ReviewFolder}/{Revised(type == MediaType.Video ? VideoPlaybackFile : ImageFile, revision)}";

    /// <summary>The blurred thumbnail waiting for review, in the private originals bucket.</summary>
    public static string ReviewThumbnail(Guid userId, Guid mediaId, MediaType type, int revision = 0) =>
        $"{Folder(userId, mediaId)}/{ReviewFolder}/{Revised(type == MediaType.Video ? VideoThumbnailFile : ImageThumbnailFile, revision)}";

    /// <summary>
    /// Sub-folder, in the sensitive bucket, for the real processed files of media in a post tagged sensitive. They're
    /// never public: age-verified adults, the uploader and moderators get short-lived signed links to them.
    /// </summary>
    public const string SensitiveFolder = "sensitive";

    /// <summary>File name of the heavily blurred preview published for sensitive media (before any revision suffix).</summary>
    public const string PreviewFile = "preview.webp";

    /// <summary>
    /// Where a processed file of sensitive media is kept, in the sensitive bucket: its published key with
    /// <see cref="SensitiveFolder"/> added before the file name, e.g. <c>{userId}/{mediaId}/sensitive/720p.mp4</c>.
    /// </summary>
    /// <param name="publishedKey">The file's key in the public bucket (<see cref="Playback"/> or <see cref="Thumbnail"/>).</param>
    public static string Sensitive(string publishedKey)
    {
        var slash = publishedKey.LastIndexOf('/');
        return slash < 0
            ? $"{SensitiveFolder}/{publishedKey}"
            : $"{publishedKey[..slash]}/{SensitiveFolder}/{publishedKey[(slash + 1)..]}";
    }

    /// <summary>
    /// The heavily blurred, low-resolution preview of sensitive media, in the public bucket: the only file of sensitive
    /// media that's ever public. Named per revision, like the other published files.
    /// </summary>
    /// <param name="userId">The uploader.</param>
    /// <param name="mediaId">The media.</param>
    /// <param name="revision">The media revision.</param>
    public static string Preview(Guid userId, Guid mediaId, int revision = 0) =>
        $"{Folder(userId, mediaId)}/{Revised(PreviewFile, revision)}";

    /// <summary>Sub-folder, in the private originals bucket, for copies of processed files being re-processed.</summary>
    public const string ReworkFolder = "rework";

    /// <summary>
    /// A copy of the processed main file, in the private originals bucket, that a re-processing (a moderator added blur
    /// regions) works from instead of the original, e.g. <c>{userId}/{mediaId}/rework/r1.mp4</c>.
    /// </summary>
    /// <param name="userId">The uploader.</param>
    /// <param name="mediaId">The media.</param>
    /// <param name="type">Video or Image.</param>
    /// <param name="revision">The revision being made.</param>
    public static string ReworkSource(Guid userId, Guid mediaId, MediaType type, int revision) =>
        $"{Folder(userId, mediaId)}/{ReworkFolder}/r{revision}{(type == MediaType.Video ? ".mp4" : ".webp")}";

    /// <summary>Sub-folder, in the private originals bucket, for blurred video segments waiting to be joined.</summary>
    public const string SegmentsFolder = "segments";

    /// <summary>
    /// One blurred segment of a video being processed in parts, in the private originals bucket, e.g.
    /// <c>{userId}/{mediaId}/segments/r0/002.mp4</c>. Removed once the segments are joined (and by the 90-day rule).
    /// </summary>
    public static string Segment(Guid userId, Guid mediaId, int revision, int index) =>
        $"{Folder(userId, mediaId)}/{SegmentsFolder}/r{revision}/{index:D3}.mp4";

    /// <summary>Sub-folder, in the private originals bucket, for the vertical copy made for sharing.</summary>
    public const string ShareFolder = "share";

    /// <summary>
    /// The vertical copy of a video made for sharing (<see cref="Domain.ShareCopy"/>), in the private originals bucket:
    /// <c>{userId}/{mediaId}/share/vertical.mp4</c>. One per media; making it again replaces it.
    /// </summary>
    /// <param name="userId">The uploader.</param>
    /// <param name="mediaId">The media.</param>
    public static string ShareVideo(Guid userId, Guid mediaId) => $"{Folder(userId, mediaId)}/{ShareFolder}/vertical.mp4";

    /// <summary><c>720p.mp4</c> for revision 0, <c>720p-r2.mp4</c> for revision 2.</summary>
    private static string Revised(string fileName, int revision) =>
        revision <= 0 ? fileName : $"{Path.GetFileNameWithoutExtension(fileName)}-r{revision}{Path.GetExtension(fileName)}";
}
