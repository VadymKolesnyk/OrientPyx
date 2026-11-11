using System.Collections.Generic;
using System.Linq;

namespace OrientPyx.Presentation.Views.Pages;

/// <summary>
/// The grid slots of the columns one section of a code-built preview (protocol, monitor) shows, when the preview's
/// columns are shared by every section but a group leaves some out (another discipline's plugin columns): a left-out
/// column gives its slot to the shown column before it — leading ones to the first shown — so the group's table still
/// spans the page, as the document does (it sizes such a group's columns on their own). None left out ⇒ one slot per
/// column.
/// </summary>
internal static class PreviewColumnSlots
{
    public static List<(int Column, int Start, int Span)> For(int count, IReadOnlyCollection<int> hidden)
    {
        var shown = Enumerable.Range(0, count).Where(c => !hidden.Contains(c)).ToList();
        var slots = new List<(int, int, int)>(shown.Count);
        for (var i = 0; i < shown.Count; i++)
        {
            var start = i == 0 ? 0 : shown[i];
            var end = i + 1 < shown.Count ? shown[i + 1] : count;
            slots.Add((shown[i], start, end - start));
        }
        return slots;
    }
}
