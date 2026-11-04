namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// The competition's saved set-up of the chip-scanning dialog («Сканування чіпів» on the participants page):
/// which additional field names a participant's chip type, how each of its values maps to an own chip or a
/// rental note, and the read-out file watched. One competition-level row (Id = 1), JSON.
/// </summary>
public class ChipScanSettingsRow
{
    /// <summary>Single competition-level row (Id = 1).</summary>
    public int Id { get; set; } = 1;

    /// <summary>The <see cref="OrientPyx.BusinessLogic.Models.ChipScanSettings"/> serialised as JSON.</summary>
    public string Json { get; set; } = string.Empty;
}
