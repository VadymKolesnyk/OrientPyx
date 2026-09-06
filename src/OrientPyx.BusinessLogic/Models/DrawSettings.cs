namespace OrientPyx.BusinessLogic.Models;

/// <summary>Which draw page a stored <see cref="DrawSettings"/> row belongs to.</summary>
public enum DrawSettingsKind
{
    /// <summary>The lane-based «Жеребкування» page (start groups / columns).</summary>
    Lanes = 0,

    /// <summary>The table-based «Класичне жеребкування» page (one row per group).</summary>
    Classic = 1,
}

/// <summary>
/// The state of a draw page for one day, persisted to the event database so the entered values survive
/// closing and reopening the page. Both draw pages share this record: the lane page uses the global
/// start/interval/gap plus <see cref="Lanes"/>, the classic page uses <see cref="Rows"/> (its per-group
/// start/interval/checkbox); the separation field is common. Group ids that no longer run on the day are
/// dropped when the settings are applied, and groups missing from the stored state fall back to defaults.
/// </summary>
public sealed class DrawSettings
{
    /// <summary>The attribute kept off consecutive start slots.</summary>
    public DrawSeparationField Separation { get; set; } = DrawSeparationField.Club;

    /// <summary>Lane page: "hh:mm:ss" global start of the first competitor in every lane.</summary>
    public string GlobalStart { get; set; } = "11:00:00";

    /// <summary>Lane page: "hh:mm:ss" gap between consecutive competitors.</summary>
    public string Interval { get; set; } = "00:01:00";

    /// <summary>Lane page: "hh:mm:ss" empty time left after each group on a lane.</summary>
    public string GroupGap { get; set; } = "00:00:00";

    /// <summary>Lane page: how many start groups the «Авто» button distributes into.</summary>
    public int AutoGroupCount { get; set; } = 5;

    /// <summary>Lane page: whether chips are drawn proportional to the member count (timeline view).</summary>
    public bool ProportionalHeights { get; set; }

    /// <summary>Lane page: the arrangement — one entry per lane, each an ordered list of group ids.</summary>
    public List<DrawLaneSettings> Lanes { get; set; } = [];

    /// <summary>Classic page: one entry per group row.</summary>
    public List<DrawGroupRowSettings> Rows { get; set; } = [];
}

/// <summary>One lane (start group) of the lane-based draw: the groups it holds, in start order.</summary>
public sealed class DrawLaneSettings
{
    public List<Guid> GroupIds { get; set; } = [];
}

/// <summary>One group row of the classic draw: whether it takes part, and its own start and interval.</summary>
public sealed class DrawGroupRowSettings
{
    public Guid GroupId { get; set; }

    public bool Selected { get; set; }

    public string Start { get; set; } = "11:00:00";

    public string Interval { get; set; } = "00:01:00";
}
