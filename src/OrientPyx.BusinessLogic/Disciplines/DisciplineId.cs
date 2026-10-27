namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Identity of a competition type (discipline). A plain string so that disciplines can come from plugins
/// (<c>"&lt;pluginId&gt;.&lt;name&gt;"</c>) as well as the built-in set below. Stored as that string in the
/// event database (<c>EventDay.DefaultDiscipline</c>, <c>GroupDaySettings.DisciplineOverride</c>); the
/// built-in values equal the former enum names, so existing databases read unchanged.
/// <para>A <c>default(DisciplineId)</c> reads as <see cref="SetCourse"/>.</para>
/// </summary>
public readonly struct DisciplineId : IEquatable<DisciplineId>, IComparable<DisciplineId>, IComparable
{
    private readonly string? _value;

    public DisciplineId(string value) => _value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The stored identifier, e.g. <c>"SetCourse"</c> or <c>"missed-penalty.set-course"</c>.</summary>
    public string Value => _value ?? SetCourseName;

    private const string SetCourseName = "SetCourse";

    /// <summary>Classic: competitor visits all control points in a prescribed order.</summary>
    public static readonly DisciplineId SetCourse = new(SetCourseName);

    /// <summary>Score: competitor picks any control points; ranked by count (then time).</summary>
    public static readonly DisciplineId ScoreByCount = new("ScoreByCount");

    /// <summary>Score by time: points collected within a time limit, penalty per minute late.</summary>
    public static readonly DisciplineId ScoreByTime = new("ScoreByTime");

    /// <summary>Rogaine: long score format run by teams, ranked by points collected.</summary>
    public static readonly DisciplineId Rogaine = new("Rogaine");

    /// <summary>Mixed (змішаний): the course order is a pattern mixing prescribed order and free choice.</summary>
    public static readonly DisciplineId Mixed = new("Mixed");

    /// <summary>Scatter (розсіювання): several valid orders per group, auto-detected per runner.</summary>
    public static readonly DisciplineId Scatter = new("Scatter");

    /// <summary>Relay (естафета): teams of runners on consecutive legs; leg N starts from leg N−1's finish.</summary>
    public static readonly DisciplineId Relay = new("Relay");

    public bool Equals(DisciplineId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is DisciplineId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(DisciplineId other) => string.CompareOrdinal(Value, other.Value);

    public int CompareTo(object? obj) => obj is DisciplineId other ? CompareTo(other) : 1;

    public static bool operator ==(DisciplineId left, DisciplineId right) => left.Equals(right);

    public static bool operator !=(DisciplineId left, DisciplineId right) => !left.Equals(right);

    public override string ToString() => Value;
}
