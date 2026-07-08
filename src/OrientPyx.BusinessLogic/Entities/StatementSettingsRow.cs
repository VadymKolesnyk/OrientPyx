namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// The competition-level participant-statement («відомість») template, stored in the event database (a single
/// row, Id = 1). Holds the statement layout (orientation, ordered/visible columns, header text) serialised as
/// JSON — the same shape the app-level default uses. A competition with no row falls back to the app-level
/// default at load time, so a fresh competition starts from the configured template and is saved from then on.
/// </summary>
public class StatementSettingsRow
{
    /// <summary>Single competition-level row (Id = 1).</summary>
    public int Id { get; set; } = 1;

    /// <summary>The <see cref="OrientPyx.BusinessLogic.Models.StatementSettings"/> serialised as JSON.</summary>
    public string Json { get; set; } = string.Empty;
}
