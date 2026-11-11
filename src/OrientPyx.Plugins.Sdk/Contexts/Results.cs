using System.ComponentModel;
using OrientPyx.Plugins.Sdk.Toolkit;

namespace OrientPyx.Plugins.Sdk;

/// <summary>A read-only view of a competition participant for discipline rules.</summary>
public sealed class ParticipantView
{
    private static readonly IReadOnlyDictionary<string, string?> NoFields = new Dictionary<string, string?>();

    public string FullName { get; init; } = string.Empty;

    public string Number { get; init; } = string.Empty;

    /// <summary>Year of birth; null when the birth date is not known.</summary>
    public int? BirthYear { get; init; }

    public string Club { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;

    public string Team { get; init; } = string.Empty;

    /// <summary>The participant's additional fields, by key.</summary>
    public IReadOnlyDictionary<string, string?> Fields { get; init; } = NoFields;

    /// <summary>The value of an additional field; null when missing or blank.</summary>
    public string? Field(string key) =>
        Fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>
/// One competitor's result as discipline rules see it. <see cref="Set"/> stores discipline-specific values (shown
/// by <see cref="Discipline.Columns"/>); <see cref="MarkUnranked"/> keeps the result but gives it no place and no
/// points.
/// </summary>
public sealed class ResultRow
{
    private readonly Dictionary<string, object?> _values;

    public ResultRow() : this(new Dictionary<string, object?>())
    {
    }

    /// <summary>Wraps an existing value store (the host passes the result's stored values).</summary>
    public ResultRow(IDictionary<string, object?> values) =>
        _values = values as Dictionary<string, object?> ?? new Dictionary<string, object?>(values);

    /// <summary>Host key of the result.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Guid Id { get; init; }

    public ParticipantView? Participant { get; init; }

    public FinishStatus Status { get; init; }

    /// <summary>The actual result time (finish − start); null unless OK.</summary>
    public TimeSpan? Time { get; init; }

    /// <summary>The time the result is ranked by (after <see cref="Discipline.AdjustTime"/>).</summary>
    public TimeSpan? AdjustedTime { get; init; }

    /// <summary>Points («Бали») for a scoring format; null otherwise.</summary>
    public int? Score { get; init; }

    /// <summary>The place; written by ranking.</summary>
    public int? Place { get; set; }

    public bool OutOfCompetition { get; init; }

    /// <summary>The punched control codes in chip order.</summary>
    public IReadOnlyList<string> Punches { get; init; } = [];

    /// <summary>Every required control the run missed (maximum match); empty unless MP.</summary>
    public IReadOnlyList<string> Missed { get; init; } = [];

    /// <summary>Why the result is not ranked (see <see cref="MarkUnranked"/>); null when it is.</summary>
    public string? UnrankedReason { get; private set; }

    /// <summary>The discipline-specific values set on this row.</summary>
    public IReadOnlyDictionary<string, object?> Values => _values;

    /// <summary>Keeps the result and its time but gives it no place and no points (for a relay leg: the whole team).</summary>
    public void MarkUnranked(string reason) => UnrankedReason = reason;

    public void Set(string key, object? value) => _values[key] = value;

    public T? Get<T>(string key) =>
        _values.TryGetValue(key, out var value) ? SettingValues.ConvertTo<T>(value) : default;
}

/// <summary>What <see cref="Discipline.AdjustTime"/> sees for one OK result.</summary>
public sealed class AdjustContext
{
    public ParticipantView Participant { get; init; } = new();

    /// <summary>The actual time (for a relay leg: the leg time).</summary>
    public required TimeSpan Time { get; init; }

    /// <summary>The competition day's date (the day's date, else the earliest finish, else today).</summary>
    public DateOnly Date { get; init; }

    /// <summary>The relay leg number; null for an individual result.</summary>
    public int? Leg { get; init; }

    /// <summary>The result being adjusted; <see cref="ResultRow.Set"/> values become result columns.</summary>
    public required ResultRow Row { get; init; }

    public SettingValues Settings { get; init; } = SettingValues.Empty;

    public T Setting<T>(string key) => Settings.Get<T>(key);
}

/// <summary>One group's competitors for <see cref="Discipline.Rank"/>.</summary>
public sealed class RankingContext
{
    /// <summary>The group's name.</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>The group's competitors handed to ranking: for an individual discipline the placeable ones (the same
    /// rows as <see cref="Finished"/>); only <c>Place</c> is read back from <see cref="Discipline.Rank"/>.</summary>
    public IReadOnlyList<ResultRow> Competitors { get; init; } = [];

    /// <summary>The placeable competitors: OK, in competition, not marked unranked.</summary>
    public IReadOnlyList<ResultRow> Finished { get; init; } = [];

    public SettingValues Settings { get; init; } = SettingValues.Empty;

    /// <summary>
    /// For a relay (<see cref="CompetitorModel.TeamWithLegs"/>): the teams of the group. Each team's
    /// <see cref="TeamCompetitor.Row"/> is also in <see cref="Competitors"/> (and, when placeable, in
    /// <see cref="Finished"/>), so <see cref="RankBy"/> places teams. Empty for an individual format.
    /// </summary>
    public IReadOnlyList<TeamCompetitor> Teams { get; init; } = [];

    /// <summary>The host's own ranking data (used by the built-in fallback).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public object? HostState { get; init; }

    public T Setting<T>(string key) => Settings.Get<T>(key);

    /// <summary>Places <see cref="Finished"/> by <paramref name="order"/>: equal results share a place, the next skips.</summary>
    public void RankBy(IComparer<ResultRow> order)
    {
        foreach (var row in Finished)
            row.Place = null;
        foreach (var (row, place) in Ranking.AssignPlaces(Finished, order))
            row.Place = place;
    }
}

/// <summary>One relay team for ranking: its own result row (time = sum of the adjusted leg times) plus the leg
/// rows in leg order (null for a leg nobody runs).</summary>
public sealed class TeamCompetitor
{
    /// <summary>The team's result; its <see cref="ResultRow.Place"/> is the team's place.</summary>
    public required ResultRow Row { get; init; }

    /// <summary>The team number (100 in «100.2»); 0 while the team has no number yet (an imported team gets its
    /// numbers later).</summary>
    public int Number { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>The leg results in leg order (index 0 = leg 1); null where the leg is not assigned.</summary>
    public IReadOnlyList<ResultRow?> Members { get; init; } = [];

    /// <summary>True when some leg started from a restart (Р-29).</summary>
    public bool Restarted { get; init; }

    public TimeSpan? Time => Row.AdjustedTime;

    public FinishStatus Status => Row.Status;

    public int? Place => Row.Place;
}
