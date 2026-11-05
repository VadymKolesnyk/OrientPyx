namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>The part of a competition's data a write touched. Pages subscribe to the areas they show.</summary>
public enum DataArea
{
    Competition,
    Days,
    ControlPoints,
    Groups,
    GroupDaySettings,
    CourseVariants,
    Chips,
    Regions,
    Clubs,
    Dusshes,
    Participants,
    ParticipantDays,
    Readouts,
    Fees,
    Settings,
    CustomFields,
    Relay,
    /// <summary>App-database tables that feed the results (points rules, ranks, qualification). Published with
    /// <c>DataChangeBus.AnyEvent</c> as the event path.</summary>
    AppData,
    /// <summary>Everything — a station that fell behind the host's event buffer refreshes every page.</summary>
    Any,
}

/// <summary>
/// One data change: which competition (its folder path / key), which area, the competition's data version after the
/// write, who wrote it (<see cref="Origin"/> — the station or host name) and the global event number.
/// </summary>
public sealed record DataChange(string EventFolderPath, DataArea Area, long Version, string Origin, long Sequence = 0);

/// <summary>
/// The in-process change bus: every write through <c>IEventStore</c> publishes here (<c>NotifyingEventStore</c>), every
/// page that shows data listens. On a host the bus is also streamed to the stations; on a station the only publisher is
/// the host's stream. <see cref="Changed"/> is raised on the publishing thread — subscribers marshal to the UI themselves.
/// </summary>
public interface IDataChangeBus
{
    /// <summary>The data version of a competition (0 until something is written in this process).</summary>
    long CurrentVersion(string eventFolderPath);

    /// <summary>The number of the last published event (0 when none).</summary>
    long Sequence { get; }

    /// <summary>Publishes a change: bumps the competition's version and raises <see cref="Changed"/>.</summary>
    DataChange Publish(string eventFolderPath, DataArea area, string origin);

    /// <summary>
    /// Re-publishes an event received from elsewhere (a station replaying the host's stream): the version is taken as
    /// given, not bumped. Raises <see cref="Changed"/>.
    /// </summary>
    void Replay(DataChange change);

    /// <summary>The buffered events after <paramref name="sequence"/>; null when that number already fell out of the
    /// buffer (the caller must do a full refresh).</summary>
    IReadOnlyList<DataChange>? Since(long sequence);

    event EventHandler<DataChange>? Changed;
}
