using OrientDesk.BusinessLogic.Interfaces;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// Default <see cref="ICourseDistanceCalculator"/>. Adds up the haversine (great-circle) distance
/// between consecutive points. Any leg with a missing coordinate on either end counts as 0 km, so a
/// course that is only partially geocoded still yields a finite, stable length.
/// </summary>
public sealed class CourseDistanceCalculator : ICourseDistanceCalculator
{
    // Mean Earth radius in metres (IUGG), good enough for course-length estimates.
    private const double EarthRadiusMetres = 6_371_000.0;

    public decimal TotalKilometres(IReadOnlyList<GeoPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
            return 0m;

        var metres = 0.0;
        for (var i = 1; i < points.Count; i++)
            metres += LegMetres(points[i - 1], points[i]);

        // Round to metre precision before converting; the grid shows ~3 decimals of a kilometre.
        return Math.Round((decimal)(metres / 1000.0), 3);
    }

    // Distance of one leg in metres; 0 when either endpoint lacks coordinates.
    private static double LegMetres(GeoPoint a, GeoPoint b)
    {
        if (!a.HasCoordinates || !b.HasCoordinates)
            return 0.0;

        return Haversine(a.Latitude!.Value, a.Longitude!.Value, b.Latitude!.Value, b.Longitude!.Value);
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var rLat1 = ToRadians(lat1);
        var rLat2 = ToRadians(lat2);

        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(rLat1) * Math.Cos(rLat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMetres * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
