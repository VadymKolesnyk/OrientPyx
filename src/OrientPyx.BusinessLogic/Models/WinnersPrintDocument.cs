namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A ready-to-render "winners" (призери) printout: a small header identifying the competition/day, then one
/// section per group listing that group's prize places (1st … Nth) with the runner(s) at each place. Built in
/// the layer-neutral BusinessLogic layer (no printer/UI refs) from the same computed results the protocols use;
/// the DataAccess thermal printer renders it to a narrow roll, reusing the read-out print settings.
///
/// Shared (tied) places are kept explicit: when two runners share third place, the section carries both under a
/// single place entry, so the slip can print "2 третіх" and list both names — the caller does NOT collapse
/// ties into one line.
/// </summary>
public sealed class WinnersPrintDocument
{
    /// <summary>Competition name printed at the top; blank when unknown.</summary>
    public string CompetitionName { get; init; } = string.Empty;

    /// <summary>Secondary caption line (the protocol title, e.g. «Протокол результатів» / «Підсумковий залік»);
    /// blank when none.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The day/date line (e.g. "День 1 — 30.05.2026" or the summary date span); blank when none.</summary>
    public string DateText { get; init; } = string.Empty;

    public DateTimeOffset PrintedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>The group sections, in display order. A group with no prize winners is omitted by the builder.</summary>
    public IReadOnlyList<WinnersGroupSection> Groups { get; init; } = [];

    /// <summary>True when there is nothing to print (no group has any prize winner).</summary>
    public bool IsEmpty => Groups.Count == 0 || Groups.All(g => g.Places.Count == 0);
}

/// <summary>One group's winners: the group name and its ordered prize places (place 1 first).</summary>
public sealed record WinnersGroupSection(string GroupName, IReadOnlyList<WinnersPlace> Places);

/// <summary>
/// One prize place within a group: the place number, its already-localized heading, and every runner who holds
/// it. A single runner is the normal case; more than one means the place is shared (a tie) — e.g. two people
/// sharing 3rd place, whose <see cref="Heading"/> reads "2 третіх" and lists both names.
/// </summary>
public sealed record WinnersPlace(int Place, string Heading, IReadOnlyList<WinnerEntry> Winners)
{
    /// <summary>True when this place is shared by more than one runner (a tie).</summary>
    public bool IsShared => Winners.Count > 1;
}

/// <summary>One prize-winning runner: their full name and formatted result (time, бали, or sum).</summary>
public sealed record WinnerEntry(string FullName, string ResultText);
