namespace OrientPyx.Plugins.Sdk;

/// <summary>A single control punch read from a chip: the control code and, when known, its time.</summary>
/// <param name="ControlCode">Control code/number as written in the file. Trimmed.</param>
/// <param name="Time">Punch time when the file carried one; otherwise null.</param>
public sealed record ChipPunch(string ControlCode, DateTimeOffset? Time);
