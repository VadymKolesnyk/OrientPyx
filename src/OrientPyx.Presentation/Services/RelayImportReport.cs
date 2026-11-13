using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// After a participant import that touched relay teams: one message with what was made and what still needs a hand —
/// teams without numbers (to be given later, in step with OCAD), runners without a leg (more runners than legs) and
/// relay-day runners the file gives no team.
/// </summary>
public static class RelayImportReport
{
    public static Task ShowAsync(
        ParticipantImportResult result, UofParticipantData data, IDialogService dialogs, ILocalizationService localization)
    {
        if (!result.HasRelay)
            return Task.CompletedTask;

        var lines = new List<string>();
        if (result.RelayTeamsCreated > 0)
            lines.Add(string.Format(localization.Get("ParticipantsImport.Relay.Teams"), result.RelayTeamsCreated));
        // A file whose relay rows carry no team numbers (UOF): the teams wait for their numbers.
        if (result.RelayTeamsCreated > 0
            && data.Participants.Any(p => (p.RelayDayNumbers.Count > 0 || p.RelayOnImportedDays)
                                          && p.RelayTeamName.Length > 0 && p.RelayTeamNumber is null))
            lines.Add(localization.Get("ParticipantsImport.Relay.NoNumbers"));
        if (result.RelayUnplaced > 0)
            lines.Add(string.Format(localization.Get("ParticipantsImport.Relay.Unplaced"), result.RelayUnplaced));
        if (result.RelayWithoutTeam > 0)
            lines.Add(string.Format(localization.Get("ParticipantsImport.Relay.WithoutTeam"), result.RelayWithoutTeam));

        return dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            localization, "ParticipantsImport.Relay.Title", "ParticipantsImport.Relay.Message", confirmKey: "Common.Ok")
        {
            IsMessage = true,
            MessageArgs = [string.Join(Environment.NewLine + Environment.NewLine, lines)]
        });
    }
}
