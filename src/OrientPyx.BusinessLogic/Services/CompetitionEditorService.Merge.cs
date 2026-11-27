using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

public sealed partial class CompetitionEditorService
{
    /// <inheritdoc />
    public async Task<ParticipantMergeResult> MergeParticipantsAsync(Guid keepId, Guid mergeId, CancellationToken cancellationToken = default)
    {
        if (keepId == mergeId)
            throw new ArgumentException("A participant cannot be merged into itself.", nameof(mergeId));
        var folder = FolderPath;
        var people = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var keep = people.FirstOrDefault(p => p.Id == keepId) ?? throw new InvalidOperationException("The participant to keep is gone.");
        var merge = people.FirstOrDefault(p => p.Id == mergeId) ?? throw new InvalidOperationException("The participant to merge is gone.");

        var links = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);
        var keepDays = links.Where(l => l.ParticipantId == keepId).Select(l => l.EventDayId).ToHashSet();
        var mergeLinks = links.Where(l => l.ParticipantId == mergeId).ToList();
        // Every day of the merged person changes (moved or dropped): none of them may be closed.
        foreach (var link in mergeLinks)
            await EnsureDayEditableAsync(link.EventDayId, cancellationToken);
        var moved = mergeLinks.Where(l => !keepDays.Contains(l.EventDayId)).ToList();
        var dropped = mergeLinks.Where(l => keepDays.Contains(l.EventDayId)).ToList();

        // What only the merged one has: additional-field values and discounts the kept one lacks.
        var values = await _eventStore.GetParticipantFieldValuesAsync(folder, cancellationToken);
        var keepKeys = values.Where(v => v.ParticipantId == keepId && v.Value.Trim().Length > 0).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
        var addValues = values.Where(v => v.ParticipantId == mergeId && v.Value.Trim().Length > 0 && !keepKeys.Contains(v.Key)).ToList();
        var discounts = await _eventStore.GetParticipantDiscountsAsync(folder, cancellationToken);
        var keepDiscounts = discounts.Where(d => d.ParticipantId == keepId).Select(d => d.DiscountId).ToHashSet();
        var addDiscounts = discounts.Where(d => d.ParticipantId == mergeId && !keepDiscounts.Contains(d.DiscountId)).Select(d => d.DiscountId).ToList();

        // The kept one's blanks are filled from the merged one; nothing the kept one has is overwritten.
        var filled = new List<string>();
        string Fill(string mine, string theirs, string field)
        {
            if (mine.Trim().Length > 0 || theirs.Trim().Length == 0)
                return mine;
            filled.Add(field);
            return theirs;
        }
        T? FillId<T>(T? mine, T? theirs, string field) where T : struct
        {
            if (mine is not null || theirs is null)
                return mine;
            filled.Add(field);
            return theirs;
        }
        var updated = new Participant
        {
            Id = keep.Id,
            FullName = keep.FullName,
            Number = Fill(keep.Number, merge.Number, "bib"),
            Rank = Fill(keep.Rank, merge.Rank, "rank"),
            Coach = Fill(keep.Coach, merge.Coach, "coach"),
            BirthDate = FillId(keep.BirthDate, merge.BirthDate, "birth_date"),
            RegionId = FillId(keep.RegionId, merge.RegionId, "region"),
            ClubId = FillId(keep.ClubId, merge.ClubId, "club"),
            DusshId = FillId(keep.DusshId, merge.DusshId, "dussh"),
            Representative = Fill(keep.Representative, merge.Representative, "representative"),
            FsouCode = Fill(keep.FsouCode, merge.FsouCode, "fsou_code"),
            IsFsouMember = keep.IsFsouMember || merge.IsFsouMember,
            Payment = Fill(keep.Payment, merge.Payment, "payment"),
            Note = Fill(keep.Note, merge.Note, "note"),
            Team = Fill(keep.Team, merge.Team, "team"),
        };
        if (merge.IsFsouMember && !keep.IsFsouMember)
            filled.Add("fsou_member");

        // 1. The merged person goes, with their days, discounts and field values (their bib and chips are free then) …
        await _eventStore.DeleteParticipantAsync(folder, mergeId, cancellationToken);
        // 2. … the days only they ran come back on the kept one with everything on them: group, chip, start, П/К, status,
        // «Бонус», payment, relay team and leg. A day both ran keeps the kept one's entry.
        foreach (var link in moved)
            await _eventStore.AddParticipantDayAsync(folder, new ParticipantDay
            {
                EventDayId = link.EventDayId,
                ParticipantId = keepId,
                Order = link.Order,
                GroupId = link.GroupId,
                Chip = link.Chip,
                Payment = link.Payment,
                PaysRaisedFee = link.PaysRaisedFee,
                StartTime = link.StartTime,
                OutOfCompetition = link.OutOfCompetition,
                Bonus = link.Bonus,
                ResultStatusOverride = link.ResultStatusOverride,
                RelayTeamId = link.RelayTeamId,
                Leg = link.Leg,
                CreatedAt = link.CreatedAt,
            }, cancellationToken);
        // 3. The identity, the field values, the discounts, «Мандатку пройдено».
        await _eventStore.UpdateParticipantAsync(folder, updated, cancellationToken);
        foreach (var value in addValues)
            await _eventStore.SetParticipantFieldValueAsync(folder, keepId, value.Key, value.Value, cancellationToken);
        foreach (var discountId in addDiscounts)
            await _eventStore.SetParticipantDiscountAsync(folder, keepId, discountId, true, cancellationToken);
        if (merge.CheckedIn && !keep.CheckedIn)
            await _eventStore.SetParticipantsCheckedInAsync(folder, [keepId], true, cancellationToken);

        var dayNumbers = (await _eventStore.GetDaysAsync(folder, cancellationToken)).ToDictionary(d => d.Id, d => d.Number);
        int Number(ParticipantDay l) => dayNumbers.GetValueOrDefault(l.EventDayId);
        return new ParticipantMergeResult(
            moved.Select(Number).OrderBy(n => n).ToList(),
            dropped.Select(Number).OrderBy(n => n).ToList(),
            filled.Concat(addValues.Select(v => "field." + v.Key)).ToList(),
            addDiscounts.Count);
    }
}
