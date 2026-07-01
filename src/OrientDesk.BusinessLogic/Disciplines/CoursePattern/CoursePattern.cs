namespace OrientDesk.BusinessLogic.Disciplines.CoursePattern;

/// <summary>
/// A parsed «mixed» course-order pattern (вид «змішаний»). The pattern language — ported from the
/// standalone CourseChecker tool — lets a course mix a prescribed order with free-choice sections:
/// <list type="bullet">
///   <item><c>41 42 43</c> — a bare sequence: КП must be punched in this order (foreign/extra punches
///   between them are ignored, exactly like a set course).</item>
///   <item><c>&lt;41 42 43&gt;</c> — an explicit ordered block: same rule as a bare sequence, used to
///   order a run of controls <i>inside</i> a larger pattern.</item>
///   <item><c>[2: 45 46 47]</c> — an "any N of these" block: any <c>2</c> of the listed items, in any
///   order. The count is written before a colon; the items may themselves be controls or nested blocks.</item>
/// </list>
/// Blocks nest freely, e.g. <c>&lt;30 [2: 41 42 43] 50&gt;</c> = punch 30, then any two of 41/42/43 in
/// any order, then 50. A top-level pattern is an implicit sequence of its items.
///
/// <para>A start marker (<c>S</c>/<c>Start</c>/a token beginning with S) at the very beginning and a finish
/// marker (<c>F</c>/<c>Finish</c>) at the very end are optional: they are shown around the order for clarity
/// but are not required controls — the start/finish is defined by the day's control-point types, and those
/// punches are removed before matching. A stray S/F token elsewhere is treated as an ordinary control.</para>
///
/// <para><see cref="Match"/> greedily walks the punches (start/finish already removed), marking which ones
/// the pattern consumed (on-course) and reporting the first prescribed control it could not satisfy (the MP
/// detail). <see cref="Errors"/> lists any structural problems found while parsing (empty when valid).</para>
/// </summary>
public sealed class CoursePattern
{
    private readonly IPatternItem _root;

    private CoursePattern(IPatternItem root) => _root = root;

    /// <summary>All control codes referenced anywhere in the pattern (leaves), in reading order, with
    /// duplicates kept. Excludes the optional leading/trailing start/finish markers.</summary>
    public IReadOnlyList<string> ControlCodes { get; private set; } = [];

    /// <summary>
    /// The minimum number of control punches a valid run must contain — every control in an ordered
    /// section counts, and an <c>[N: …]</c> block counts N (not the length of its option list). Used for
    /// the read-only "control count" column so it reflects what a runner actually has to punch.
    /// </summary>
    public int RequiredCount { get; private set; }

    /// <summary>Structural problems found while parsing (unbalanced brackets, an empty or malformed choice
    /// block, etc.), in reading order. Empty when the pattern is valid.</summary>
    public IReadOnlyList<CoursePatternError> Errors { get; private set; } = [];

    /// <summary>True when the pattern parsed without any structural error.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>The start marker shown before the order (the day's start code when supplied, else "S").</summary>
    public string StartMarker { get; private set; } = "S";

    /// <summary>The finish marker shown after the order (the day's finish code when supplied, else "F").</summary>
    public string FinishMarker { get; private set; } = "F";

    /// <summary>
    /// Parses a pattern string. A null/blank string yields an empty pattern (matches anything, count 0).
    /// Optional <paramref name="startCode"/>/<paramref name="finishCode"/> are the day's actual start/finish
    /// control codes, used only for the display markers (<see cref="StartMarker"/>/<see cref="FinishMarker"/>
    /// and <see cref="NormalizedOrder"/>); parsing/matching never requires them.
    /// </summary>
    public static CoursePattern Parse(string? text, string? startCode = null, string? finishCode = null)
    {
        var errors = new List<CoursePatternError>();
        var body = StripEdgeMarkers(text ?? string.Empty);
        var items = ParseItems(body, errors);
        var root = items.Count == 1 ? items[0] : new SequenceItem(items);

        var pattern = new CoursePattern(root);
        var codes = new List<string>();
        root.CollectCodes(codes);
        pattern.ControlCodes = codes;
        pattern.RequiredCount = root.RequiredCount;
        pattern.Errors = errors;
        if (!string.IsNullOrWhiteSpace(startCode)) pattern.StartMarker = startCode!.Trim();
        if (!string.IsNullOrWhiteSpace(finishCode)) pattern.FinishMarker = finishCode!.Trim();
        return pattern;
    }

    /// <summary>
    /// The pattern rendered back to text with the start/finish markers made explicit, e.g.
    /// <c>"S &lt;41 42&gt; [2: 45 46 47] F"</c>. Used for the live editor preview so the operator sees the
    /// effective order (start … controls … finish) even when they didn't type the markers.
    /// </summary>
    public string NormalizedOrder()
    {
        // The root is a top-level (implicit) sequence — render its children without wrapping angle brackets;
        // a single-item root renders that item directly.
        var order = _root is SequenceItem seq ? seq.InnerString() : _root.ToPatternString();
        return order.Length == 0 ? $"{StartMarker} … {FinishMarker}" : $"{StartMarker} {order} {FinishMarker}";
    }

    /// <summary>
    /// Matches the pattern against the punched codes (in punch order). Returns whether the whole pattern
    /// was satisfied; <paramref name="onCourse"/> flags, per punch, whether it was consumed by the pattern
    /// (an on-course punch); <paramref name="firstMissing"/> is the code of the first prescribed control the
    /// pattern could not satisfy (empty when complete) — shown as the MP detail.
    /// </summary>
    public bool Match(IReadOnlyList<string> punches, out bool[] onCourse, out string firstMissing) =>
        Match(punches, ignoredCodes: null, out onCourse, out firstMissing);

    /// <summary>
    /// As <see cref="Match(IReadOnlyList{string}, out bool[], out string)"/>, but any code in
    /// <paramref name="ignoredCodes"/> — the day's disabled («проблемний КП») controls — is treated as
    /// already-satisfied wherever the pattern requires it, so missing it is never an error. A punch on an
    /// ignored control is still marked on-course when it lands where the pattern expects it.
    /// </summary>
    public bool Match(
        IReadOnlyList<string> punches, IReadOnlySet<string>? ignoredCodes,
        out bool[] onCourse, out string firstMissing)
    {
        onCourse = new bool[punches.Count];
        var state = new MatchState(punches, onCourse, ignoredCodes);
        var ok = _root.Match(state);
        firstMissing = ok ? string.Empty : state.FirstMissing ?? string.Empty;
        return ok;
    }

    // ── Parsing ──────────────────────────────────────────────────────────────────────────────────────

    // Drops an optional leading start marker and trailing finish marker (S/Start… and F/Finish) from the
    // pattern body so they are not parsed as required controls. Only the very first/last bare token is
    // considered; a start/finish marker in the middle stays an ordinary control. Tokens inside a block are
    // never touched (we only look before the first '<'/'[' and after the last '>'/']').
    private static string StripEdgeMarkers(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return text;

        // Leading start marker: the first whitespace-delimited token, only if it sits before any bracket.
        var firstBreak = text.IndexOfAny([' ', '\t', '<', '[']);
        if (firstBreak > 0 && IsStartMarker(text[..firstBreak]) && text[firstBreak] is ' ' or '\t')
            text = text[(firstBreak + 1)..].TrimStart();

        // Trailing finish marker: the last token, only if it sits after any bracket.
        var lastBreak = text.LastIndexOfAny([' ', '\t', '>', ']']);
        if (lastBreak >= 0 && lastBreak < text.Length - 1
            && IsFinishMarker(text[(lastBreak + 1)..]) && text[lastBreak] is ' ' or '\t')
            text = text[..lastBreak].TrimEnd();

        return text;
    }

    // A start marker: "S"/"Start" (case-insensitive), or any non-numeric token starting with S.
    private static bool IsStartMarker(string token) =>
        token.Length > 0 && token[0] is 'S' or 's' && !ulong.TryParse(token, out _);

    // A finish marker: "F"/"Finish" (case-insensitive), or any non-numeric token starting with F.
    private static bool IsFinishMarker(string token) =>
        token.Length > 0 && token[0] is 'F' or 'f' && !ulong.TryParse(token, out _);

    // Splits a run of pattern text into top-level items (bare codes and bracketed blocks), respecting
    // nesting. Whitespace separates bare codes; '<'…'>' and '['…']' delimit blocks (matched by depth).
    private static List<IPatternItem> ParseItems(string text, List<CoursePatternError> errors)
    {
        var items = new List<IPatternItem>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c is '>' or ']')
            {
                // A closing bracket with no opener — report and skip it.
                errors.Add(new CoursePatternError(CoursePatternErrorKind.UnbalancedBracket, c.ToString()));
                i++;
                continue;
            }

            if (c is '<' or '[')
            {
                var close = c == '<' ? '>' : ']';
                var end = FindMatchingClose(text, i, c, close);
                if (end < 0)
                {
                    errors.Add(new CoursePatternError(CoursePatternErrorKind.UnbalancedBracket, c.ToString()));
                    var rest = text[(i + 1)..];
                    items.Add(c == '<' ? ParseSequenceBlock(rest, errors) : ParseAnyOfBlock(rest, errors));
                    break;
                }

                var inner = text.Substring(i + 1, end - i - 1);
                items.Add(c == '<' ? ParseSequenceBlock(inner, errors) : ParseAnyOfBlock(inner, errors));
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('<' or '[' or '>' or ']'))
                    i++;
                items.Add(new ControlItem(text[start..i]));
            }
        }
        return items;
    }

    // Index of the '>'/']' that closes the block opened at <paramref name="open"/>, honouring nesting;
    // -1 when unbalanced.
    private static int FindMatchingClose(string text, int open, char openCh, char closeCh)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == openCh) depth++;
            else if (text[i] == closeCh) { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    private static IPatternItem ParseSequenceBlock(string inner, List<CoursePatternError> errors)
    {
        var items = ParseItems(inner, errors);
        if (items.Count == 0)
            errors.Add(new CoursePatternError(CoursePatternErrorKind.EmptyOrderedBlock, "<>"));
        return new SequenceItem(items);
    }

    // "[N: item item …]": the required count is written before a colon. The colon is required — without it
    // the block is malformed (an error), so a leading number is never silently mistaken for a control.
    private static IPatternItem ParseAnyOfBlock(string inner, List<CoursePatternError> errors)
    {
        inner = inner.Trim();
        var colon = inner.IndexOf(':');
        if (colon < 0)
        {
            errors.Add(new CoursePatternError(CoursePatternErrorKind.ChoiceMissingColon, $"[{inner}]"));
            var itemsNoColon = ParseItems(inner, errors);
            return new AnyOfItem(itemsNoColon.Count, itemsNoColon);
        }

        var head = inner[..colon].Trim();
        var body = inner[(colon + 1)..];
        var items = ParseItems(body, errors);

        if (!int.TryParse(head, out var amount) || amount < 0)
        {
            errors.Add(new CoursePatternError(CoursePatternErrorKind.ChoiceBadCount, head));
            amount = items.Count;
        }
        else if (items.Count == 0)
        {
            errors.Add(new CoursePatternError(CoursePatternErrorKind.EmptyChoiceBlock, "[]"));
        }
        else if (amount > items.Count)
        {
            errors.Add(new CoursePatternError(
                CoursePatternErrorKind.ChoiceCountTooLarge, $"{amount}>{items.Count}"));
            amount = items.Count;
        }

        return new AnyOfItem(amount, items);
    }
}
