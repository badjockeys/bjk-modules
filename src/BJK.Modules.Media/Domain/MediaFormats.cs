namespace BJK.Modules.Media.Domain;

/// <summary>
/// The file types people can upload, and the extension used for the stored original. Some types have several names
/// because browsers disagree (AVI, BMP). ffmpeg decodes them all; a photo is read from its first frame, so an animated
/// GIF becomes a still (widened 21 Oct 2026: M4V, WebM, MKV, AVI, AVIF, GIF, TIFF, BMP).
/// </summary>
public static class MediaFormats
{
    private static readonly Dictionary<string, (MediaType Type, string Extension)> Supported =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["video/mp4"] = (MediaType.Video, "mp4"),
            ["video/quicktime"] = (MediaType.Video, "mov"),
            ["video/3gpp"] = (MediaType.Video, "3gp"),
            ["video/x-m4v"] = (MediaType.Video, "m4v"),
            ["video/webm"] = (MediaType.Video, "webm"),
            ["video/x-matroska"] = (MediaType.Video, "mkv"),
            ["video/x-msvideo"] = (MediaType.Video, "avi"),
            ["video/avi"] = (MediaType.Video, "avi"),
            ["video/msvideo"] = (MediaType.Video, "avi"),
            ["image/jpeg"] = (MediaType.Image, "jpg"),
            ["image/png"] = (MediaType.Image, "png"),
            ["image/heic"] = (MediaType.Image, "heic"),
            ["image/heif"] = (MediaType.Image, "heif"),
            ["image/webp"] = (MediaType.Image, "webp"),
            ["image/avif"] = (MediaType.Image, "avif"),
            ["image/gif"] = (MediaType.Image, "gif"),
            ["image/tiff"] = (MediaType.Image, "tiff"),
            ["image/bmp"] = (MediaType.Image, "bmp"),
            ["image/x-ms-bmp"] = (MediaType.Image, "bmp"),
        };

    public static IReadOnlyCollection<string> ContentTypes => Supported.Keys;

    public static bool TryGet(string? contentType, out MediaType type, out string extension)
    {
        if (contentType is not null && Supported.TryGetValue(contentType.Trim(), out var format))
        {
            type = format.Type;
            extension = format.Extension;
            return true;
        }

        type = default;
        extension = string.Empty;
        return false;
    }
}
