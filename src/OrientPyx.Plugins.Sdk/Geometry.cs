namespace OrientPyx.Plugins.Sdk;

/// <summary>An optional paper-map coordinate pair (millimetres) for one point on a course.</summary>
/// <param name="X">Map X in millimetres, or null when unknown.</param>
/// <param name="Y">Map Y in millimetres, or null when unknown.</param>
public readonly record struct MapPoint(double? X, double? Y)
{
    /// <summary>True only when both coordinates are present.</summary>
    public bool HasCoordinates => X is not null && Y is not null;
}

/// <summary>An optional WGS-84 coordinate pair for one point on a course.</summary>
/// <param name="Latitude">WGS-84 latitude in degrees, or null when unknown.</param>
/// <param name="Longitude">WGS-84 longitude in degrees, or null when unknown.</param>
public readonly record struct GeoPoint(double? Latitude, double? Longitude)
{
    /// <summary>True only when both coordinates are present.</summary>
    public bool HasCoordinates => Latitude is not null && Longitude is not null;
}
