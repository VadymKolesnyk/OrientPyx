using System.Globalization;
using System.Text.Json;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Reads and writes <c>GroupDaySettings.RestartTimesJson</c>: the restart time of day per relay leg (legs 2…N,
/// Р-29) as a JSON object <c>{"2":"11:30:00","3":"12:00:00"}</c>. A malformed value reads as "no restarts".
/// </summary>
public static class RelayRestartTimes
{
    public static IReadOnlyDictionary<int, TimeSpan> Parse(string? json)
    {
        var result = new Dictionary<int, TimeSpan>();
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (raw is null)
                return result;
            foreach (var (legText, timeText) in raw)
                if (int.TryParse(legText, NumberStyles.None, CultureInfo.InvariantCulture, out var leg) && leg >= 2
                    && TimeSpan.TryParse(timeText, CultureInfo.InvariantCulture, out var time))
                    result[leg] = time;
        }
        catch (JsonException)
        {
        }
        return result;
    }

    public static string Format(IReadOnlyDictionary<int, TimeSpan> times)
    {
        if (times.Count == 0)
            return string.Empty;
        var raw = times.Where(kv => kv.Key >= 2).OrderBy(kv => kv.Key).ToDictionary(
            kv => kv.Key.ToString(CultureInfo.InvariantCulture),
            kv => kv.Value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        return raw.Count == 0 ? string.Empty : JsonSerializer.Serialize(raw);
    }
}
