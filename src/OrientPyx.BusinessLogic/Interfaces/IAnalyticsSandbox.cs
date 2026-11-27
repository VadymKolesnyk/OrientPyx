namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>The SQL type of an analytics column.</summary>
public enum AnalyticsType
{
    Text,
    Integer,
    Real,
    /// <summary>Stored as 0 / 1.</summary>
    Boolean,
}

/// <summary>One column of an analytics table, with its meaning for <c>describe_data</c>.</summary>
public sealed record AnalyticsColumn(string Name, AnalyticsType Type, string Description);

/// <summary>A flat table built from the competition (plan §5.2); <paramref name="Rows"/> hold values in column order.</summary>
public sealed record AnalyticsTable(string Name, IReadOnlyList<AnalyticsColumn> Columns, IReadOnlyList<object?[]> Rows);

/// <summary>
/// One query: <paramref name="CacheKey"/> = competition + data version (a new version builds the tables again);
/// <paramref name="BuildTable"/> builds a table on demand — only the tables the SQL names are built.
/// </summary>
public sealed record AnalyticsQuery(
    string CacheKey,
    string Sql,
    IReadOnlyList<string> TableNames,
    Func<string, CancellationToken, Task<AnalyticsTable>> BuildTable,
    int MaxRows = 500,
    int MaxBytes = 256 * 1024,
    TimeSpan? Timeout = null);

/// <summary>The answer: column names, rows (at most <c>MaxRows</c>), how many rows the query had, whether it was cut.</summary>
public sealed record AnalyticsQueryResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, int TotalRows, bool Truncated);

/// <summary>The SQL itself was refused or failed — the message is SQLite's (or the sandbox's) for the assistant to fix.</summary>
public sealed class AnalyticsQueryException(string message) : Exception(message);

/// <summary>
/// The read-only SQL sandbox (plan §5.3): an in-memory SQLite holding only the analytics tables — never event.db or
/// app.db — one SELECT per call, an authorizer, limits, a timeout. Implemented in DataAccess (SQLite lives there).
/// </summary>
public interface IAnalyticsSandbox
{
    Task<AnalyticsQueryResult> QueryAsync(AnalyticsQuery query, CancellationToken cancellationToken = default);

    /// <summary>Drops the cached tables of every key starting with <paramref name="cacheKeyPrefix"/> (a competition).</summary>
    void Invalidate(string cacheKeyPrefix);
}
