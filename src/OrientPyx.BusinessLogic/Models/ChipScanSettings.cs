namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The saved set-up of the chip-scanning dialog, per competition (<see cref="Entities.ChipScanSettingsRow"/>).
/// The dialog hands every scanned chip to the next participant without one; <see cref="FieldKey"/> names the
/// additional field whose value says what kind of chip that participant needs, and <see cref="Rules"/> map each
/// value of that field onto «own chip» or «rental chip with this note». A blank value maps through the rule whose
/// <see cref="ChipScanTypeRule.Value"/> is empty; a value with no rule reads as a rental note of that value.
/// </summary>
public sealed class ChipScanSettings
{
    /// <summary>The key of the «chip type» additional field; empty when every participant gets the same kind.</summary>
    public string FieldKey { get; set; } = string.Empty;

    /// <summary>The read-out file the dialog watches; empty ⇒ the competition default (<c>chips/rentchip.csv</c>).</summary>
    public string ReadoutFilePath { get; set; } = string.Empty;

    /// <summary>One rule per field value (the empty value included).</summary>
    public List<ChipScanTypeRule> Rules { get; set; } = [];
}

/// <summary>How one value of the chip-type field is scanned: an own chip (never added to the rental base) or
/// a rental chip filed under <see cref="Note"/>.</summary>
public sealed class ChipScanTypeRule
{
    /// <summary>The field value this rule covers; empty for a blank cell.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>True: the participant brings their own chip; it is recorded on them but not in the rental base.</summary>
    public bool IsOwn { get; set; }

    /// <summary>The note a newly scanned rental chip is filed under (e.g. «AIR»); may be empty.</summary>
    public string Note { get; set; } = string.Empty;
}
