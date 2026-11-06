using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>The start draw with «Враховувати попередні дні»: the earlier days' start positions steer the order
/// (no repeated first/last starter, no repeated order in a small group, balanced positions), the plain draw is
/// unchanged, and the prep data carries the history for individuals and relay teams.</summary>
[Collection(AppCollection.Name)]
public sealed class DrawHistoryTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static IStartDrawService Draw => Sp.GetRequiredService<IStartDrawService>();

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>A group whose members ran the given earlier days in the listed orders (each order = names,
    /// first starter first). Every member is keyed by its name.</summary>
    private static DrawGroup GroupWithDays(IReadOnlyList<(string Name, string Club)> members, params string[][] days)
    {
        var history = members.ToDictionary(m => m.Name, _ => new List<DrawHistoryEntry>(), StringComparer.Ordinal);
        for (var d = 0; d < days.Length; d++)
        {
            var order = days[d];
            for (var i = 0; i < order.Length; i++)
                history[order[i]].Add(new DrawHistoryEntry(d + 1, i, order.Length, i > 0 ? order[i - 1] : null));
        }

        var list = members
            .Select(m => new DrawParticipant(Guid.NewGuid(), Guid.NewGuid(), m.Name, "", "", m.Club, "", m.Name, history[m.Name]))
            .ToList();
        return new DrawGroup(Guid.NewGuid(), "Ч21", "31", ["31"], list);
    }

    private static List<string> Order(DrawGroup group, IReadOnlyList<DrawStartAssignment> assignments)
    {
        var byLink = group.Members.ToDictionary(m => m.LinkId, m => m.FullName);
        return assignments.OrderBy(a => a.StartTime).Select(a => byLink[a.LinkId]).ToList();
    }

    private static List<string> DrawOrder(DrawGroup group, int seed, bool considerPreviousDays,
        DrawSeparationField separation = DrawSeparationField.None)
        => Order(group, Draw.Draw([[group]], TimeSpan.Zero, Minute, separation, seed: seed, considerPreviousDays: considerPreviousDays));

    [Fact]
    public void ThreeRunners_NeverRepeatYesterdaysFirstLastOrOrder()
    {
        var group = GroupWithDays([("A", ""), ("B", ""), ("C", "")], ["A", "B", "C"]);
        for (var seed = 1; seed <= 100; seed++)
        {
            var order = DrawOrder(group, seed, considerPreviousDays: true);
            Assert.NotEqual("A", order[0]);
            Assert.NotEqual("C", order[^1]);
            Assert.NotEqual(["A", "B", "C"], order);
        }
    }

    [Fact]
    public void TwoRunners_SecondDayIsAlwaysReversed()
    {
        var group = GroupWithDays([("A", ""), ("B", "")], ["A", "B"]);
        for (var seed = 1; seed <= 50; seed++)
            Assert.Equal(["B", "A"], DrawOrder(group, seed, considerPreviousDays: true));
    }

    [Fact]
    public void SameOrderTwice_ThirdDayIsTheFullReversal()
    {
        // A opened and C closed the group two days running: the only order with no repeated first/last starter
        // and no repeated neighbour pair is C, B, A.
        var group = GroupWithDays([("A", ""), ("B", ""), ("C", "")], ["A", "B", "C"], ["A", "B", "C"]);
        for (var seed = 1; seed <= 50; seed++)
            Assert.Equal(["C", "B", "A"], DrawOrder(group, seed, considerPreviousDays: true));
    }

    [Fact]
    public void FourRunners_EdgesNeverRepeatAndSeparationStillHolds()
    {
        var group = GroupWithDays(
            [("A", "X"), ("B", "X"), ("C", "Y"), ("D", "Y")],
            ["A", "C", "B", "D"], ["A", "D", "B", "C"]);
        for (var seed = 1; seed <= 100; seed++)
        {
            var order = DrawOrder(group, seed, considerPreviousDays: true, DrawSeparationField.Club);
            Assert.NotEqual("A", order[0]);
            Assert.True(order[^1] is not ("D" or "C"), $"seed {seed}: {string.Join(",", order)}");

            // Club separation (X X Y Y → always alternating) is applied inside every candidate.
            var clubs = order.Select(n => group.Members.Single(m => m.FullName == n).ClubName).ToList();
            for (var i = 1; i < clubs.Count; i++)
                Assert.NotEqual(clubs[i - 1], clubs[i]);
        }
    }

    [Fact]
    public void NewRunnersWithoutHistory_AreNeutralButStillMoveTheVeteranOffTheEdge()
    {
        // Only A has a history (first yesterday); B and C are new on this day.
        var group = GroupWithDays([("A", ""), ("B", ""), ("C", "")]);
        group = group with
        {
            Members = group.Members
                .Select(m => m.FullName == "A" ? m with { History = [new DrawHistoryEntry(1, 0, 4, null)] } : m)
                .ToList(),
        };
        var firsts = new HashSet<string>();
        for (var seed = 1; seed <= 100; seed++)
        {
            var order = DrawOrder(group, seed, considerPreviousDays: true);
            Assert.NotEqual("A", order[0]);
            firsts.Add(order[0]);
        }
        Assert.Equal(["B", "C"], firsts.Order().ToList()); // the newcomers still share the first slot at random
    }

    [Fact]
    public void WithoutHistoryOrWithTheFlagOff_TheDrawIsTheSameAsBefore()
    {
        var withHistory = GroupWithDays([("A", ""), ("B", ""), ("C", ""), ("D", "")], ["A", "B", "C", "D"]);
        var noHistory = withHistory with { Members = withHistory.Members.Select(m => m with { History = [] }).ToList() };

        for (var seed = 1; seed <= 20; seed++)
        {
            var plain = DrawOrder(noHistory, seed, considerPreviousDays: false);
            // The flag has nothing to work on without a history: identical result.
            Assert.Equal(plain, DrawOrder(noHistory, seed, considerPreviousDays: true));
            // History is ignored when the flag is off.
            Assert.Equal(plain, DrawOrder(withHistory, seed, considerPreviousDays: false));
        }

        // The classic draw honours the flag the same way.
        var classic = Draw.DrawClassic([new ClassicDrawGroup(withHistory, TimeSpan.Zero, Minute)],
            DrawSeparationField.None, seed: 3, considerPreviousDays: true);
        Assert.NotEqual("A", Order(withHistory, classic)[0]);
    }

    [Fact]
    public async Task PrepData_CarriesEveryRunnersPositionsFromTheEarlierDays()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("drawhist"), dayCount: 2);
        await c.AddControlsAsync(1, "31", "32");
        await c.AddControlsAsync(2, "31", "32");
        await c.AddGroupAsync("Ч21", "31 32");
        await c.AddRunnerAsync("A", "1", "", "Ч21");
        await c.AddRunnerAsync("B", "2", "", "Ч21");
        await c.AddRunnerAsync("C", "3", "", "Ч21");
        await c.AddRunnerAsync("D", "4", "", "Ч21", days: [2]); // new on day 2
        await c.UpdateLinkAsync("A", 1, l => l.StartTime = new TimeSpan(11, 0, 0));
        await c.UpdateLinkAsync("B", 1, l => l.StartTime = new TimeSpan(11, 1, 0));
        await c.UpdateLinkAsync("C", 1, l => l.StartTime = new TimeSpan(11, 2, 0));
        await c.SelectAsync(2);
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();

        // Day 1 has nothing before it.
        var day1 = Assert.Single((await editor.GetDrawPrepDataAsync(c.Day(1).Id)).Groups);
        Assert.All(day1.Members, m => Assert.Empty(m.History));

        var day2 = Assert.Single((await editor.GetDrawPrepDataAsync(c.Day(2).Id)).Groups);
        var byName = day2.Members.ToDictionary(m => m.FullName);
        Assert.Equal(4, byName.Count);
        var keyA = c.Runners["A"].Id.ToString("N");
        Assert.Equal(keyA, byName["A"].HistoryKey);
        Assert.Equal([new DrawHistoryEntry(1, 0, 3, null)], byName["A"].History);
        Assert.Equal([new DrawHistoryEntry(1, 1, 3, keyA)], byName["B"].History);
        Assert.Equal([new DrawHistoryEntry(1, 2, 3, c.Runners["B"].Id.ToString("N"))], byName["C"].History);
        Assert.Empty(byName["D"].History);

        // The draw of day 2 then never opens with A again.
        for (var seed = 1; seed <= 30; seed++)
            Assert.NotEqual("A", DrawOrder(day2, seed, considerPreviousDays: true)[0]);
    }

    [Fact]
    public async Task PrepData_RelayTeamsAreKeyedByGroupAndNumber()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("drawhistrelay"), dayCount: 2, discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31", "32");
        await c.AddControlsAsync(2, "31", "32");
        var group = await c.AddGroupAsync("Е", "31 32");
        await c.AddRunnerAsync("R1", "", "", "Е");
        await c.AddRunnerAsync("R2", "", "", "Е");
        await c.AddRelayTeamAsync("Е", 1, "Перша", 1, ("R1", 1));
        await c.AddRelayTeamAsync("Е", 2, "Друга", 1, ("R2", 1));
        await c.UpdateLinkAsync("R1", 1, l => l.StartTime = new TimeSpan(11, 0, 0));
        await c.UpdateLinkAsync("R2", 1, l => l.StartTime = new TimeSpan(11, 5, 0));
        // Day 2: the same team numbers, the runners swapped — the team, not the runner, is the unit.
        await c.AddRelayTeamAsync("Е", 1, "Перша", 2, ("R2", 1));
        await c.AddRelayTeamAsync("Е", 2, "Друга", 2, ("R1", 1));
        await c.SelectAsync(2);

        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var day2 = Assert.Single((await editor.GetDrawPrepDataAsync(c.Day(2).Id)).Groups);
        var byNumber = day2.Members.ToDictionary(m => m.Number);
        var key1 = $"team:{group.Id:N}:1";
        Assert.Equal(key1, byNumber["1"].HistoryKey);
        Assert.Equal([new DrawHistoryEntry(1, 0, 2, null)], byNumber["1"].History);
        Assert.Equal([new DrawHistoryEntry(1, 1, 2, key1)], byNumber["2"].History);
    }
}
