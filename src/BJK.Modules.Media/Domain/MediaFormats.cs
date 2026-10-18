namespace BJK.Modules.Media.Domain;

/// <summary>The file types people can upload, and the extension used for the stored original.</summary>
public static class MediaFormats
{
    private static readonly Dictionary<string, (MediaType Type, string Extension)> Supported =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["video/mp4"] = (MediaType.Video, "mp4"),
            ["video/quicktime"] = (MediaType.Video, "mov"),
            ["video/3gpp"] = (MediaType.Video, "3gp"),
            ["image/jpeg"] = (MediaType.Image, "jpg"),
            ["image/png"] = (MediaType.Image, "png"),
            ["image/heic"] = (MediaType.Image, "heic"),
            ["image/heif"] = (MediaType.Image, "heif"),
            ["image/webp"] = (MediaType.Image, "webp"),
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
