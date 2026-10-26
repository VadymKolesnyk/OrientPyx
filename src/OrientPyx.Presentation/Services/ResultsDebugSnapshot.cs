using System.Text.Encodings.Web;
using System.Text.Json;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Developer safety net (Debug builds only show the menu item): writes every day's computed results of the open
/// competition to <c>events/&lt;id&gt;/_debug/results-&lt;yyyyMMdd-HHmmss&gt;.json</c>, sorted so two snapshots
/// of the same data are byte-identical. Compare two files with <c>scripts/diff-results.ps1</c> to prove a
/// refactoring left results unchanged (only fields that exist since before the plugin work are written, so a
/// snapshot from an older build stays comparable). Reads through the same <see cref="ICompetitionEditorService.GetResultProtocolDataAsync"/>
/// the protocols use.
/// </summary>
public sealed class ResultsDebugSnapshot
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;

    public ResultsDebugSnapshot(ICompetitionEditorService editor, ISessionService session)
    {
        _editor = editor;
        _session = session;
    }

    /// <summary>Writes the snapshot and returns its path; null when no competition is open.</summary>
    public async Task<string?> WriteAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is not { } current)
            return null;

        var days = await _editor.GetDaysAsync(cancellationToken);
        var output = new List<object>();
        foreach (var day in days.OrderBy(d => d.Number))
        {
            var data = await _editor.GetResultProtocolDataAsync(day.Id, cancellationToken);
            output.Add(new
            {
                Day = day.Number,
                Discipline = day.DefaultDiscipline.Value,
                Groups = data.Groups
                    .OrderBy(g => g.Name, StringComparer.Ordinal)
                    .Select(g => new
                    {
                        g.Name,
                        Rows = g.Rows
                            .OrderBy(r => r.Number, StringComparer.Ordinal)
                            .ThenBy(r => r.FullName, StringComparer.Ordinal)
                            .Select(r => new
                            {
                                r.Number,
                                r.FullName,
                                Status = r.Result.Status.ToString(),
                                Time = r.Result.ResultTime?.ToString(),
                                r.Result.Place,
                                r.Result.Score,
                                r.Result.Points,
                                r.Result.AwardedRank,
                                r.Result.OutOfCompetition
                            })
                    })
            });
        }

        var folder = Path.Combine(current.FolderPath, "_debug");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"results-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var json = JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        await File.WriteAllTextAsync(path, json, cancellationToken);
        return path;
    }
}
