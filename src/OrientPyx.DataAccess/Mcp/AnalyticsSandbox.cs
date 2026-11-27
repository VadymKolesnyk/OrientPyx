using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OrientPyx.BusinessLogic.Interfaces;
using SQLitePCL;

namespace OrientPyx.DataAccess.Mcp;

/// <summary>
/// The read-only SQL sandbox of the assistant (plan §5.3, М12): an in-memory SQLite per (competition, data version, days)
/// holding only the analytics tables — it never sees event.db or app.db. While tables are loaded everything is allowed;
/// a query runs under an authorizer that allows only SELECT / READ / FUNCTION / RECURSIVE (so no INSERT, PRAGMA, ATTACH,
/// a second statement doing anything else), ATTACH is limited to 0 databases, strings to 10 MB, the SQL text to 100 KB,
/// and a timer interrupts it after the time-out. No process-wide heap limit: the event databases share this SQLite.
/// </summary>
public sealed class AnalyticsSandbox : IAnalyticsSandbox, IDisposable
{
    private const int MaxCached = 4;

    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    public async Task<AnalyticsQueryResult> QueryAsync(AnalyticsQuery query, CancellationToken cancellationToken = default)
    {
        var sql = (query.Sql ?? string.Empty).Trim().TrimEnd(';').Trim();
        if (sql.Length == 0)
            throw new AnalyticsQueryException("The SQL is empty.");
        if (sql.Length > 100_000)
            throw new AnalyticsQueryException("The SQL is longer than 100 KB.");

        var entry = Acquire(query.CacheKey);
        await entry.Lock.WaitAsync(cancellationToken);
        try
        {
            foreach (var table in query.TableNames)
            {
                if (entry.Built.Contains(table) || !Mentions(sql, table))
                    continue;
                var data = await query.BuildTable(table, cancellationToken);
                entry.Load(data);
            }
            return entry.Run(sql, query.MaxRows, query.MaxBytes, query.Timeout ?? TimeSpan.FromSeconds(5), cancellationToken);
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    public void Invalidate(string cacheKeyPrefix)
    {
        List<Entry> gone;
        lock (_gate)
        {
            gone = _entries.Where(e => e.Key.StartsWith(cacheKeyPrefix, StringComparison.Ordinal)).ToList();
            _entries.RemoveAll(gone.Contains);
        }
        foreach (var e in gone)
            e.DisposeWhenFree();
    }

    public void Dispose() => Invalidate(string.Empty);

    private Entry Acquire(string key)
    {
        List<Entry> evicted = [];
        Entry entry;
        lock (_gate)
        {
            entry = _entries.FirstOrDefault(e => e.Key == key) ?? new Entry(key);
            _entries.Remove(entry);
            _entries.Insert(0, entry);
            // A newer data version of the same competition makes the older ones useless; beyond that keep a few.
            var competition = key.Split('|')[0] + "|";
            evicted.AddRange(_entries.Skip(1).Where(e => e.Key.StartsWith(competition, StringComparison.Ordinal)));
            evicted.AddRange(_entries.Except(evicted).Skip(MaxCached));
            _entries.RemoveAll(evicted.Contains);
        }
        foreach (var e in evicted)
            e.DisposeWhenFree();
        return entry;
    }

    private static bool Mentions(string sql, string table) =>
        Regex.IsMatch(sql, $@"(?<![A-Za-z0-9_]){Regex.Escape(table)}(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);

    /// <summary>One cached database: its connection, the tables loaded so far, a lock (one query at a time).</summary>
    private sealed class Entry
    {
        private readonly SqliteConnection _connection;
        // Kept in a field: the native side holds only a pointer to it.
        private readonly strdelegate_authorizer _authorizer;
        private volatile bool _loading;
        private int _disposed;

        public Entry(string key)
        {
            Key = key;
            Batteries_V2.Init();
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            var db = _connection.Handle!;
            raw.sqlite3_limit(db, raw.SQLITE_LIMIT_ATTACHED, 0);
            raw.sqlite3_limit(db, raw.SQLITE_LIMIT_LENGTH, 10_000_000);
            raw.sqlite3_limit(db, raw.SQLITE_LIMIT_SQL_LENGTH, 100_000);
            _authorizer = (_, action, _, _, _, _) => Authorize(action);
            raw.sqlite3_set_authorizer(db, _authorizer, null);
            AddFunctions(_connection);
        }

        public string Key { get; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public HashSet<string> Built { get; } = new(StringComparer.OrdinalIgnoreCase);

        private int Authorize(int action)
        {
            if (_loading)
                return action == raw.SQLITE_ATTACH ? raw.SQLITE_DENY : raw.SQLITE_OK;
            return action is raw.SQLITE_SELECT or raw.SQLITE_READ or raw.SQLITE_FUNCTION or raw.SQLITE_RECURSIVE
                ? raw.SQLITE_OK
                : raw.SQLITE_DENY;
        }

        public void Load(AnalyticsTable table)
        {
            _loading = true;
            try
            {
                using var tx = _connection.BeginTransaction();
                var columns = table.Columns;
                using (var create = _connection.CreateCommand())
                {
                    create.Transaction = tx;
                    create.CommandText = $"DROP TABLE IF EXISTS \"{table.Name}\"; CREATE TABLE \"{table.Name}\" (" +
                        string.Join(", ", columns.Select(c => $"\"{c.Name}\" {SqlType(c.Type)}")) + ")";
                    create.ExecuteNonQuery();
                }
                if (table.Rows.Count > 0)
                {
                    using var insert = _connection.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = $"INSERT INTO \"{table.Name}\" VALUES (" + string.Join(", ", columns.Select((_, i) => "$p" + i)) + ")";
                    var parameters = columns.Select((_, i) => insert.Parameters.Add("$p" + i, SqliteType.Text)).ToArray();
                    insert.Prepare();
                    foreach (var row in table.Rows)
                    {
                        for (var i = 0; i < parameters.Length; i++)
                        {
                            var value = i < row.Length ? row[i] : null;
                            parameters[i].SqliteType = value switch
                            {
                                long or int => SqliteType.Integer,
                                double or float or decimal => SqliteType.Real,
                                _ => SqliteType.Text,
                            };
                            parameters[i].Value = value ?? DBNull.Value;
                        }
                        insert.ExecuteNonQuery();
                    }
                }
                tx.Commit();
                Built.Add(table.Name);
            }
            finally
            {
                _loading = false;
            }
        }

        public AnalyticsQueryResult Run(string sql, int maxRows, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var db = _connection.Handle!;
            using var timer = new Timer(_ => raw.sqlite3_interrupt(db), null, timeout, Timeout.InfiniteTimeSpan);
            using var cancel = cancellationToken.Register(() => raw.sqlite3_interrupt(db));
            try
            {
                Check(db, sql);
                using var command = _connection.CreateCommand();
                command.CommandText = sql;
                using var reader = command.ExecuteReader();
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                var rows = new List<object?[]>();
                var total = 0;
                var bytes = 0;
                var truncated = false;
                while (reader.Read())
                {
                    total++;
                    if (truncated || rows.Count >= maxRows)
                    {
                        truncated = true;
                        if (total > 100_000)
                            break; // counting the rest of a huge result is not worth the time
                        continue;
                    }
                    var row = new object?[columns.Count];
                    for (var i = 0; i < row.Length; i++)
                    {
                        row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
                        {
                            byte[] blob => $"<blob {blob.Length} bytes>",
                            string s when s.Length > 2000 => s[..2000] + "…",
                            var v => v,
                        };
                        bytes += row[i] switch { null => 4, string s => s.Length + 3, _ => 12 };
                    }
                    rows.Add(row);
                    if (bytes > maxBytes)
                        truncated = true;
                }
                return new AnalyticsQueryResult(columns, rows, total, truncated);
            }
            catch (SqliteException ex)
            {
                var message = ex.SqliteErrorCode switch
                {
                    9 => $"The query was stopped after {timeout.TotalSeconds:0} s. Simplify it (filter early, avoid cross joins).",
                    23 => "Only a SELECT that reads the tables is allowed (no INSERT / UPDATE / DELETE / CREATE / PRAGMA / ATTACH).",
                    _ => ex.Message.Replace("SQLite Error " + ex.SqliteErrorCode + ": ", string.Empty, StringComparison.Ordinal),
                };
                throw new AnalyticsQueryException(message);
            }
        }

        // Exactly one statement, and one SQLite itself calls read-only (sqlite3_stmt_readonly) — the authorizer refuses the
        // rest, this refuses a second statement hidden after the first and anything that would write.
        private static void Check(sqlite3 db, string sql)
        {
            var rc = raw.sqlite3_prepare_v2(db, sql, out var statement, out var tail);
            try
            {
                if (rc != raw.SQLITE_OK)
                    throw new AnalyticsQueryException(raw.sqlite3_errmsg(db).utf8_to_string());
                if (statement.IsInvalid)
                    throw new AnalyticsQueryException("The SQL holds no statement.");
                if (!string.IsNullOrWhiteSpace(StripComments(tail)))
                    throw new AnalyticsQueryException("Only one statement per query.");
                if (raw.sqlite3_stmt_readonly(statement) == 0)
                    throw new AnalyticsQueryException("Only a SELECT that reads the tables is allowed.");
            }
            finally
            {
                statement.Dispose();
            }
        }

        private static string StripComments(string? sql)
        {
            var text = sql ?? string.Empty;
            text = Regex.Replace(text, @"--[^\r\n]*", string.Empty);
            text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return text.Trim().Trim(';').Trim();
        }

        public void DisposeWhenFree()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _ = Task.Run(async () =>
            {
                await Lock.WaitAsync();
                try
                {
                    _connection.Dispose();
                }
                finally
                {
                    Lock.Release();
                }
            });
        }

        private static string SqlType(AnalyticsType type) => type switch
        {
            AnalyticsType.Integer or AnalyticsType.Boolean => "INTEGER",
            AnalyticsType.Real => "REAL",
            _ => "TEXT",
        };

        // median(x), percentile(x, p): the aggregates SQLite lacks and every leg / time analysis wants. The seed is
        // null so each group starts with its own list.
        private static void AddFunctions(SqliteConnection connection)
        {
            connection.CreateAggregate<object?, List<double>?, double?>(
                "median",
                null,
                (acc, value) =>
                {
                    acc ??= [];
                    if (ToDouble(value) is { } d)
                        acc.Add(d);
                    return acc;
                },
                acc => Percentile(acc, 50),
                isDeterministic: true);
            connection.CreateAggregate<object?, object?, (List<double>? Values, double P), double?>(
                "percentile",
                (null, 50),
                (acc, value, p) =>
                {
                    var list = acc.Values ?? [];
                    if (ToDouble(value) is { } d)
                        list.Add(d);
                    return (list, ToDouble(p) ?? 50);
                },
                acc => Percentile(acc.Values, acc.P),
                isDeterministic: true);
        }

        private static double? ToDouble(object? value) => value switch
        {
            null or DBNull => null,
            long l => l,
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };

        private static double? Percentile(List<double>? values, double p)
        {
            if (values is null || values.Count == 0)
                return null;
            values.Sort();
            var rank = Math.Clamp(p, 0, 100) / 100 * (values.Count - 1);
            var lo = (int)Math.Floor(rank);
            var hi = (int)Math.Ceiling(rank);
            return values[lo] + (values[hi] - values[lo]) * (rank - lo);
        }
    }
}
