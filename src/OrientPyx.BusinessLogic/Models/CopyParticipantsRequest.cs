namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// One «копіювання учасників з дня в день» run: which day to read, which to write, and which per-day
/// fields to carry over. The group is always copied — membership without a group is meaningless here,
/// so a group missing from the target day is added to it (and created competition-wide if the name is
/// new). Every other field is opt-in; see <see cref="CopyParticipantsOptions"/>.
/// </summary>
public sealed record CopyParticipantsRequest(
    Guid SourceDayId,
    Guid TargetDayId,
    CopyParticipantsOptions Options);

/// <summary>
/// The per-field toggles of a copy run. Defaults mirror the dialog's initial state: chip and
/// out-of-competition on, payment and start time off.
/// </summary>
/// <param name="Chip">Carry the day's chip number. A chip already held by someone else on the target
/// day is skipped (chips stay unique per day).</param>
/// <param name="Payment">Carry the per-day payment note AND the raised-fee flag (they are one
/// "оплата" concept to the user). Only meaningful while the competition is in per-day payment mode.</param>
/// <param name="StartTime">Carry the start minute.</param>
/// <param name="OutOfCompetition">Carry the "поза конкурсом" flag.</param>
public sealed record CopyParticipantsOptions(
    bool Chip = true,
    bool Payment = false,
    bool StartTime = false,
    bool OutOfCompetition = true);

/// <summary>
/// What a copy run did: how many links it created, how many participants were already on the target day
/// (left untouched), how many groups it had to add to the target day, and how many chips it had to skip
/// because the number was already taken there.
/// </summary>
public sealed record CopyParticipantsResult(
    int Copied,
    int AlreadyPresent,
    int GroupsCreated,
    int ChipsSkipped);
