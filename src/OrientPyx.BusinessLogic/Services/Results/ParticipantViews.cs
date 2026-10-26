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

    public ParticipantViews(EventDaySnapshot snapshot)
    {
        _participants = snapshot.Participants.ToDictionary(p => p.Id);
        _regions = snapshot.Regions.ToDictionary(r => r.Id, r => r.Name);
        _clubs = snapshot.Clubs.ToDictionary(c => c.Id, c => c.Name);
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
                Team = p.Team
            }
            : new ParticipantView();
        _byParticipant[participantId] = view;
        return view;
    }
}
