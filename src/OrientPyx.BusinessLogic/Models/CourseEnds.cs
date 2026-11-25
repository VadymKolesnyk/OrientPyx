using OrientPyx.BusinessLogic.Entities;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Where one course begins and ends. A start/finish box is told by its control-point type, never by its name («С1»,
/// «Ста1», «S»…), and is never a required control: it matters only for the first and last leg — their distance and pace
/// on the splits and the slip. A day may have several (groups starting from different places, a finish of their own):
/// the course's start is the start-typed control its order names (the first one), its finish the finish-typed control
/// it names (the last one); an order that names none runs from the day's first start / to its first finish.
/// </summary>
public sealed record CourseEnds(ControlPoint? Start, ControlPoint? Finish)
{
    private static readonly char[] Separators = [' ', '\t', '\r', '\n', ',', ';', '<', '>', '[', ']'];

    /// <summary>The start's code (trimmed), or null when the day has no start.</summary>
    public string? StartCode => Start?.Code.Trim();

    /// <summary>The finish's code (trimmed), or null when the day has no finish.</summary>
    public string? FinishCode => Finish?.Code.Trim();

    public GeoPoint StartCoord => Start is null ? default : new GeoPoint(Start.Latitude, Start.Longitude);

    public GeoPoint FinishCoord => Finish is null ? default : new GeoPoint(Finish.Latitude, Finish.Longitude);

    public MapPoint StartMap => Start is null ? default : new MapPoint(Start.MapX, Start.MapY);

    public MapPoint FinishMap => Finish is null ? default : new MapPoint(Finish.MapX, Finish.MapY);

    /// <summary>The start and finish of <paramref name="courseOrder"/> (a plain order or a «mixed» pattern) among the
    /// day's <paramref name="controlPoints"/>.</summary>
    public static CourseEnds Of(string? courseOrder, IReadOnlyList<ControlPoint> controlPoints)
    {
        var starts = controlPoints.Where(c => c.Type == ControlPointType.Start).ToList();
        var finishes = controlPoints.Where(c => c.Type == ControlPointType.Finish).ToList();

        ControlPoint? start = null;
        ControlPoint? finish = null;
        foreach (var token in (courseOrder ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            start ??= starts.FirstOrDefault(c => Same(c, token));
            finish = finishes.FirstOrDefault(c => Same(c, token)) ?? finish;
        }

        return new CourseEnds(start ?? starts.FirstOrDefault(), finish ?? finishes.FirstOrDefault());
    }

    private static bool Same(ControlPoint point, string token) =>
        string.Equals(point.Code.Trim(), token, StringComparison.OrdinalIgnoreCase);
}
