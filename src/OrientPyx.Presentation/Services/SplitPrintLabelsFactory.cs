using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Builds the localized captions the split-printout renderer draws around the values (the document itself is
/// values-only). Shared by the read-out log's print action and the print-settings dialog's test slip, so a test
/// print looks exactly like a real one.
/// </summary>
public static class SplitPrintLabelsFactory
{
    public static SplitPrintLabels Create(ILocalizationService localization) => new(
        ChipLabel: localization.Get("FinishRead.Print.Chip"),
        RentalChipLabel: localization.Get("FinishRead.Print.RentalChip"),
        StartLabel: localization.Get("FinishRead.Print.Start"),
        FinishLabel: localization.Get("FinishRead.Print.Finish"),
        ResultLabel: localization.Get("FinishRead.Print.Result"),
        DistanceLabel: localization.Get("FinishRead.Print.Distance"),
        AvgPaceLabel: localization.Get("FinishRead.Print.AvgPace"),
        StatusLabel: localization.Get("FinishRead.Col.Status"),
        MpDetailLabel: localization.Get("FinishRead.Print.MpDetail"),
        CountDetailLabel: localization.Get("FinishRead.Print.CountDetail"),
        NoCourseDetailLabel: localization.Get("FinishRead.Print.NoCourseDetail"),
        TotalPointsLabel: localization.Get("FinishRead.Print.TotalPoints"),
        VariantLabel: localization.Get("Splits.VariantCode"),
        ColSeq: localization.Get("FinishRead.Print.ColSeq"),
        ColCode: localization.Get("FinishRead.Print.ColCode"),
        ColElapsed: localization.Get("FinishRead.Print.ColElapsed"),
        ColLeg: localization.Get("FinishRead.Print.ColLeg"),
        ColDistance: localization.Get("FinishRead.Print.ColDistance"),
        ColPace: localization.Get("FinishRead.Print.ColPace"),
        ColPoints: localization.Get("FinishRead.Print.ColPoints"),
        CorrectOrderTitle: localization.Get("FinishRead.Print.CorrectOrder"),
        PlaceLabel: localization.Get("FinishRead.Print.Place"));
}
