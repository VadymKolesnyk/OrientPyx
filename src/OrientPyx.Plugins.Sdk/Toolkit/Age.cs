namespace OrientPyx.Plugins.Sdk.Toolkit;

/// <summary>Age as orienteering counts it — by calendar year, like the group age window.</summary>
public static class Age
{
    /// <summary>The competition year minus the birth year (Р-46).</summary>
    public static int InYear(int birthYear, DateOnly date) => date.Year - birthYear;
}
