using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Results;

/// <summary>Builds the read-only <see cref="ParticipantView"/> discipline rules see, from a day snapshot.</summary>
internal sealed class ParticipantViews
{
    private readonly Dictionary<Guid, ParticipantView> _byParticipant = [];
    private readonly IReadOnlyDictionary<Guid, Participant> _participants;
    private readonly Dictionary<Guid, string> _regions;
    private readonly Dictionary<Guid, string> _clubs;
    private readonly Dictionary<Guid, string> _dusshes;
    private readonly Dictionary<Guid, Dictionary<string, string?>> _fields;

    public ParticipantViews(EventDaySnapshot snapshot)
        : this(snapshot.Participants, snapshot.Regions, snapshot.Clubs, snapshot.Dusshes, snapshot.FieldValues)
    {
    }

    public ParticipantViews(
        IEnumerable<Participant> participants,
        IEnumerable<Region> regions,
        IEnumerable<Club> clubs,
        IEnumerable<Dussh> dusshes,
        IEnumerable<ParticipantFieldValue> fieldValues)
    {
        _participants = participants.ToDictionary(p => p.Id);
        _regions = regions.ToDictionary(r => r.Id, r => r.Name);
        _clubs = clubs.ToDictionary(c => c.Id, c => c.Name);
        _dusshes = dusshes.ToDictionary(d => d.Id, d => d.Name);
        _fields = fieldValues
            .GroupBy(v => v.ParticipantId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.Key, v => (string?)v.Value));
    }

    public ParticipantView For(Guid participantId)
    {
        if (_byParticipant.TryGetValue(participantId, out var view))
            return view;

        view = _participants.TryGetValue(participantId, out var p)
            ? new ParticipantView
            {
                FullName = p.FullName,
                Number = p.Number,
                BirthYear = p.BirthDate?.Year,
                Club = p.ClubId is { } cid && _clubs.TryGetValue(cid, out var club) ? club : string.Empty,
                Region = p.RegionId is { } rid && _regions.TryGetValue(rid, out var region) ? region : string.Empty,
                Team = p.Team,
                Dussh = p.DusshId is { } did && _dusshes.TryGetValue(did, out var dussh) ? dussh : string.Empty,
                Qualification = p.Rank ?? string.Empty,
                Fields = _fields.TryGetValue(participantId, out var fields) ? fields : new Dictionary<string, string?>()
            }
            : new ParticipantView();
        _byParticipant[participantId] = view;
        return view;
    }
}
