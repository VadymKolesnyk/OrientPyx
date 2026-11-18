using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Undo;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core.Undo;

/// <summary>
/// What the undo tests share: an editor call run as one undo step (the way the UI's <c>IUndoHistory.RunAsync</c> will),
/// the revert through <see cref="IEditHistoryService"/>, and a dump of a whole <c>event.db</c> to compare states.
/// The journal is process-wide, so every test builds its own competition and asserts only on its own steps.
/// </summary>
internal static class UndoKit
{
    public static IServiceProvider Sp => TestEnvironment.Instance.Services;

    public static ICompetitionEditorService Editor => Sp.GetRequiredService<ICompetitionEditorService>();

    public static IEventStore Store => Sp.GetRequiredService<IEventStore>();

    public static IEditHistoryService History => Sp.GetRequiredService<IEditHistoryService>();

    /// <summary>Runs <paramref name="action"/> as one undo step and returns the step's id.</summary>
    public static async Task<Guid> StepAsync(Func<Task> action)
    {
        var step = Guid.NewGuid();
        using (UndoScope.Enter(step))
            await action();
        return step;
    }

    /// <summary>Ctrl+Z (or, on a revert step, Ctrl+Y) of the session's competition.</summary>
    public static Task<UndoOutcome> RevertAsync(Guid step) => History.RevertAsync(step);

    /// <summary>Reverts and asserts the status; returns the revert step (what redo reverts in turn).</summary>
    public static async Task<Guid> RevertExpectAsync(Guid step, UndoRevertStatus expected, string? because = null)
    {
        var outcome = await RevertAsync(step);
        Assert.True(outcome.Result.Status == expected,
            $"{because ?? "revert"}: expected {expected}, got {outcome.Result.Status} {Describe(outcome.Result)}");
        return outcome.RevertStepId;
    }

    /// <summary>The conflicts of a result, readable in an assertion message.</summary>
    public static string Describe(UndoRevertResult result)
        => string.Join("; ", result.Conflicts.Select(c => $"{c.Kind} {c.Entity} «{c.Row}» {c.Field}: {c.Expected} → {c.Actual}"));
}

/// <summary>
/// Every row of every table of a competition's <c>event.db</c> (but the migrations history), rows sorted, read on a
/// connection of its own (no pooling, so nothing stays open). Two equal dumps = the same data.
/// </summary>
internal static class EventDbDump
{
    public static async Task<string> TakeAsync(string eventFolderPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(eventFolderPath, "event.db"),
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();

        var tables = new List<string>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' "
                               + "AND name <> '__EFMigrationsHistory' ORDER BY name";
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
        }

        var text = new StringBuilder();
        foreach (var table in tables)
        {
            var rows = new List<string>();
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = $"SELECT * FROM \"{table}\"";
                await using var reader = await select.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var cells = new string[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                        cells[i] = $"{reader.GetName(i)}={Format(reader.GetValue(i))}";
                    rows.Add(string.Join(" | ", cells));
                }
            }
            rows.Sort(StringComparer.Ordinal);
            text.Append("## ").Append(table).Append(" (").Append(rows.Count).AppendLine(")");
            foreach (var row in rows)
                text.AppendLine(row);
        }
        return text.ToString();
    }

    /// <summary>The rows of one table only (for a focused assertion).</summary>
    public static async Task<IReadOnlyList<string>> RowsAsync(string eventFolderPath, string table)
    {
        var dump = await TakeAsync(eventFolderPath);
        var lines = dump.Split(Environment.NewLine);
        var start = Array.FindIndex(lines, l => l.StartsWith($"## {table} (", StringComparison.Ordinal));
        if (start < 0)
            return [];
        return lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal) && l.Length > 0).ToList();
    }

    private static string Format(object value) => value switch
    {
        DBNull => "∅",
        byte[] bytes => Convert.ToHexString(bytes),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
