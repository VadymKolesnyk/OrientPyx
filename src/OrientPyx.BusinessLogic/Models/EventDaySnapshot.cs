using OrientPyx.BusinessLogic.Entities;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Every table a per-day read needs, captured in ONE database transaction so the whole set comes from a
/// single consistent snapshot.
/// <para>
/// The protocol/roster reads used to issue one query per table, each on its own connection. Under SQLite's
/// WAL journal every connection takes its own read snapshot, so two overlapping reads (a page reloaded while
/// an earlier load was still running, a draw saving in between) could mix rows from BEFORE and AFTER a write
/// into one result — producing duplicated and mis-ordered rows in the built document. Reading everything
/// inside one transaction removes that window: all lists below are guaranteed to agree with each other.
/// </para>
/// </summary>
public sealed record EventDaySnapshot
{
    /// <summary>The day's participant links (day membership, group, chip, start time), in day-grid order.</summary>
    public IReadOnlyList<ParticipantDay> Links { get; init; } = [];

    /// <summary>Every participant in the competition (links resolve into these by id).</summary>
    public IReadOnlyList<Participant> Participants { get; init; } = [];

    public IReadOnlyList<Group> Groups { get; init; } = [];

    /// <summary>The day's per-group settings (membership + course/discipline overrides), in display order.</summary>
    public IReadOnlyList<GroupDaySettings> GroupDaySettings { get; init; } = [];

    public IReadOnlyList<ControlPoint> ControlPoints { get; init; } = [];

    public IReadOnlyList<Region> Regions { get; init; } = [];

    public IReadOnlyList<Club> Clubs { get; init; } = [];

    public IReadOnlyList<Dussh> Dusshes { get; init; } = [];

    public IReadOnlyList<EventDay> Days { get; init; } = [];

    /// <summary>Competition metadata (name, organisation, officials); null when none is stored yet.</summary>
    public CompetitionInfo? Info { get; init; }
}
