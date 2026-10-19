namespace BJK.Modules.Posts.Domain;

/// <summary>
/// Where incidents can be posted. Each region is a set of simplified outlines (longitude, latitude pairs, WGS 84),
/// drawn by hand from the coastline at roughly 5 to 15 km accuracy and kept well clear of neighbouring countries:
/// <list type="bullet">
/// <item><see cref="UnitedKingdom"/> ("GB", the ISO code for the whole UK): mainland Great Britain, Northern Ireland
/// (following the land border with Ireland) and the islands: Isle of Wight, Anglesey, Scilly, Arran, the Inner and
/// Outer Hebrides, Orkney, Shetland and Rathlin.</item>
/// <item><see cref="IsleOfMan"/> ("IM"), <see cref="Jersey"/> ("JE") and <see cref="Guernsey"/> ("GG", with Alderney and
/// Sark): Crown Dependencies, outside the UK, so off by default.</item>
/// </list>
/// The outlines are generous at sea and tight on land borders and narrow straits (Ireland, France across the Dover
/// Strait). Points within a few kilometres of the coast or the Irish border may be classified either way.
/// </summary>
public static class ServiceRegions
{
    public const string UnitedKingdom = "GB";
    public const string IsleOfMan = "IM";
    public const string Jersey = "JE";
    public const string Guernsey = "GG";

    /// <summary>Regions allowed when <c>Posts:AllowedRegions</c> isn't set: the UK only.</summary>
    public static readonly IReadOnlyList<string> Default = [UnitedKingdom];

    private static readonly IReadOnlyDictionary<string, double[][]> Outlines = new Dictionary<string, double[][]>(StringComparer.OrdinalIgnoreCase)
    {
        [UnitedKingdom] =
        [
            // Mainland Great Britain, anticlockwise from Dover.
            [
                1.45, 51.15, 1.45, 51.41, 0.95, 51.50, 0.95, 51.62, 1.30, 51.85, 1.65, 52.10, 1.80, 52.48, 1.75, 52.65,
                1.40, 52.92, 0.95, 52.98, 0.50, 52.97, 0.35, 52.80, 0.35, 53.10, 0.25, 53.45, 0.15, 53.60, -0.10, 54.10,
                -0.33, 54.29, -0.60, 54.49, -1.05, 54.66, -1.25, 54.72, -1.38, 55.00, -1.55, 55.30, -1.80, 55.66,
                -2.00, 55.80, -2.10, 55.92, -2.55, 56.05, -2.80, 56.08, -3.10, 55.97, -3.20, 56.00, -2.60, 56.25,
                -2.75, 56.45, -2.52, 56.55, -2.42, 56.72, -2.03, 57.15, -1.74, 57.50, -2.00, 57.70, -3.00, 57.75, -3.35, 57.75, -3.85, 57.63,
                -4.20, 57.50, -3.95, 57.82, -3.70, 58.00, -3.40, 58.25, -3.05, 58.45, -3.02, 58.64, -3.40, 58.67,
                -4.00, 58.60, -4.80, 58.62, -5.00, 58.63, -5.20, 58.40, -5.45, 58.05, -5.80, 57.85, -5.85, 57.55,
                -5.70, 57.30, -5.85, 57.00, -6.25, 56.70, -5.95, 56.55, -5.50, 56.40, -5.65, 56.05, -5.75, 55.70,
                -5.80, 55.30, -5.55, 55.40, -5.40, 55.80, -4.85, 55.60, -4.65, 55.45, -4.95, 55.15, -5.15, 54.95,
                -5.12, 54.75, -4.86, 54.63, -4.40, 54.68, -3.90, 54.78, -3.55, 54.95, -3.45, 54.88, -3.66, 54.60,
                -3.48, 54.20, -3.25, 54.05, -3.00, 53.95, -3.05, 53.80, -3.10, 53.55, -3.10, 53.40, -3.40, 53.35,
                -3.85, 53.33, -4.15, 53.22, -4.35, 53.12, -4.60, 52.98, -4.78, 52.78, -4.45, 52.80, -4.10, 52.90,
                -4.10, 52.60, -4.10, 52.40, -4.70, 52.12, -5.10, 52.03, -5.32, 51.86, -5.25, 51.70, -4.95, 51.60,
                -4.30, 51.65, -4.30, 51.55, -3.95, 51.57, -3.60, 51.40, -3.15, 51.40, -2.70, 51.55, -2.70, 51.48,
                -3.00, 51.35, -3.15, 51.20, -3.60, 51.22, -4.20, 51.22, -4.25, 51.05, -4.55, 50.95, -4.60, 50.65,
                -5.05, 50.58, -5.15, 50.40, -5.55, 50.25, -5.78, 50.08, -5.72, 50.02, -5.20, 49.95, -5.00, 50.15, -4.75, 50.30,
                -4.20, 50.33, -3.65, 50.20, -3.50, 50.45, -3.20, 50.68, -2.70, 50.70, -2.45, 50.52, -1.95, 50.58,
                -1.70, 50.72, -1.30, 50.78, -1.00, 50.78, -0.75, 50.72, -0.20, 50.80, 0.25, 50.73, 0.60, 50.85,
                0.98, 50.91, 1.10, 51.05,
            ],

            // Northern Ireland: the coast from Lough Foyle round to Carlingford Lough, then the land border back.
            [
                -6.95, 55.19, -6.50, 55.25, -6.05, 55.25, -5.98, 55.05, -5.75, 54.80, -5.48, 54.70, -5.43, 54.48,
                -5.55, 54.30, -5.90, 54.20, -5.97, 54.05, -6.07, 54.01, -6.15, 54.05, -6.24, 54.085, -6.27, 54.11, -6.30, 54.10, -6.65, 54.07, -6.65, 54.27,
                -6.80, 54.40, -6.95, 54.37, -7.15, 54.30, -7.30, 54.15, -7.63, 54.13, -7.85, 54.21, -8.05, 54.36,
                -8.18, 54.47, -7.92, 54.66, -7.75, 54.72, -7.55, 54.78, -7.475, 54.815, -7.475, 54.87, -7.43, 54.96, -7.375, 55.04,
                -7.25, 55.07, -7.05, 55.18,
            ],

            // Rathlin Island.
            [-6.34, 55.26, -6.14, 55.26, -6.14, 55.32, -6.34, 55.32],

            // Isle of Wight.
            [-1.60, 50.66, -1.30, 50.78, -1.06, 50.72, -1.15, 50.57, -1.30, 50.56, -1.52, 50.62],

            // Anglesey and Holy Island.
            [-4.20, 53.20, -4.02, 53.30, -4.25, 53.44, -4.56, 53.44, -4.72, 53.32, -4.55, 53.14, -4.35, 53.12],

            // Isles of Scilly.
            [-6.48, 49.84, -6.18, 49.84, -6.18, 50.00, -6.48, 50.00],

            // Arran.
            [-5.42, 55.42, -5.03, 55.44, -5.03, 55.73, -5.25, 55.76, -5.42, 55.62],

            // Inner Hebrides: Islay, Jura, Colonsay, Mull, Coll, Tiree, the Small Isles and Skye.
            [
                -6.55, 55.55, -5.95, 55.65, -5.70, 56.05, -5.65, 56.45, -5.95, 56.70, -5.70, 57.00, -5.60, 57.30,
                -5.95, 57.60, -6.30, 57.75, -6.85, 57.50, -6.45, 57.10, -7.05, 56.60, -6.75, 56.25, -6.45, 56.05,
                -6.55, 55.85,
            ],

            // Outer Hebrides, Barra Head to the Butt of Lewis.
            [-7.75, 56.75, -7.25, 56.75, -6.95, 57.40, -6.10, 58.25, -6.20, 58.56, -6.60, 58.42, -7.20, 58.10, -7.75, 57.40],

            // Orkney.
            [-3.48, 58.70, -2.40, 58.70, -2.33, 59.42, -3.10, 59.42, -3.48, 59.10],

            // Fair Isle.
            [-1.70, 59.50, -1.55, 59.50, -1.55, 59.57, -1.70, 59.57],

            // Shetland.
            [-1.82, 59.82, -1.00, 59.86, -0.70, 60.86, -1.00, 60.90, -1.78, 60.50, -1.82, 60.10],
        ],
        [IsleOfMan] =
        [
            [-4.82, 54.04, -4.55, 54.03, -4.30, 54.25, -4.33, 54.43, -4.45, 54.43, -4.75, 54.25],
        ],
        [Jersey] =
        [
            [-2.27, 49.15, -2.00, 49.15, -2.00, 49.28, -2.27, 49.28],
        ],
        [Guernsey] =
        [
            // Guernsey, Herm and Sark.
            [-2.70, 49.40, -2.33, 49.40, -2.33, 49.52, -2.70, 49.52],

            // Alderney.
            [-2.26, 49.69, -2.13, 49.69, -2.13, 49.75, -2.26, 49.75],
        ],
    };

    /// <summary>Every region code this class knows.</summary>
    public static IReadOnlyCollection<string> Known => [.. Outlines.Keys];

    /// <summary>The region a point lies in (GB, IM, JE or GG), or null if it's in none of them.</summary>
    public static string? RegionOf(decimal latitude, decimal longitude)
    {
        var lat = (double)latitude;
        var lng = (double)longitude;
        foreach (var (region, outlines) in Outlines)
        {
            if (outlines.Any(outline => Contains(outline, lng, lat)))
            {
                return region;
            }
        }

        return null;
    }

    /// <summary>True if the point lies in one of the allowed regions.</summary>
    public static bool IsAllowed(decimal latitude, decimal longitude, IEnumerable<string> allowedRegions)
    {
        var region = RegionOf(latitude, longitude);
        return region is not null && allowedRegions.Contains(region, StringComparer.OrdinalIgnoreCase);
    }

    // Ray casting: count how many edges a ray going east from the point crosses.
    private static bool Contains(double[] outline, double x, double y)
    {
        var inside = false;
        var count = outline.Length / 2;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            double xi = outline[2 * i], yi = outline[(2 * i) + 1];
            double xj = outline[2 * j], yj = outline[(2 * j) + 1];
            if ((yi > y) != (yj > y) && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
