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

    /// <summary>
    /// The concrete control order the walk settled on, in prescribed order — the pattern's free-choice
    /// blocks resolved to the options this runner actually took (and, past the point the run broke down,
    /// to the block's leading options). Built while matching so the splits panel can show one linear
    /// «правильний порядок» instead of every alternative the pattern allows.
    /// </summary>
    public List<ResolvedControl> Resolved { get; } = [];

    /// <summary>Records one control of the resolved order. <paramref name="taken"/> is true when a punch
    /// satisfied it (or it is a disabled control that counts as satisfied).</summary>
    public void Resolve(string code, bool taken) => Resolved.Add(new ResolvedControl(code.Trim(), taken));

    /// <summary>Drops everything resolved after <paramref name="count"/> — undoes what a speculative walk
    /// (an "any N of" option that was tried and rejected) recorded, so only the chosen branch is kept.</summary>
    public void RewindResolved(int count) => Resolved.RemoveRange(count, Resolved.Count - count);

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

    /// <summary>
    /// How well this item fits the punches starting at <paramref name="from"/>, without touching the walk
    /// state: the number of this item's required controls that the runner actually punched. Used by an
    /// "any N of" block to pick the option closest to what the runner ran, instead of the first that happens
    /// to accept the current punch.
    /// </summary>
    int ScoreAgainst(MatchState state, int from);

    /// <summary>
    /// True when this item's <i>first</i> required control is exactly the punch at <paramref name="index"/> —
    /// i.e. taking this option would start consuming right here, without scanning forward. An "any N of"
    /// block offers each punch to such options first: a long option may reference more of the runner's
    /// punches overall (a higher <see cref="ScoreAgainst"/>) yet begin much later, and letting it win would
    /// swallow the punches a shorter, exactly-fitting option needed.
    /// </summary>
    bool StartsAt(MatchState state, int index);

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

    /// <summary>
    /// Appends this item's controls to the resolved order without matching anything — every one flagged not
    /// taken. Used past the point a run broke down, so the panel still shows one complete concrete order
    /// (a free-choice block contributing its leading options) rather than stopping at the failure.
    /// </summary>
    void ResolveUnmatched(MatchState state);

    /// <summary>Renders this item back to pattern text (for the normalized-order editor preview).</summary>
    string ToPatternString();

    /// <summary>
    /// How many concrete passage orders this item allows: 1 for a control, the product of its children for
    /// an ordered run, and for an <c>[N …]</c> block every way of picking N options × every order they can be
    /// taken in (each option contributing its own count). Computed without materialising the orders, so a
    /// wide block can be reported before it is expanded. Saturates at <see cref="long.MaxValue"/>.
    /// </summary>
    long CountOrders();

    /// <summary>
    /// Every concrete passage order this item allows, each a flat list of control codes. Stops once
    /// <paramref name="budget"/> orders have been produced across the whole expansion (the caller's cap),
    /// so a wide free-choice block can't run away.
    /// </summary>
    IEnumerable<List<string>> Expand(ExpandBudget budget);
}

/// <summary>A shared cap for a pattern expansion: every produced order draws from it, and once it is spent
/// the enumeration stops wherever it is. Kept mutable and shared so nested blocks all honour one limit.</summary>
internal sealed class ExpandBudget
{
    public ExpandBudget(int limit) => Remaining = limit;

    /// <summary>How many more complete orders may still be produced.</summary>
    public int Remaining { get; private set; }

    /// <summary>True once the cap is spent — every level stops enumerating.</summary>
    public bool Exhausted => Remaining <= 0;

    /// <summary>Books one produced order against the cap.</summary>
    public void Take() => Remaining--;
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

    public long CountOrders() => 1;

    public IEnumerable<List<string>> Expand(ExpandBudget budget)
    {
        yield return [Code];
    }

    public bool Match(MatchState state)
    {
        for (var i = state.Pos; i < state.Punches.Count; i++)
        {
            if (state.Matches(i, Code))
            {
                state.OnCourse[i] = true;
                state.Pos = i + 1;
                state.Resolve(Code, taken: true);
                return true;
            }
        }

        // A disabled («проблемний») control is not required — treat it as satisfied without consuming a
        // punch, so a runner who couldn't punch a broken box is not penalised (matches the set-course rule).
        if (state.IsIgnored(Code))
        {
            state.Resolve(Code, taken: true);
            return true;
        }

        state.Fail(Code);
        state.Resolve(Code, taken: false);
        return false;
    }

    public void ResolveUnmatched(MatchState state) => state.Resolve(Code, taken: false);

    /// <summary>True when this very control is the punch at <paramref name="index"/>.</summary>
    public bool StartsAt(MatchState state, int index) =>
        index < state.Punches.Count && state.Matches(index, Code);

    /// <summary>1 when this control appears anywhere from <paramref name="from"/> on (or it is disabled, so
    /// it counts as satisfied); 0 when the runner never punched it.</summary>
    public int ScoreAgainst(MatchState state, int from)
    {
        if (state.IsIgnored(Code))
            return 1;
        for (var i = from; i < state.Punches.Count; i++)
            if (state.Matches(i, Code))
                return 1;
        return 0;
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

    /// <summary>The product of the children's counts — every combination of their own orders.</summary>
    public long CountOrders()
    {
        var total = 1L;
        foreach (var item in _items)
            total = PatternMath.MultiplySaturating(total, item.CountOrders());
        return total;
    }

    /// <summary>Every combination of the children's orders, concatenated in the sequence's order.</summary>
    public IEnumerable<List<string>> Expand(ExpandBudget budget) => ExpandFrom(0, budget);

    private IEnumerable<List<string>> ExpandFrom(int index, ExpandBudget budget)
    {
        if (index >= _items.Count)
        {
            yield return [];
            yield break;
        }

        foreach (var head in _items[index].Expand(budget))
        {
            foreach (var tail in ExpandFrom(index + 1, budget))
            {
                if (budget.Exhausted)
                    yield break;
                yield return [.. head, .. tail];
            }
        }
    }

    public bool Match(MatchState state)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Match(state))
                continue;

            // The run broke here: keep listing the rest of the prescribed order (all untaken) so the resolved
            // order stays complete — it is the course the runner was on, not just the part they finished.
            for (var k = i + 1; k < _items.Count; k++)
                _items[k].ResolveUnmatched(state);
            return false;
        }
        return true;
    }

    public void ResolveUnmatched(MatchState state)
    {
        foreach (var item in _items)
            item.ResolveUnmatched(state);
    }

    /// <summary>An ordered run starts here when its first child does — that child is what it would consume
    /// first (an empty run starts nowhere).</summary>
    public bool StartsAt(MatchState state, int index) =>
        _items.Count > 0 && _items[0].StartsAt(state, index);

    /// <summary>The sum of the children's fits — how many of this run's controls the runner punched.</summary>
    public int ScoreAgainst(MatchState state, int from) => _items.Sum(x => x.ScoreAgainst(state, from));
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

    /// <summary>Every way of picking <see cref="Amount"/> of the options, times every order those picked
    /// options can be taken in, times each picked option's own internal orders.</summary>
    public long CountOrders()
    {
        if (Amount <= 0 || _items.Count == 0)
            return 1;

        // Sum over every combination of Amount options: (orders inside the picked options) × Amount!
        var total = 0L;
        foreach (var combo in PatternMath.Combinations(_items.Count, Amount))
        {
            var inner = 1L;
            foreach (var i in combo)
                inner = PatternMath.MultiplySaturating(inner, _items[i].CountOrders());
            total = PatternMath.AddSaturating(total, inner);
        }
        return PatternMath.MultiplySaturating(total, PatternMath.Factorial(Amount));
    }

    /// <summary>Every valid passage of this block: each choice of <see cref="Amount"/> options, in every
    /// order, with each option expanded to its own orders.</summary>
    public IEnumerable<List<string>> Expand(ExpandBudget budget)
    {
        if (Amount <= 0 || _items.Count == 0)
        {
            yield return [];
            yield break;
        }

        foreach (var combo in PatternMath.Combinations(_items.Count, Amount))
        {
            if (budget.Exhausted)
                yield break;

            foreach (var permutation in PatternMath.Permutations(combo))
            {
                if (budget.Exhausted)
                    yield break;

                foreach (var order in ExpandPermutation(permutation, 0, budget))
                {
                    if (budget.Exhausted)
                        yield break;
                    yield return order;
                }
            }
        }
    }

    // Every combination of the picked options' own orders, laid out in the permutation's order.
    private IEnumerable<List<string>> ExpandPermutation(
        IReadOnlyList<int> permutation, int index, ExpandBudget budget)
    {
        if (index >= permutation.Count)
        {
            yield return [];
            yield break;
        }

        foreach (var head in _items[permutation[index]].Expand(budget))
        {
            foreach (var tail in ExpandPermutation(permutation, index + 1, budget))
            {
                if (budget.Exhausted)
                    yield break;
                yield return [.. head, .. tail];
            }
        }
    }

    public bool Match(MatchState state)
    {
        if (Amount <= 0)
            return true;

        var unused = _items.ToList();
        var matched = 0;
        var before0 = state.Pos;   // where the block started — the reference point for scoring the options

        while (state.Pos < state.Punches.Count && matched < Amount)
        {
            // Offer the current punch to each remaining option; a single-control option matches directly
            // (so we don't let ControlItem.Match scan far ahead and swallow later punches), a nested block
            // is asked to match from here. The first option that consumes something wins.
            var before = state.Pos;
            var resolvedBefore = state.Resolved.Count;
            IPatternItem? took = null;

            // Offer the current punch to the remaining options, the ones that start right here first and the
            // rest by overall fit, so a punch several options could accept goes to the variant the runner is
            // actually running — not merely the one written first, nor the longest one that happens to
            // reference more of their later punches. A single-control option matches directly (so
            // ControlItem.Match can't scan far ahead and swallow later punches); a nested block is asked to
            // match from here.
            foreach (var option in OptionsForPunch(state, before, unused))
            {
                if (option is ControlItem cp)
                {
                    if (state.Matches(before, cp.Code))
                    {
                        state.OnCourse[before] = true;
                        state.Pos = before + 1;
                        state.Resolve(cp.Code, taken: true);
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
                    // A nested block that couldn't match here leaves Pos untouched; keep scanning. Its
                    // speculative resolution is dropped too — only the option finally taken contributes to
                    // the resolved order.
                    state.Pos = before;
                    state.RewindResolved(resolvedBefore);
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
                state.Resolve(cp.Code, taken: true);
                unused.RemoveAt(k);
                matched++;
            }
        }

        if (matched >= Amount)
            return true;

        // Short of the required count: fill the resolved order with the options that best fit the runner's
        // passage, so the panel shows a complete concrete order — the variant they came closest to — with the
        // controls they never took flagged as missing. Picking by fit (not just the first ones left) keeps the
        // slip from claiming a pile of missing controls when the runner was one КП short of a valid variant.
        foreach (var option in BestOptions(state, before0, unused, Amount - matched))
            option.ResolveUnmatched(state);

        state.Fail(unused.Count > 0 ? unused[0].ErrorIfEmpty : ErrorIfEmpty);
        return false;
    }

    /// <summary>Fills the resolved order with the options that best fit the runner's passage, all flagged not
    /// taken — so even an unreached block shows the variant closest to what they ran.</summary>
    public void ResolveUnmatched(MatchState state)
    {
        foreach (var option in BestOptions(state, state.Pos, _items, Amount))
            option.ResolveUnmatched(state);
    }

    /// <summary>A free-choice block starts here when any of its options does — whichever one the runner
    /// began with.</summary>
    public bool StartsAt(MatchState state, int index) => _items.Any(x => x.StartsAt(state, index));

    /// <summary>This block's fit is its best <see cref="Amount"/> options' fits — the variant the runner
    /// came closest to completing.</summary>
    public int ScoreAgainst(MatchState state, int from) =>
        BestOptions(state, from, _items, Amount).Sum(x => x.ScoreAgainst(state, from));

    /// <summary>The <paramref name="amount"/> options that best fit the punches from <paramref name="from"/>,
    /// most-matching first — how the block picks which variant the runner was on. Ties keep the pattern's
    /// own order, so a runner who took none of them still sees the block as written.</summary>
    private static List<IPatternItem> BestOptions(
        MatchState state, int from, IReadOnlyList<IPatternItem> options, int amount) =>
        options
            .Select((item, order) => (item, order, score: item.ScoreAgainst(state, from)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.order)
            .Take(Math.Max(0, amount))
            .Select(x => x.item)
            .ToList();

    /// <summary>
    /// The remaining options ordered for the punch at <paramref name="at"/>: the ones that would start
    /// consuming right there come first (longest first among them, so a run isn't beaten by a bare control
    /// that merely repeats its opening code), and only then the rest by overall fit. Without the
    /// starts-here rule, a long option that references many later punches outranks a short one that fits
    /// the current punch exactly, scans forward, and swallows what the short option needed.
    /// </summary>
    private static List<IPatternItem> OptionsForPunch(
        MatchState state, int at, IReadOnlyList<IPatternItem> options) =>
        options
            .Select((item, order) => (item, order, starts: item.StartsAt(state, at),
                score: item.ScoreAgainst(state, at)))
            .OrderByDescending(x => x.starts)
            .ThenByDescending(x => x.starts ? x.item.RequiredCount : 0)
            .ThenByDescending(x => x.score)
            .ThenBy(x => x.order)
            .Select(x => x.item)
            .ToList();
}
