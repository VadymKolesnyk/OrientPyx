using System.Globalization;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Default <see cref="IWinnersPrintBuilder"/>. Collects the top prize places per group from the same computed
/// results the protocols use, keeping shared (tied) places whole so the renderer can show "2 третіх" and both
/// names. For a single day it reads places straight off <see cref="ParticipantDayResult"/> (team groups rank by
/// team); for the summary it delegates the cross-day ranking to <see cref="ISummaryProtocolBuilder"/> so the
/// winners match the «Підсумковий залік». Layer-neutral (BusinessLogic).
/// </summary>
public sealed class WinnersPrintBuilder : IWinnersPrintBuilder
{
    private readonly ISummaryProtocolBuilder _summary;

    public WinnersPrintBuilder(ISummaryProtocolBuilder summary) => _summary = summary;

    public WinnersPrintDocument BuildForDay(
        ResultProtocolData data, WinnersPrintHeader header, WinnersPrintLabels labels, int topPlaces)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(labels);

        var sections = new List<WinnersGroupSection>();
        foreach (var group in data.Groups.OrderBy(g => g.Order))
        {
            var entries = group.IsTeam
                ? CollectTeamPlaces(group.Rows)
                : CollectPersonalPlaces(group.Rows);

            var places = BuildPlaces(entries, topPlaces, labels);
            if (places.Count > 0)
                sections.Add(new WinnersGroupSection(group.Name, places));
        }

        return new WinnersPrintDocument
        {
            CompetitionName = header.CompetitionName,
            Title = header.Title,
            DateText = header.DateText,
            Groups = sections
        };
    }

    public WinnersPrintDocument BuildForSummary(
        SummaryProtocolData data, SummaryProtocolSettings settings,
        WinnersPrintHeader header, WinnersPrintLabels labels, int topPlaces)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(labels);

        var ranked = _summary.RankGroups(data, settings);

        var sections = new List<WinnersGroupSection>();
        foreach (var group in ranked.OrderBy(g => g.Order))
        {
            // Only the placed entries are prize candidates (поза конкурсом entries have no place).
            var entries = group.Entries
                .Where(e => e.Place is not null)
                .Select(e => new PlacedEntry(e.Place!.Value, e.Member.FullName, e.TotalText))
                .ToList();

            var places = BuildPlaces(entries, topPlaces, labels);
            if (places.Count > 0)
                sections.Add(new WinnersGroupSection(group.GroupName, places));
        }

        return new WinnersPrintDocument
        {
            CompetitionName = header.CompetitionName,
            Title = header.Title,
            DateText = header.DateText,
            Groups = sections
        };
    }

    // Personal group: one placed entry per OK finisher that has a place (out-of-competition runners have none),
    // with the result time as the shown result.
    private static List<PlacedEntry> CollectPersonalPlaces(IReadOnlyList<ResultProtocolRow> rows)
    {
        var entries = new List<PlacedEntry>();
        foreach (var row in rows)
            if (row.Result.Place is { } place)
                entries.Add(new PlacedEntry(place, row.FullName, ResultText(row.Result)));
        return entries;
    }

    // Team (rogaine) group: one placed entry per team (place + score stamped on every member; take from the
    // first member of each team). The team name is the winner shown; teamless runners have no team place.
    private static List<PlacedEntry> CollectTeamPlaces(IReadOnlyList<ResultProtocolRow> rows)
    {
        var entries = new List<PlacedEntry>();
        foreach (var team in rows.Where(r => r.Team.Length > 0)
                     .GroupBy(r => r.Team, StringComparer.CurrentCultureIgnoreCase))
        {
            var lead = team.First();
            if (lead.Result.Place is { } place)
                entries.Add(new PlacedEntry(place, team.Key, ResultText(lead.Result)));
        }
        return entries;
    }

    // Groups the placed entries by place, keeps the places ≤ topPlaces (so a shared last place is kept whole),
    // orders them ascending, and lists each place's winners by name. Empty when no entry is placed.
    private static List<WinnersPlace> BuildPlaces(List<PlacedEntry> entries, int topPlaces, WinnersPrintLabels labels)
    {
        return entries
            .Where(e => e.Place >= 1 && e.Place <= topPlaces)
            .GroupBy(e => e.Place)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var winners = g.OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase)
                    .Select(e => new WinnerEntry(e.FullName, e.ResultText))
                    .ToList();
                var heading = winners.Count > 1
                    ? labels.SharedPlaceHeading(winners.Count, g.Key)
                    : labels.PlaceHeading(g.Key);
                return new WinnersPlace(g.Key, heading, winners);
            })
            .ToList();
    }

    // An OK run shows its result time ("h:mm:ss"); a scored (rogaine) result shows its бали; any other status
    // shows the short code. Mirrors ResultProtocolBuilder.ResultCell so the winners result matches the protocol.
    private static string ResultText(ParticipantDayResult r)
    {
        if (r.Status == FinishStatus.Ok)
        {
            if (r.Score is { } score)
                return score.ToString(CultureInfo.InvariantCulture);
            return r.ResultTime is { } e && e >= TimeSpan.Zero ? e.ToString("h\\:mm\\:ss") : string.Empty;
        }
        return ShortCode(r.Status);
    }

    private static string ShortCode(FinishStatus status) => status switch
    {
        FinishStatus.Ok => "OK",
        FinishStatus.Mp => "MP",
        FinishStatus.Ovt => "OVT",
        FinishStatus.Dnf => "DNF",
        FinishStatus.Dns => "DNS",
        FinishStatus.Dsq => "DSQ",
        _ => string.Empty
    };

    // A placed candidate gathered from either source before grouping into WinnersPlace entries.
    private sealed record PlacedEntry(int Place, string FullName, string ResultText);
}
