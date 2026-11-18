namespace OrientPyx.Presentation.Services.Undo;

/// <summary>
/// Where the undo step of a change comes from, decided synchronously on the UI thread at the moment a value changes
/// (writes are debounced, so the moment of the write is too late):
/// <list type="number">
/// <item>an open <see cref="Begin"/> gesture — one action applying values synchronously (a paste, fill-down, F6, F8,
/// Delete, a page command); nested gestures join the outer one;</item>
/// <item>the open cell editing session (<see cref="OpenSession"/>, from a lazy cell's editor going live to its retire) —
/// every change of one edit is one step, even if a pause split it into two writes;</item>
/// <item>otherwise a new step per change (a checkbox, a choice outside an edit).</item>
/// </list>
/// Changes made by the program itself (a refused number put back, the second row of a chip swap, values a service
/// answered with) run under <see cref="Programmatic"/>: they join no step and do not clear «redo».
/// UI thread only — the step reaches the write through a closure, never through an async-local here.
/// </summary>
public static class UndoGesture
{
    private static Gesture? _gesture;
    private static int _programmatic;
    private static Session? _session;

    /// <summary>True while program-made changes are being applied (see <see cref="Programmatic"/>).</summary>
    public static bool IsProgrammatic => _programmatic > 0;

    /// <summary>
    /// Opens a gesture: every change captured until the scope is disposed is one undo step, labelled
    /// <paramref name="label"/> (null ⇒ the label of the first capture). Wraps only the synchronous application of
    /// values, never an <c>await</c>. A gesture opened inside another joins it.
    /// </summary>
    public static IDisposable Begin(string? label = null)
    {
        if (_gesture is { } outer)
        {
            outer.Depth++;
            return new Scope(() => outer.Depth--);
        }
        var gesture = new Gesture(label);
        _gesture = gesture;
        return new Scope(() =>
        {
            if (--gesture.Depth == 0 && ReferenceEquals(_gesture, gesture))
                _gesture = null;
        });
    }

    /// <summary>
    /// Keeps the captures made until disposed in one step without naming it — for one change that fans out to several
    /// values (a merged roster cell writing every day). Inside an open gesture it joins that gesture; inside a live
    /// cell edit it does nothing (the session already makes every change of the edit one step — a fresh gesture here
    /// would split a typed value into a step per keystroke); otherwise it is <see cref="Begin"/> with the label of the
    /// first capture.
    /// </summary>
    public static IDisposable Group()
    {
        if (_gesture is null && _session is { IsOpen: true })
            return new Scope(() => { });
        return Begin();
    }

    /// <summary>
    /// A gesture that continues a step captured earlier in the same user action: every change captured until disposed
    /// joins <paramref name="step"/> (e.g. a dialog that already wrote part of its action — a region made with
    /// «+ новий» — before its values are applied to the rows). A null or already undone step ⇒ <see cref="Begin"/>.
    /// Inside another gesture it joins that one, as <see cref="Begin"/> does.
    /// </summary>
    public static IDisposable Continue(UndoEntry? step, string? label = null)
    {
        var scope = Begin(label);
        if (step is { Closed: false } && _gesture is { Entry: null, Depth: 1 } gesture)
            gesture.Entry = step;
        return scope;
    }

    /// <summary>Program-made changes until disposed: their writes go outside every step and never clear «redo».</summary>
    public static IDisposable Programmatic()
    {
        _programmatic++;
        return new Scope(() => _programmatic--);
    }

    /// <summary>
    /// A cell's editor went live: until <see cref="CloseSession"/> every change is one step. <paramref name="commit"/>
    /// ends the edit as Enter would (the value stays) — undo calls it before reverting. <paramref name="isText"/> marks
    /// a text editor, whose own undo handles Ctrl+Z while it is focused.
    /// </summary>
    public static Session OpenSession(string? label, Action commit, bool isText)
    {
        var session = new Session(label, commit, isText);
        _session = session;
        return session;
    }

    /// <summary>The editor retired: its session takes no more changes.</summary>
    public static void CloseSession(Session? session)
    {
        if (session is null)
            return;
        session.IsOpen = false;
        session.Commit = null;
        if (ReferenceEquals(_session, session))
            _session = null;
    }

    /// <summary>
    /// Ends the live cell edit, as Enter would, before an undo/redo. A text edit is left alone unless
    /// <paramref name="includeText"/> (the menus close any edit; the keys never reach a focused text box).
    /// </summary>
    public static void CommitLiveSession(bool includeText)
    {
        if (_session is not { IsOpen: true } session || (session.IsText && !includeText))
            return;
        var commit = session.Commit;
        CloseSession(session);
        commit?.Invoke();
    }

    /// <summary>The gesture or session a capture joins right now (null ⇒ a new step per change).</summary>
    internal static StepSlot? CurrentSlot
        => _gesture is { } gesture ? gesture : _session is { IsOpen: true } session ? session : null;

    /// <summary>A gesture or session: the label it gives its step and the history entry it captured (if any).</summary>
    public abstract class StepSlot
    {
        protected StepSlot(string? label) => Label = label;

        public string? Label { get; }

        internal UndoEntry? Entry { get; set; }
    }

    private sealed class Gesture : StepSlot
    {
        public Gesture(string? label) : base(label)
        {
        }

        public int Depth { get; set; } = 1;
    }

    /// <summary>One cell editing session (held by the lazy cell while its editor is live).</summary>
    public sealed class Session : StepSlot
    {
        internal Session(string? label, Action commit, bool isText) : base(label)
        {
            Commit = commit;
            IsText = isText;
        }

        internal Action? Commit { get; set; }

        internal bool IsText { get; }

        internal bool IsOpen { get; set; } = true;
    }

    private sealed class Scope : IDisposable
    {
        private Action? _dispose;

        public Scope(Action dispose) => _dispose = dispose;

        public void Dispose()
        {
            var dispose = _dispose;
            _dispose = null;
            dispose?.Invoke();
        }
    }
}
