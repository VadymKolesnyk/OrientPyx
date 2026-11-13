using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// After a participant import that put relay teams on a day whose own type is not the relay (the import made only the
/// relay groups relay): asks, per such day, whether to make the whole day a relay day. Every group keeps what it runs —
/// the groups without teams keep the former type as their own.
/// </summary>
public static class RelayDaySwitchOffer
{
    public static async Task RunAsync(
        ParticipantImportResult result, UofParticipantData data, ParticipantImportScope scope,
        ICompetitionEditorService editor, ISessionService session, IDisciplineStrategyProvider strategies,
        IDialogService dialogs, IBusyService busy, ILocalizationService localization)
    {
        if (!result.HasRelay)
            return;

        var offers = await busy.RunAsync(() => editor.GetRelayDaySwitchesAsync(data, scope));
        foreach (var offer in offers)
        {
            var current = DisciplineLabels.Of(strategies.For(offer.Current), localization);
            var relay = DisciplineLabels.Of(strategies.For(DisciplineId.Relay), localization);
            var kept = offer.KeptGroups.Count > 0
                ? Environment.NewLine + Environment.NewLine + string.Format(
                    localization.Get("ParticipantsImport.RelayDay.Kept"), current, string.Join(", ", offer.KeptGroups))
                : string.Empty;
            var switchIt = await dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                localization, "ParticipantsImport.RelayDay.Title", "ParticipantsImport.RelayDay.Message",
                confirmKey: "ParticipantsImport.RelayDay.Switch", cancelKey: "ParticipantsImport.RelayDay.Keep")
            {
                MessageArgs = [offer.DayNumber, current, relay, kept]
            });
            if (!switchIt)
                continue;

            var day = await busy.RunAsync(() => editor.SwitchDayToRelayAsync(offer.DayId));
            // The session caches the active day with its type: re-point it so the pages pick the relay up now.
            if (day is not null && session.CurrentDay?.Id == day.Id)
                await busy.RunAsync(() => session.SetCurrentDayAsync(day));
        }
    }
}
