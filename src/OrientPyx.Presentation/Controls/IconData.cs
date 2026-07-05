using System.Collections.Generic;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// The subset of <a href="https://lucide.dev">Lucide</a> icon geometries used across the app, keyed by
/// Lucide's icon name. These are the raw SVG <c>path</c> "d" strings from the Lucide project (MIT licensed),
/// each on the standard 24×24 canvas and meant to be *stroked* (round caps/joins, width 2) — see
/// <see cref="Icon"/> which draws them. We embed only what we use rather than take the Avalonia Lucide NuGet
/// package, because those packages target Avalonia 11 while this app runs on Avalonia 12 (see the note on
/// <see cref="Icon"/>).
///
/// To add an icon: copy its <c>path d="…"</c> from https://lucide.dev/icons/&lt;name&gt; and add an entry here.
/// </summary>
public static class IconData
{
    public static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        // ---- Import / export / files -----------------------------------------------------------------
        // upload: tray with an up-arrow (used for the UOF-file and generic import actions)
        ["Upload"] = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M17 8l-5-5-5 5 M12 3v12",
        // download: tray with a down-arrow (export)
        ["Download"] = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3",
        // file-spreadsheet: a document with a grid (CSV / Excel import)
        ["FileSpreadsheet"] = "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M14 2v4a2 2 0 0 0 2 2h4 M8 13h2 M14 13h2 M8 17h2 M14 17h2",
        // file-text: a document with text lines (protocols / generic page)
        ["FileText"] = "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M14 2v4a2 2 0 0 0 2 2h4 M16 13H8 M16 17H8 M10 9H8",
        // file: plain document (page fallback)
        ["File"] = "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M14 2v4a2 2 0 0 0 2 2h4",
        // folder-open: an opened folder (competition list / open)
        ["FolderOpen"] = "m6 14 1.45-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.55 6a2 2 0 0 1-1.94 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2",

        // ---- Bulk / list actions ---------------------------------------------------------------------
        // list-ordered: numbered rows (assign start numbers)
        ["ListOrdered"] = "M10 12h11 M10 6h11 M10 18h11 M4 10V6l-2 1 M4 18a1.5 1.5 0 0 0 0-3 1.5 1.5 0 0 0-1.3.8 M2.5 18h1.5",
        // credit-card: chip card (assign chips)
        ["CreditCard"] = "M2 5h20a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H2a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z M1 10h22 M6 15h4",
        // arrow-up-down: reorder (manual start order)
        ["ArrowUpDown"] = "m21 16-4 4-4-4 M17 20V4 M3 8l4-4 4 4 M7 4v16",
        // square-pen (edit): a pencil over a card (bulk edit a field)
        ["SquarePen"] = "M12 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7 M18.375 2.625a1 1 0 0 1 3 3l-9.013 9.014a2 2 0 0 1-.853.505l-2.873.84a.5.5 0 0 1-.62-.62l.84-2.873a2 2 0 0 1 .506-.852z",
        // pencil: plain edit
        ["Pencil"] = "M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z M15 5l4 4",
        // triangle-alert: warning triangle (mark age violators / out of competition)
        ["TriangleAlert"] = "m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3 M12 9v4 M12 17h.01",

        // ---- Add / delete / confirm ------------------------------------------------------------------
        // plus (add)
        ["Plus"] = "M5 12h14 M12 5v14",
        // trash-2 (delete row)
        ["Trash"] = "M3 6h18 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2 M10 11v6 M14 11v6",
        // check (valid / done)
        ["Check"] = "M20 6 9 17l-5-5",
        // x (invalid / clear)
        ["X"] = "M18 6 6 18 M6 6l12 12",

        // ---- Navigation chevrons ---------------------------------------------------------------------
        ["ChevronDown"] = "m6 9 6 6 6-6",
        ["ChevronUp"] = "m18 15-6-6-6 6",
        ["ChevronRight"] = "m9 18 6-6-6-6",
        ["ChevronLeft"] = "m15 18-6-6 6-6",

        // ---- Page-nav / dashboard glyphs -------------------------------------------------------------
        // layout-dashboard (dashboard tiles)
        ["LayoutDashboard"] = "M3 3h7v9H3z M14 3h7v5h-7z M14 12h7v9h-7z M3 16h7v5H3z",
        // clock (start / draw)
        ["Clock"] = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z M12 6v6l4 2",
        // list (simple list page)
        ["List"] = "M3 5h18 M3 12h18 M3 19h18",
        // table (grid / summary)
        ["Table"] = "M3 3h18v18H3z M3 9h18 M9 9v12 M3 15h18",
        // bar-chart (splits / stats)
        ["BarChart"] = "M4 20V10 M10 20V4 M16 20v-6 M4 20h18",
        // users (participants)
        ["Users"] = "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z M22 21v-2a4 4 0 0 0-3-3.87 M16 3.13a4 4 0 0 1 0 7.75",
        // flag (control points / КП)
        ["Flag"] = "M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z M4 22v-7",
        // map-pin (control point / region)
        ["MapPin"] = "M20 10c0 6-8 12-8 12s-8-6-8-12a8 8 0 0 1 16 0z M12 8a2 2 0 1 0 0 4 2 2 0 0 0 0-4z",

        // ---- Misc UI ---------------------------------------------------------------------------------
        // refresh-cw (auto-update / republish)
        ["RefreshCw"] = "M21 12a9 9 0 0 0-9-9 9.75 9.75 0 0 0-6.74 2.74L3 8 M3 3v5h5 M3 12a9 9 0 0 0 9 9 9.75 9.75 0 0 0 6.74-2.74L21 16 M21 21v-5h-5",
        // circle-help (help "?")
        ["CircleHelp"] = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3 M12 17h.01",
        // info
        ["Info"] = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z M12 16v-4 M12 8h.01",
        // circle-alert (error message)
        ["CircleAlert"] = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z M12 8v4 M12 16h.01",
        // eye (visible / preview)
        ["Eye"] = "M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7z M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z",
        // eye-off (hidden)
        ["EyeOff"] = "M10.73 5.08A10.4 10.4 0 0 1 12 5c7 0 10 7 10 7a13 13 0 0 1-1.67 2.68 M6.6 6.6C3.6 8.3 2 12 2 12s3 7 10 7a9.7 9.7 0 0 0 5.4-1.6 M14.1 14.1a3 3 0 1 1-4.2-4.2 M2 2l20 20",
        // external-link (open frontend)
        ["ExternalLink"] = "M15 3h6v6 M10 14 21 3 M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6",
        // tag (chips / labels)
        ["Tag"] = "M2 9.5V4a2 2 0 0 1 2-2h5.5a2 2 0 0 1 1.41.59l9 9a2 2 0 0 1 0 2.82l-5.5 5.5a2 2 0 0 1-2.82 0l-9-9A2 2 0 0 1 2 9.5z M7 7h.01",
        // calendar (days)
        ["Calendar"] = "M8 2v4 M16 2v4 M3 6h18v15a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z M3 10h18",
        // search (magnifier — table search box)
        ["Search"] = "m21 21-4.34-4.34 M11 17a6 6 0 1 0 0-12 6 6 0 0 0 0 12z",
        // copy (copy link)
        ["Copy"] = "M8 4h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z M4 16a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2",
        // loader-circle (spinner — rotated by an animation at the call site)
        ["LoaderCircle"] = "M21 12a9 9 0 1 1-6.219-8.56",
        // activity (running background processes badge)
        ["Activity"] = "M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2",
        // grip-vertical (drag handle)
        ["GripVertical"] = "M9 5a1 1 0 1 0 0 2 1 1 0 0 0 0-2z M9 11a1 1 0 1 0 0 2 1 1 0 0 0 0-2z M9 17a1 1 0 1 0 0 2 1 1 0 0 0 0-2z M15 5a1 1 0 1 0 0 2 1 1 0 0 0 0-2z M15 11a1 1 0 1 0 0 2 1 1 0 0 0 0-2z M15 17a1 1 0 1 0 0 2 1 1 0 0 0 0-2z",
    };
}
