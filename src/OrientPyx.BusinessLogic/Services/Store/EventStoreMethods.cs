using System.Reflection;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.BusinessLogic.Services.Store;

/// <summary>
/// The classification of every <see cref="IEventStore"/> method: a read, or a write and which <see cref="DataArea"/>s it
/// touches. An explicit map, not a name heuristic — the change bus publishes from it (<see cref="NotifyingEventStore"/>),
/// the station proxy decides timeouts and idempotency keys from it, and a test fails when a method is added to the
/// interface without being listed here.
/// </summary>
public static class EventStoreMethods
{
    private static readonly IReadOnlyDictionary<string, DataArea[]> WriteAreas = new Dictionary<string, DataArea[]>(StringComparer.Ordinal)
    {
        // Competition metadata
        [nameof(IEventStore.SaveCompetitionInfoAsync)] = [DataArea.Competition],
        [nameof(IEventStore.SetHiddenAsync)] = [DataArea.Competition],
        [nameof(IEventStore.SetRosterEnabledAsync)] = [DataArea.Competition],
        [nameof(IEventStore.SetPointsRulesJsonAsync)] = [DataArea.Competition],
        [nameof(IEventStore.SetPaymentPerDayAsync)] = [DataArea.Competition, DataArea.Participants, DataArea.ParticipantDays],

        // Days
        [nameof(IEventStore.AddDayAsync)] = [DataArea.Days],
        [nameof(IEventStore.UpdateDayAsync)] = [DataArea.Days],
        [nameof(IEventStore.UpdateDayNumberAsync)] = [DataArea.Days],
        [nameof(IEventStore.DeleteDayAsync)] = [DataArea.Days],

        // Control points
        [nameof(IEventStore.AddControlPointAsync)] = [DataArea.ControlPoints],
        [nameof(IEventStore.AddControlPointsAsync)] = [DataArea.ControlPoints],
        [nameof(IEventStore.ReplaceControlPointsAsync)] = [DataArea.ControlPoints],
        [nameof(IEventStore.UpdateControlPointAsync)] = [DataArea.ControlPoints],
        [nameof(IEventStore.DeleteControlPointAsync)] = [DataArea.ControlPoints],
        [nameof(IEventStore.SetControlPointsDisabledAsync)] = [DataArea.ControlPoints],

        // Groups
        [nameof(IEventStore.AddGroupAsync)] = [DataArea.Groups],
        [nameof(IEventStore.UpdateGroupAsync)] = [DataArea.Groups],
        [nameof(IEventStore.DeleteGroupAsync)] = [DataArea.Groups, DataArea.GroupDaySettings],
        [nameof(IEventStore.MergeGroupAsync)] = [DataArea.Groups, DataArea.GroupDaySettings, DataArea.ParticipantDays],
        [nameof(IEventStore.UpdateGroupEntryFeeAsync)] = [DataArea.Groups, DataArea.Fees],
        [nameof(IEventStore.UpdateGroupAgeWindowAsync)] = [DataArea.Groups],

        // Group-day settings
        [nameof(IEventStore.AddGroupDaySettingsAsync)] = [DataArea.GroupDaySettings],
        [nameof(IEventStore.AddGroupDaySettingsRangeAsync)] = [DataArea.GroupDaySettings],
        [nameof(IEventStore.UpdateGroupDaySettingsAsync)] = [DataArea.GroupDaySettings],
        [nameof(IEventStore.DeleteGroupDaySettingsAsync)] = [DataArea.GroupDaySettings],
        [nameof(IEventStore.UpdateRelaySettingsAsync)] = [DataArea.GroupDaySettings, DataArea.Relay],
        [nameof(IEventStore.UpdateRelayCourseSettingsAsync)] = [DataArea.GroupDaySettings, DataArea.Relay],

        // Course variants
        [nameof(IEventStore.ReplaceCourseVariantsForGroupAsync)] = [DataArea.CourseVariants],

        // Rental chips
        [nameof(IEventStore.AddRentalChipAsync)] = [DataArea.Chips],
        [nameof(IEventStore.AddRentalChipsAsync)] = [DataArea.Chips],
        [nameof(IEventStore.UpdateRentalChipAsync)] = [DataArea.Chips],
        [nameof(IEventStore.DeleteRentalChipAsync)] = [DataArea.Chips],
        [nameof(IEventStore.DeleteAllRentalChipsAsync)] = [DataArea.Chips],

        // Regions / clubs / ДЮСШ
        [nameof(IEventStore.AddRegionAsync)] = [DataArea.Regions],
        [nameof(IEventStore.UpdateRegionAsync)] = [DataArea.Regions],
        [nameof(IEventStore.DeleteRegionAsync)] = [DataArea.Regions],
        [nameof(IEventStore.ClearParticipantsRegionAsync)] = [DataArea.Participants],
        [nameof(IEventStore.AddClubAsync)] = [DataArea.Clubs],
        [nameof(IEventStore.UpdateClubAsync)] = [DataArea.Clubs],
        [nameof(IEventStore.DeleteClubAsync)] = [DataArea.Clubs],
        [nameof(IEventStore.ClearParticipantsClubAsync)] = [DataArea.Participants],
        [nameof(IEventStore.AddDusshAsync)] = [DataArea.Dusshes],
        [nameof(IEventStore.UpdateDusshAsync)] = [DataArea.Dusshes],
        [nameof(IEventStore.DeleteDusshAsync)] = [DataArea.Dusshes],
        [nameof(IEventStore.ClearParticipantsDusshAsync)] = [DataArea.Participants],

        // Participants
        [nameof(IEventStore.AddParticipantAsync)] = [DataArea.Participants],
        [nameof(IEventStore.DeleteAllParticipantsAsync)] = [DataArea.Participants, DataArea.ParticipantDays, DataArea.Relay],
        [nameof(IEventStore.UpdateParticipantAsync)] = [DataArea.Participants],
        [nameof(IEventStore.DeleteParticipantAsync)] = [DataArea.Participants, DataArea.ParticipantDays, DataArea.Relay],
        [nameof(IEventStore.SetParticipantPaysRaisedFeeAsync)] = [DataArea.Participants, DataArea.Fees],
        [nameof(IEventStore.SetParticipantsCheckedInAsync)] = [DataArea.Participants],
        [nameof(IEventStore.SetParticipantDiscountAsync)] = [DataArea.Participants, DataArea.Fees],
        [nameof(IEventStore.SetParticipantNumbersBatchAsync)] = [DataArea.Participants],
        [nameof(IEventStore.ImportParticipantsBatchAsync)] =
        [
            DataArea.Participants, DataArea.ParticipantDays, DataArea.Groups, DataArea.GroupDaySettings,
            DataArea.Regions, DataArea.Clubs, DataArea.Dusshes, DataArea.Relay,
        ],
        [nameof(IEventStore.SetParticipantFieldValueAsync)] = [DataArea.Participants, DataArea.CustomFields],

        // Participant-day links
        [nameof(IEventStore.SetParticipantDayGroupAsync)] = [DataArea.ParticipantDays],
        [nameof(IEventStore.AddParticipantDayAsync)] = [DataArea.ParticipantDays],
        [nameof(IEventStore.UpdateParticipantDayAsync)] = [DataArea.ParticipantDays],
        [nameof(IEventStore.SetParticipantDayResultStatusAsync)] = [DataArea.ParticipantDays],
        [nameof(IEventStore.SetParticipantDayBonusAsync)] = [DataArea.ParticipantDays],
        [nameof(IEventStore.SetParticipantDayPaymentAsync)] = [DataArea.ParticipantDays, DataArea.Fees],
        [nameof(IEventStore.SetParticipantDayRaisedFeeAsync)] = [DataArea.ParticipantDays, DataArea.Fees],
        [nameof(IEventStore.SetParticipantDayChipsBatchAsync)] = [DataArea.ParticipantDays],
        [nameof(IEventStore.CopyParticipantsBetweenDaysAsync)] = [DataArea.ParticipantDays, DataArea.GroupDaySettings, DataArea.Groups],
        [nameof(IEventStore.SetParticipantDayStartTimesBatchAsync)] = [DataArea.ParticipantDays],
        [nameof(IEventStore.DeleteParticipantDayAsync)] = [DataArea.ParticipantDays, DataArea.Relay],

        // Finish read-outs
        [nameof(IEventStore.AddFinishReadoutsAsync)] = [DataArea.Readouts],
        [nameof(IEventStore.UpdateFinishReadoutAsync)] = [DataArea.Readouts],
        [nameof(IEventStore.DeleteFinishReadoutsForDayAsync)] = [DataArea.Readouts],

        // Entry fees
        [nameof(IEventStore.AddChipPriceOverrideAsync)] = [DataArea.Fees],
        [nameof(IEventStore.UpdateChipPriceOverrideAsync)] = [DataArea.Fees],
        [nameof(IEventStore.DeleteChipPriceOverrideAsync)] = [DataArea.Fees],
        [nameof(IEventStore.AddEntryFeeDiscountAsync)] = [DataArea.Fees],
        [nameof(IEventStore.UpdateEntryFeeDiscountAsync)] = [DataArea.Fees],
        [nameof(IEventStore.DeleteEntryFeeDiscountAsync)] = [DataArea.Fees],

        // Per-competition settings blobs
        [nameof(IEventStore.SaveResultProtocolJsonAsync)] = [DataArea.Settings],
        [nameof(IEventStore.SaveStartProtocolJsonAsync)] = [DataArea.Settings],
        [nameof(IEventStore.SaveDrawSettingsJsonAsync)] = [DataArea.Settings],
        [nameof(IEventStore.SaveSummaryProtocolJsonAsync)] = [DataArea.Settings],
        [nameof(IEventStore.SaveStatementJsonAsync)] = [DataArea.Settings],
        [nameof(IEventStore.SaveChipScanJsonAsync)] = [DataArea.Settings],
        [nameof(IEventStore.SaveOnlinePublishJsonAsync)] = [DataArea.Settings],
        [nameof(IEventStore.SaveDisciplineSettingsAsync)] = [DataArea.Settings],
        [nameof(IEventStore.SaveImportMappingJsonAsync)] = [DataArea.Settings],
        [nameof(IEventStore.SaveMonitorJsonAsync)] = [DataArea.Settings],

        // Additional participant fields
        [nameof(IEventStore.AddCustomFieldDefAsync)] = [DataArea.CustomFields],
        [nameof(IEventStore.UpdateCustomFieldDefAsync)] = [DataArea.CustomFields],
        [nameof(IEventStore.DeleteCustomFieldDefAsync)] = [DataArea.CustomFields, DataArea.Participants],

        // Relay
        [nameof(IEventStore.AddRelayTeamsAsync)] = [DataArea.Relay],
        [nameof(IEventStore.UpdateRelayTeamAsync)] = [DataArea.Relay, DataArea.ParticipantDays],
        [nameof(IEventStore.DeleteRelayTeamAsync)] = [DataArea.Relay, DataArea.ParticipantDays],
        [nameof(IEventStore.DeleteRelayTeamsForGroupDayAsync)] = [DataArea.Relay, DataArea.ParticipantDays],
        [nameof(IEventStore.UpsertLegCourseAssignmentsAsync)] = [DataArea.Relay],
        [nameof(IEventStore.AssignRelayLegAsync)] = [DataArea.Relay, DataArea.ParticipantDays],
    };

    private static readonly IReadOnlySet<string> ReadNames = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(IEventStore.EnsureCreatedAsync),
        nameof(IEventStore.CheckpointAsync),
        nameof(IEventStore.ReleaseAsync),
        nameof(IEventStore.GetCompetitionInfoAsync),
        nameof(IEventStore.GetDaysAsync),
        nameof(IEventStore.GetControlPointsAsync),
        nameof(IEventStore.GetGroupsAsync),
        nameof(IEventStore.GetGroupDaySettingsAsync),
        nameof(IEventStore.CountGroupDaySettingsForGroupAsync),
        nameof(IEventStore.GetCourseVariantsAsync),
        nameof(IEventStore.GetRentalChipsAsync),
        nameof(IEventStore.GetRegionsAsync),
        nameof(IEventStore.GetClubsAsync),
        nameof(IEventStore.GetDusshesAsync),
        nameof(IEventStore.GetParticipantsAsync),
        nameof(IEventStore.GetParticipantDiscountsAsync),
        nameof(IEventStore.GetParticipantDaysAsync),
        nameof(IEventStore.GetDaySnapshotAsync),
        nameof(IEventStore.GetAllParticipantDaysAsync),
        nameof(IEventStore.CountParticipantDaysForParticipantAsync),
        nameof(IEventStore.GetFinishReadoutsAsync),
        nameof(IEventStore.GetChipPriceOverridesAsync),
        nameof(IEventStore.GetEntryFeeDiscountsAsync),
        nameof(IEventStore.GetResultProtocolJsonAsync),
        nameof(IEventStore.GetStartProtocolJsonAsync),
        nameof(IEventStore.GetDrawSettingsJsonAsync),
        nameof(IEventStore.GetSummaryProtocolJsonAsync),
        nameof(IEventStore.GetStatementJsonAsync),
        nameof(IEventStore.GetChipScanJsonAsync),
        nameof(IEventStore.GetOnlinePublishJsonAsync),
        nameof(IEventStore.GetDisciplineSettingsAsync),
        nameof(IEventStore.GetCustomFieldDefsAsync),
        nameof(IEventStore.GetParticipantFieldValuesAsync),
        nameof(IEventStore.GetImportMappingJsonAsync),
        nameof(IEventStore.GetMonitorJsonAsync),
        nameof(IEventStore.GetRelayTeamsAsync),
        nameof(IEventStore.GetLegCourseAssignmentsAsync),
    };

    /// <summary>Methods executed on the host as background jobs (they report progress through <c>IProgress&lt;&gt;</c>).</summary>
    public static readonly IReadOnlySet<string> JobMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(IEventStore.ImportParticipantsBatchAsync),
    };

    /// <summary>Methods a station executes locally as no-ops: they act on the host's database files.</summary>
    public static readonly IReadOnlySet<string> HostFileMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(IEventStore.CheckpointAsync),
        nameof(IEventStore.ReleaseAsync),
    };

    /// <summary>The <see cref="IAppStore"/> methods that change the tables feeding the results (<see cref="DataArea.AppData"/>).</summary>
    public static readonly IReadOnlySet<string> AppDataWrites = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(IAppStore.SeedRanksIfEmptyAsync),
        nameof(IAppStore.AddRankAsync),
        nameof(IAppStore.UpdateRankAsync),
        nameof(IAppStore.DeleteRankAsync),
        nameof(IAppStore.AppendRankAsync),
        nameof(IAppStore.SeedPointsRulesIfEmptyAsync),
        nameof(IAppStore.AddPointsRuleAsync),
        nameof(IAppStore.UpdatePointsRuleAsync),
        nameof(IAppStore.DeletePointsRuleAsync),
        nameof(IAppStore.AppendPointsRuleAsync),
        nameof(IAppStore.SeedRankQualificationIfEmptyAsync),
        nameof(IAppStore.AddRankQualificationRowAsync),
        nameof(IAppStore.UpdateRankQualificationRowAsync),
        nameof(IAppStore.DeleteRankQualificationRowAsync),
        nameof(IAppStore.SaveRankConditionsAsync),
    };

    public static IReadOnlyDictionary<string, DataArea[]> Writes => WriteAreas;

    public static IReadOnlySet<string> Reads => ReadNames;

    public static bool IsWrite(string methodName) => WriteAreas.ContainsKey(methodName);

    public static bool IsRead(string methodName) => ReadNames.Contains(methodName);

    /// <summary>The areas a write touches; empty for a read.</summary>
    public static IReadOnlyList<DataArea> AreasOf(string methodName)
        => WriteAreas.TryGetValue(methodName, out var areas) ? areas : [];

    /// <summary>Every method of <see cref="IEventStore"/> that is neither a listed read nor a listed write — must be empty.</summary>
    public static IReadOnlyList<string> Unclassified()
        => typeof(IEventStore).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(n => !IsWrite(n) && !IsRead(n))
            .Distinct()
            .ToList();
}
