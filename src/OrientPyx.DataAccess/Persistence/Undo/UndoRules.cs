using OrientPyx.BusinessLogic.Entities;

namespace OrientPyx.DataAccess.Persistence.Undo;

/// <summary>A Guid (or key) column that points at a row of another table — a relation by convention only (no FK).</summary>
internal sealed record UndoReferenceRule(Type Entity, string Property, Type Target);

/// <summary>
/// Every relation between event-database rows, checked against the state after a revert: a row the revert puts back
/// or changes must point at rows that exist then, and a row it deletes must not be referenced by rows the step did not
/// make. A test keeps the list complete — every <c>…Id</c> Guid property of the model (primary keys aside) is listed
/// here or in <see cref="NotReferences"/>.
/// </summary>
internal static class UndoReferenceRules
{
    public static readonly IReadOnlyList<UndoReferenceRule> All =
    [
        new(typeof(ParticipantDay), nameof(ParticipantDay.EventDayId), typeof(EventDay)),
        new(typeof(ParticipantDay), nameof(ParticipantDay.ParticipantId), typeof(Participant)),
        new(typeof(ParticipantDay), nameof(ParticipantDay.GroupId), typeof(Group)),
        new(typeof(ParticipantDay), nameof(ParticipantDay.RelayTeamId), typeof(RelayTeam)),
        new(typeof(Participant), nameof(Participant.RegionId), typeof(Region)),
        new(typeof(Participant), nameof(Participant.ClubId), typeof(Club)),
        new(typeof(Participant), nameof(Participant.DusshId), typeof(Dussh)),
        new(typeof(ParticipantDiscount), nameof(ParticipantDiscount.ParticipantId), typeof(Participant)),
        new(typeof(ParticipantDiscount), nameof(ParticipantDiscount.DiscountId), typeof(EntryFeeDiscount)),
        new(typeof(ParticipantFieldValue), nameof(ParticipantFieldValue.ParticipantId), typeof(Participant)),
        new(typeof(ParticipantFieldValue), nameof(ParticipantFieldValue.Key), typeof(CustomFieldDef)),
        new(typeof(ControlPoint), nameof(ControlPoint.EventDayId), typeof(EventDay)),
        new(typeof(FinishReadout), nameof(FinishReadout.EventDayId), typeof(EventDay)),
        new(typeof(GroupDaySettings), nameof(GroupDaySettings.EventDayId), typeof(EventDay)),
        new(typeof(GroupDaySettings), nameof(GroupDaySettings.GroupId), typeof(Group)),
        new(typeof(GroupCourseVariant), nameof(GroupCourseVariant.EventDayId), typeof(EventDay)),
        new(typeof(GroupCourseVariant), nameof(GroupCourseVariant.GroupId), typeof(Group)),
        new(typeof(RelayTeam), nameof(RelayTeam.EventDayId), typeof(EventDay)),
        new(typeof(RelayTeam), nameof(RelayTeam.GroupId), typeof(Group)),
        new(typeof(RelayTeam), nameof(RelayTeam.ClubId), typeof(Club)),
        new(typeof(RelayTeam), nameof(RelayTeam.RegionId), typeof(Region)),
        new(typeof(LegCourseAssignment), nameof(LegCourseAssignment.EventDayId), typeof(EventDay)),
        new(typeof(LegCourseAssignment), nameof(LegCourseAssignment.GroupId), typeof(Group)),
        new(typeof(ResultProtocolSettingsRow), nameof(ResultProtocolSettingsRow.EventDayId), typeof(EventDay)),
        new(typeof(StartProtocolSettingsRow), nameof(StartProtocolSettingsRow.EventDayId), typeof(EventDay)),
        new(typeof(DrawSettingsRow), nameof(DrawSettingsRow.EventDayId), typeof(EventDay)),
        new(typeof(PluginSettingsRow), nameof(PluginSettingsRow.EventDayId), typeof(EventDay)),
    ];

    /// <summary>Guid <c>…Id</c> properties that are not relations inside <c>event.db</c> (app-level ids in <c>app.db</c>).</summary>
    public static readonly IReadOnlySet<(Type Entity, string Property)> NotReferences = new HashSet<(Type, string)>
    {
        (typeof(GroupDaySettings), nameof(GroupDaySettings.PointsRuleId)),
        (typeof(CompetitionInfo), nameof(CompetitionInfo.DefaultPointsRuleId)),
    };

    public static UndoReferenceRule? Find(Type entity, string property)
        => All.FirstOrDefault(r => r.Entity == entity && r.Property == property);
}

/// <summary>
/// A uniqueness the service layer enforces (not the database): the value must not be held by another row in the same
/// scope, compared like the service compares it (<c>TextKey</c>: trim, case, Cyrillic/Latin look-alikes). Blank values
/// never collide.
/// </summary>
internal sealed record UndoUniqueRule(Type Entity, string Property, string? ScopeProperty);

/// <summary>The service-level uniqueness rules a revert must not break (a new one in the service belongs here too).</summary>
internal static class UndoUniqueRules
{
    public static readonly IReadOnlyList<UndoUniqueRule> All =
    [
        // A start number is unique within the competition.
        new(typeof(Participant), nameof(Participant.Number), null),
        // A chip is unique within a day.
        new(typeof(ParticipantDay), nameof(ParticipantDay.Chip), nameof(ParticipantDay.EventDayId)),
        // A rental chip number is listed once (the index is exact; the service also folds look-alikes).
        new(typeof(RentalChip), nameof(RentalChip.Number), null),
        // Group and directory names.
        new(typeof(Group), nameof(Group.Name), null),
        new(typeof(Region), nameof(Region.Name), null),
        new(typeof(Club), nameof(Club.Name), null),
        new(typeof(Dussh), nameof(Dussh.Name), null),
    ];
}
