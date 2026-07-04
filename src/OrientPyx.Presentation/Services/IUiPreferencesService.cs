namespace OrientPyx.Presentation.Services;

/// <summary>Where the finish-read splits/passage panel is docked relative to the log table.</summary>
public enum SplitsDock
{
    /// <summary>Below the table (default).</summary>
    Bottom,

    /// <summary>To the right of the table.</summary>
    Right
}

/// <summary>
/// App-wide UI preferences that persist across runs and competitions (so they live with the app, not in
/// a competition's <c>views.json</c>). Stored as a small <c>preferences.json</c> in the data folder.
/// Failures are swallowed — a preference is convenience state, never data.
/// </summary>
public interface IUiPreferencesService
{
    /// <summary>Which side the finish-read splits panel is docked on.</summary>
    SplitsDock SplitsDock { get; set; }

    /// <summary>Size of the finish-read splits panel along its docked edge (height when bottom, width when right), in DIPs.</summary>
    double SplitsSize { get; set; }

    /// <summary>Width of the prescribed-course column in the ordered splits layout, in DIPs; the passage list takes the rest.</summary>
    double SplitsPrescribedWidth { get; set; }
}
