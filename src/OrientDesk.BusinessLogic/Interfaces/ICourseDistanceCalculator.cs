namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Computes the straight-line ("as the crow flies") length of a course from the geographic
/// positions of the control points it visits, in order. Layer-neutral and side-effect free, so it
/// can serve the group import today and any future distance display/recalculation elsewhere.
///
/// A leg whose either endpoint has no coordinates contributes <b>0</b> to the total (rather than
/// throwing or skipping the point), keeping the result stable for partially-geocoded courses.
/// </summary>
public interface ICourseDistanceCalculator
{
    /// <summary>
    /// Sums the great-circle distance between consecutive points and returns it in kilometres.
    /// Returns 0 for fewer than two points. A null coordinate on either side of a leg makes that
    /// leg count as 0 km.
    /// </summary>
    /// <param name="points">The visited points in running order, each with optional WGS-84 lat/lon.</param>
    decimal TotalKilometres(IReadOnlyList<GeoPoint> points);
}

/// <summary>An optional WGS-84 coordinate pair for one point on a course.</summary>
/// <param name="Latitude">WGS-84 latitude in degrees, or null when unknown.</param>
/// <param name="Longitude">WGS-84 longitude in degrees, or null when unknown.</param>
public readonly record struct GeoPoint(double? Latitude, double? Longitude)
{
    /// <summary>True only when both coordinates are present.</summary>
    public bool HasCoordinates => Latitude is not null && Longitude is not null;
}
