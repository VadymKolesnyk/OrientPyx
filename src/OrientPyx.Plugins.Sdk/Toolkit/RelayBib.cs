using System.Globalization;

namespace OrientPyx.Plugins.Sdk.Toolkit;

/// <summary>
/// The relay leg number «{team}.{leg}» (100.2) — computed from the team number and the leg, never stored on
/// the participant (Р-23).
/// </summary>
public static class RelayBib
{
    /// <summary>«100.2» for team 100, leg 2.</summary>
    public static string Format(int teamNumber, int leg) =>
        teamNumber.ToString(CultureInfo.InvariantCulture) + "." + leg.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses «100.2» (also «100,2» and «100/2») into team 100, leg 2; a bare «100» parses as team 100 with no
    /// leg (the whole team, Р-24). False for anything else.
    /// </summary>
    public static bool TryParse(string? text, out int teamNumber, out int? leg)
    {
        teamNumber = 0;
        leg = null;
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        var sep = s.IndexOfAny(['.', ',', '/']);
        if (sep < 0)
            return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out teamNumber);

        if (!int.TryParse(s[..sep], NumberStyles.None, CultureInfo.InvariantCulture, out teamNumber)
            || !int.TryParse(s[(sep + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var l)
            || l <= 0)
        {
            teamNumber = 0;
            return false;
        }
        leg = l;
        return true;
    }

    /// <summary>True when <paramref name="text"/> is a leg number with an explicit leg («100.2»).</summary>
    public static bool IsLegNumber(string? text) => TryParse(text, out _, out var leg) && leg is not null;
}
