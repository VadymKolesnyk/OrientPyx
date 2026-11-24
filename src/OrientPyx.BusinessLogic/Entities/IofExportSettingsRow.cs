namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// The competition's saved options of the «Експорт IOF XML» page (organisation source, birth dates, one day or several
/// days in one file). One competition-level row (Id = 1), JSON.
/// </summary>
public class IofExportSettingsRow
{
    /// <summary>Single competition-level row (Id = 1).</summary>
    public int Id { get; set; } = 1;

    /// <summary>The <see cref="OrientPyx.BusinessLogic.Models.IofExportSettings"/> serialised as JSON.</summary>
    public string Json { get; set; } = string.Empty;
}
