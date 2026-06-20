using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Performs the start draw (жеребкування) for a competition day: turns the prepared start groups into a
/// concrete start time per competitor. Pure domain logic — no database, files or UI — so the same draw can
/// be previewed in the page and then saved. Implements the standard orienteering draw: a random order
/// inside each group, with the option to keep competitors who share a chosen attribute (region / club /
/// team) off consecutive start slots.
/// </summary>
public interface IStartDrawService
{
    /// <summary>
    /// Draws start times for every member of every supplied start group. Each start group is an ordered
    /// sequence of groups whose members start one after another from <paramref name="globalStart"/> with
    /// <paramref name="interval"/> between consecutive slots; start groups are independent of one another
    /// (they all begin at <paramref name="globalStart"/> on their own start lane).
    ///
    /// Within a single group the members are shuffled randomly. When <paramref name="separation"/> is not
    /// <see cref="DrawSeparationField.None"/>, the order is then adjusted so that no two consecutive
    /// competitors share that attribute where it can be avoided (the next differing competitor is pulled
    /// forward). A blank attribute value never collides with anything.
    ///
    /// Returns one assignment per member across all groups. The order of groups inside a start group is
    /// taken as given (the page lets the user arrange it); only the order of members inside each group is
    /// drawn here.
    /// </summary>
    /// <param name="startGroups">
    /// The start groups, each a list of <see cref="DrawGroup"/> in the order they run on that start lane.
    /// </param>
    /// <param name="globalStart">Time of day the first competitor in each start group starts.</param>
    /// <param name="interval">Gap between consecutive start slots.</param>
    /// <param name="separation">Which attribute to keep off consecutive slots (or none).</param>
    /// <param name="seed">Optional fixed seed for a reproducible draw; null uses a random seed.</param>
    IReadOnlyList<DrawStartAssignment> Draw(
        IReadOnlyList<IReadOnlyList<DrawGroup>> startGroups,
        TimeSpan globalStart,
        TimeSpan interval,
        DrawSeparationField separation,
        int? seed = null);
}
