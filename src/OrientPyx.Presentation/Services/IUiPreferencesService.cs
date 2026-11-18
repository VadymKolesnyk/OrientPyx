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

    /// <summary>
    /// When on (the default), a finish read-out of a rental chip due back pops a toast — whatever page is open —
    /// so the finish judge takes the chip. This and the alerts below are edited in «Налаштування сповіщень» on the
    /// «Зчитка на фініші» auto-read panel; they belong to this computer (its speakers, its screen).
    /// </summary>
    bool CollectChipToast { get; set; }

    /// <summary>When on (the default), that read-out also plays the «дзінь-дон» (<see cref="FinishAlert.CollectChip"/>).</summary>
    bool CollectChipSound { get; set; }

    /// <summary>
    /// When on (the default), an unknown chip read out plays the three beeps (<see cref="FinishAlert.UnknownChip"/>) the
    /// moment it is read, before its «Невідомий чіп» dialog opens — the auto-read waits for that dialog.
    /// </summary>
    bool UnknownChipSound { get; set; }

    /// <summary>
    /// When on (the default), a rental chip due back also shows a Windows notification while the window is minimized or
    /// inactive (<see cref="IDesktopAlerts.ShowNotification"/>; Windows only).
    /// </summary>
    bool CollectChipSystemNotification { get; set; }

    /// <summary>
    /// When on (the default), an unknown chip also shows a Windows notification while the window is minimized or
    /// inactive — its click brings the window (with the «Невідомий чіп» dialog) to the front. Windows only.
    /// </summary>
    bool UnknownChipSystemNotification { get; set; }

    /// <summary>
    /// When on (the default), those alerts also flash the taskbar button (bounce the Dock icon) while the window is
    /// minimized or inactive.
    /// </summary>
    bool FlashWhenInactive { get; set; }

    /// <summary>
    /// The version «Пропустити цю версію» was pressed for in the «Доступна нова версія» dialog: the start-up check
    /// stays silent about it (a newer one is offered again). Null = nothing skipped. The check on the Settings page
    /// ignores it — asking there is explicit.
    /// </summary>
    string? SkippedUpdateVersion { get; set; }
}
