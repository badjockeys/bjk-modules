namespace BJK.Modules.Posts.Domain;

/// <summary>Great-circle distance between two points, good to well under 1% at UK distances.</summary>
public static class GeoDistance
{
    private const double EarthRadiusKm = 6371.0;

    /// <summary>The distance in kilometres between two latitude/longitude points (haversine).</summary>
    public static double Km(decimal latitude1, decimal longitude1, decimal latitude2, decimal longitude2)
    {
        var lat1 = ToRadians((double)latitude1);
        var lat2 = ToRadians((double)latitude2);
        var dLat = lat2 - lat1;
        var dLon = ToRadians((double)(longitude2 - longitude1));
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
