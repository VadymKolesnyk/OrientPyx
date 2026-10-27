namespace OrientPyx.BusinessLogic.Enums;

/// <summary>How a relay team that used a restart is ranked (Р-35).</summary>
public enum RelayRestartRanking
{
    /// <summary>Together with everyone else, by time.</summary>
    Normal,

    /// <summary>After every team without a restart; among themselves by time (the default).</summary>
    AfterClean,

    /// <summary>Not placed («П/К»).</summary>
    Unranked
}
