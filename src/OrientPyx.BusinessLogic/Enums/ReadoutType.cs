namespace OrientPyx.BusinessLogic.Enums;

/// <summary>
/// Which timing-system readout file format the app reads. An application-level setting (the whole
/// installation uses one timing system), stored as an int on the app settings row and used to pick
/// the matching <see cref="Interfaces.IReadoutParser"/>.
/// </summary>
public enum ReadoutType
{
    /// <summary>SPORTident Reader "Config+ (card readout)" CSV: named-header, UTF-8, per-station DOW columns.</summary>
    SportIdent = 0,

    /// <summary>Sport Time CSV: named-header, windows-1251, DOW empty (weekday may be in parentheses after the time).</summary>
    SportTime = 1
}
