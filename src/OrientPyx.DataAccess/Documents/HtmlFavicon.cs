using System.Text;

namespace OrientPyx.DataAccess.Documents;

/// <summary>
/// The app icon (purple tile + white/orange control-point diamond, same as <c>Assets/orientpyx.png</c>) as an
/// inline SVG favicon for the exported HTML pages. Embedded as a <c>data:</c> URI so the file stays stand-alone
/// and the icon shows in the browser tab even offline.
/// </summary>
internal static class HtmlFavicon
{
    private const string Svg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 256 256\">" +
        "<rect width=\"256\" height=\"256\" rx=\"48\" fill=\"#7c3aed\"/>" +
        "<path d=\"M128 51 51 128l77 77z\" fill=\"#fff\"/>" +
        "<path d=\"M128 51l77 77-77 77z\" fill=\"#ff6600\"/>" +
        "</svg>";

    /// <summary>The ready <c>&lt;link rel="icon"&gt;</c> tag (with trailing newline) for the page head.</summary>
    public static readonly string LinkTag =
        "<link rel=\"icon\" type=\"image/svg+xml\" href=\"data:image/svg+xml;base64," +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Svg)) + "\">\n";
}
