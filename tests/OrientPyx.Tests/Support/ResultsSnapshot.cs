using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.Tests.Support;

/// <summary>
/// Every day's computed results of the current competition in the exact shape the app's Debug
/// «Файл → (Debug) Знімок результатів…» writes, so a snapshot from a running build and one from the tests
/// are comparable with <c>scripts/diff-results.ps1</c>. Sorted, so equal data gives byte-equal text.
/// </summary>
public static class ResultsSnapshot
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    public static async Task<string> BuildAsync(ICompetitionEditorService editor, ISessionService session, IReadOnlyList<EventDay> days)
    {
        var output = new List<object>();
        foreach (var day in days.OrderBy(d => d.Number))
        {
            await session.SetCurrentDayAsync(day);
            var data = await editor.GetResultProtocolDataAsync(day.Id);
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
        return JsonSerializer.Serialize(output, Json);
    }
}
