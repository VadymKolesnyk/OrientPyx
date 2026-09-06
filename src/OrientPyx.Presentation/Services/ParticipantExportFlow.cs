using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Default <see cref="IParticipantExportFlow"/>. Shows the format modal, then serialises the captured
/// view with the <see cref="ITabularWriter"/> registered for the chosen format (CSV in BusinessLogic,
/// .xlsx in DataAccess — resolved here from the injected set). Writing runs under the busy overlay. The
/// flow never touches the file system; it returns the bytes for the view to save.
/// </summary>
public sealed class ParticipantExportFlow : IParticipantExportFlow
{
    private readonly ILocalizationService _localization;
    private readonly ISessionService _session;
    private readonly IDialogService _dialogs;
    private readonly IBusyService _busy;
    private readonly IReadOnlyDictionary<ExportFormat, ITabularWriter> _writers;

    public ParticipantExportFlow(
        ILocalizationService localization,
        ISessionService session,
        IDialogService dialogs,
        IBusyService busy,
        IEnumerable<ITabularWriter> writers)
    {
        _localization = localization;
        _session = session;
        _dialogs = dialogs;
        _busy = busy;
        // One writer per format; the last registration wins if a format were ever registered twice.
        _writers = writers.ToDictionary(w => w.Format);
    }

    public async Task<ParticipantExportResult?> RunAsync(CsvParticipantData view, EventDay? day)
    {
        if (_session.CurrentEvent is null || view.Header.Count == 0)
            return null;

        var format = await _dialogs.ShowExportFormatAsync(new ExportFormatViewModel(_localization, view.Rows.Count));
        if (format is null)
            return null; // cancelled

        if (!_writers.TryGetValue(format.Value, out var writer))
            return null;

        var bytes = await _busy.RunAsync(() => Task.FromResult(writer.Write(view)));

        var (extension, mime) = format.Value switch
        {
            ExportFormat.Excel => ("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            _ => ("csv", "text/csv")
        };

        return new ParticipantExportResult(format.Value, bytes, SuggestedFileName(extension, day), extension, mime);
    }

    // "Учасники - <competition> - [День N - ]<day date>.<ext>" (see ExportFileName). A multi-day roster
    // has no single day, so it is named by the competition alone.
    private string SuggestedFileName(string extension, EventDay? day) => ExportFileName.Build(
        _localization, "Export.NamePart", _session.CurrentEvent?.Name, extension, day,
        date: _session.CurrentEvent?.StartDate,
        defaultNameKey: "Export.DefaultName");
}
