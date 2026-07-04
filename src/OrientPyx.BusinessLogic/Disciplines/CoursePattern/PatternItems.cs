namespace OrientPyx.BusinessLogic.Disciplines.CoursePattern;

/// <summary>
/// Mutable walk state shared by the pattern items while matching one runner's punches. <see cref="Pos"/>
/// is the index of the next unconsumed punch; matching advances it (skipping foreign/extra punches) and
/// marks each consumed punch in <see cref="OnCourse"/>. <see cref="FirstMissing"/> keeps the first
/// prescribed control that could not be found, for the MP detail.
/// </summary>
internal sealed class MatchState
{
    public MatchState(IReadOnlyList<string> punches, bool[] onCourse, IReadOnlySet<string>? ignoredCodes = null)
    {
        Punches = punches;
        OnCourse = onCourse;
        _ignored = ignoredCodes;
    }

    private readonly IReadOnlySet<string>? _ignored;

    public IReadOnlyList<string> Punches { get; }
    public bool[] OnCourse { get; }
    public int Pos { get; set; }
    public string? FirstMissing { get; private set; }

    /// <summary>True when this control is disabled («проблемний КП») for the day and so may be skipped.</summary>
    public bool IsIgnored(string code) => _ignored is not null && _ignored.Contains(code.Trim());

    /// <summary>Records the first control the pattern failed to satisfy (only the first is kept).</summary>
    public void Fail(string code) => FirstMissing ??= code;

    public bool Matches(int index, string code) =>
        string.Equals(Punches[index].Trim(), code.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>One node of a parsed course pattern: a control, an ordered block, or an "any N of" block.</summary>
internal interface IPatternItem
{
    /// <summary>Minimum punches this item requires from a valid run (a control = 1; an ordered block = the
    /// sum of its children; an <c>[N …]</c> block = N).</summary>
    int RequiredCount { get; }

    /// <summary>Appends every control code this item references (leaves), in reading order.</summary>
    void CollectCodes(List<string> codes);

    /// <summary>The first control code this item would look for (used as the MP detail when it can't match).</summary>
    string ErrorIfEmpty { get; }

    /// <summary>
    /// Greedily matches this item from <see cref="MatchState.Pos"/> onward, advancing the pointer past the
    /// punches it consumed (marking them on-course) and skipping any foreign/out-of-order punches in
    /// between. Returns false (recording the first missing control) when it can't be satisfied.
    /// </summary>
    bool Match(MatchState state);

    /// <summary>Renders this item back to pattern text (for the normalized-order editor preview).</summary>
    string ToPatternString();
}

/// <summary>A single control point: matched by scanning forward for its code (skipping extras).</summary>
internal sealed class ControlItem : IPatternItem
{
    public ControlItem(string code) => Code = code.Trim();

    public string Code { get; }

    public int RequiredCount => 1;
    public string ErrorIfEmpty => Code;

    public void CollectCodes(List<string> codes) => codes.Add(Code);

    public string ToPatternString() => Code;

    public bool Match(MatchState state)
    {
        for (var i = state.Pos; i < state.Punches.Count; i++)
        {
            if (state.Matches(i, Code))
            {
                state.OnCourse[i] = true;
                state.Pos = i + 1;
                return true;
            }
        }

        // A disabled («проблемний») control is not required — treat it as satisfied without consuming a
        // punch, so a runner who couldn't punch a broken box is not penalised (matches the set-course rule).
        if (state.IsIgnored(Code))
            return true;

        state.Fail(Code);
        return false;
    }
}

/// <summary>An ordered run of items: each child must match in turn (children may skip extras between them).</summary>
internal sealed class SequenceItem : IPatternItem
{
    private readonly IReadOnlyList<IPatternItem> _items;

    public SequenceItem(IReadOnlyList<IPatternItem> items) => _items = items;

    public int RequiredCount => _items.Sum(x => x.RequiredCount);
    public string ErrorIfEmpty => _items.Count > 0 ? _items[0].ErrorIfEmpty : string.Empty;

    public void CollectCodes(List<string> codes)
    {
        foreach (var item in _items)
            item.CollectCodes(codes);
    }

    public string ToPatternString() =>
        _items.Count == 0 ? "<>" : $"<{InnerString()}>";

    /// <summary>The children joined by spaces, without the surrounding angle brackets — used to render the
    /// top-level (implicit) sequence, which isn't itself an explicit &lt;…&gt; block.</summary>
    public string InnerString() => string.Join(' ', _items.Select(x => x.ToPatternString()));

    public bool Match(MatchState state)
    {
        foreach (var item in _items)
        {
            if (!item.Match(state))
                return false;
        }
        return true;
    }
}

/// <summary>
/// An "any <c>Amount</c> of these" block: consumes punches that satisfy any not-yet-used option, in any
/// order, until <see cref="Amount"/> options are matched. Mirrors CourseChecker's greedy AnyOfBlock —
/// each punch is offered to the remaining options and the first that accepts it wins.
/// </summary>
internal sealed class AnyOfItem : IPatternItem
{
    private readonly IReadOnlyList<IPatternItem> _items;

    public AnyOfItem(int amount, IReadOnlyList<IPatternItem> items)
    {
        Amount = amount;
        _items = items;
    }

    public int Amount { get; }

    public int RequiredCount => Amount;
    public string ErrorIfEmpty => _items.Count > 0 ? _items[0].ErrorIfEmpty : string.Empty;

    public void CollectCodes(List<string> codes)
    {
        foreach (var item in _items)
            item.CollectCodes(codes);
    }

    public string ToPatternString() =>
        $"[{Amount}: {string.Join(' ', _items.Select(x => x.ToPatternString()))}]";

    public bool Match(MatchState state)
    {
        if (Amount <= 0)
            return true;

        var unused = _items.ToList();
        var matched = 0;

        while (state.Pos < state.Punches.Count && matched < Amount)
        {
            // Offer the current punch to each remaining option; a single-control option matches directly
            // (so we don't let ControlItem.Match scan far ahead and swallow later punches), a nested block
            // is asked to match from here. The first option that consumes something wins.
            var before = state.Pos;
            IPatternItem? took = null;
            foreach (var option in unused)
            {
                if (option is ControlItem cp)
                {
                    if (state.Matches(before, cp.Code))
                    {
                        state.OnCourse[before] = true;
                        state.Pos = before + 1;
                        took = option;
                        break;
                    }
                }
                else if (option.Match(state))
                {
                    took = option;
                    break;
                }
                else
                {
                    // A nested block that couldn't match here leaves Pos untouched; keep scanning.
                    state.Pos = before;
                }
            }

            if (took is not null)
            {
                unused.Remove(took);
                matched++;
            }
            else
            {
                // This punch satisfies no remaining option — a foreign/extra punch; skip it and go on.
                state.Pos = before + 1;
            }
        }

        // Make up any shortfall with disabled («проблемні») options: an ignored control counts as satisfied
        // without a punch (a broken box can't be reached), so the block still passes if the runner took the
        // other required controls. Nested blocks aren't auto-satisfied — only leaf controls can be disabled.
        for (var k = unused.Count - 1; k >= 0 && matched < Amount; k--)
        {
            if (unused[k] is ControlItem { } cp && state.IsIgnored(cp.Code))
            {
                unused.RemoveAt(k);
                matched++;
            }
        }

        if (matched >= Amount)
            return true;

        state.Fail(unused.Count > 0 ? unused[0].ErrorIfEmpty : ErrorIfEmpty);
        return false;
    }
}
