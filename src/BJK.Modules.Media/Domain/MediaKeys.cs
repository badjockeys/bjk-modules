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

    /// <summary>Everything a user has uploaded sits under this prefix.</summary>
    public static string UserFolder(Guid userId) => $"{userId:D}/";

    public static string Folder(Guid userId, Guid mediaId) => $"{userId:D}/{mediaId:D}";

    /// <summary>The uploaded file, in the private originals bucket.</summary>
    public static string Original(Guid userId, Guid mediaId, string extension) =>
        $"{Folder(userId, mediaId)}/original.{extension}";

    /// <summary>The main processed file, in the public bucket.</summary>
    public static string Playback(Guid userId, Guid mediaId, MediaType type) =>
        $"{Folder(userId, mediaId)}/{(type == MediaType.Video ? VideoPlaybackFile : ImageFile)}";

    public static string Thumbnail(Guid userId, Guid mediaId, MediaType type) =>
        $"{Folder(userId, mediaId)}/{(type == MediaType.Video ? VideoThumbnailFile : ImageThumbnailFile)}";
}
