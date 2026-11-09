namespace OrientPyx.BusinessLogic.Models;

/// <summary>A start number another participant already holds: who (<paramref name="HolderName"/>) and the number the
/// edited participant keeps instead (<paramref name="KeptNumber"/>, possibly blank).</summary>
public sealed record NumberConflict(string HolderName, string KeptNumber);
