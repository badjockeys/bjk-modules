namespace BJK.Modules.Media.Infrastructure.Processing;

/// <summary>
/// The pictures added to vertical copies made for sharing, built into the assembly: a small "badjockeys.com"
/// watermark with the logo, and a 1080×1920 end card ("Seen on Bad Jockeys", badjockeys.com, faces and number plates
/// are blurred). Both were drawn in the brand's colours and Poppins; the end card names no post, so one copy suits
/// every video.
/// </summary>
public static class ShareVideoAssets
{
    public const int Width = 1080;

    public const int Height = 1920;

    /// <summary>How long the end card shows.</summary>
    public const double EndCardSeconds = 2.5;

    private const string WatermarkResource = "BJK.Modules.Media.share-watermark.png";
    private const string EndCardResource = "BJK.Modules.Media.share-endcard.png";

    /// <summary>Writes both pictures into <paramref name="folder"/>.</summary>
    /// <param name="folder">The job's working folder.</param>
    /// <param name="cancellationToken">Stops writing.</param>
    /// <returns>The paths of the watermark and the end card.</returns>
    public static async Task<(string Watermark, string EndCard)> WriteToAsync(string folder, CancellationToken cancellationToken)
    {
        var watermark = Path.Combine(folder, "watermark.png");
        var endCard = Path.Combine(folder, "endcard.png");
        await CopyAsync(WatermarkResource, watermark, cancellationToken);
        await CopyAsync(EndCardResource, endCard, cancellationToken);
        return (watermark, endCard);
    }

    private static async Task CopyAsync(string resource, string path, CancellationToken cancellationToken)
    {
        await using var source = typeof(ShareVideoAssets).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The {resource} resource is missing from the Media assembly.");
        await using var file = File.Create(path);
        await source.CopyToAsync(file, cancellationToken);
    }
}
