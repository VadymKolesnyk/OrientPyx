using System.Text;
using System.Text.RegularExpressions;

namespace OrientDesk.Presentation.Services;

/// <summary>
/// Decodes raw XML bytes into a string using the encoding declared in the document's prolog
/// (<c>&lt;?xml … encoding="…"?&gt;</c>), so non-UTF-8 files — notably the windows-1251 UOF exports —
/// are read correctly on every platform. <see cref="XDocument.Parse(string)"/> works on a string and
/// ignores the declaration, so the byte→string step must honour it itself.
///
/// Cross-platform note: legacy code pages such as windows-1251 are only available once
/// <see cref="EnsureCodePagesRegistered"/> has registered <see cref="CodePagesEncodingProvider"/>
/// (done once at startup), since .NET ships only UTF/ASCII by default on Linux/macOS.
/// </summary>
public static class XmlEncodingReader
{
    private static int _registered;

    // Sniffs `encoding="..."` (or single-quoted) from the prolog. Case-insensitive on the keyword.
    private static readonly Regex EncodingAttr = new(
        "encoding\\s*=\\s*[\"']([^\"']+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Registers the legacy code-page provider (windows-1251 etc.) so <see cref="Encoding.GetEncoding(string)"/>
    /// resolves them on all platforms. Idempotent and thread-safe; call once during startup.
    /// </summary>
    public static void EnsureCodePagesRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Decodes <paramref name="bytes"/> to text. Honours a byte-order mark when present; otherwise
    /// reads the encoding declared in the XML prolog (defaulting to UTF-8 when none is declared or the
    /// declared name is unknown). Never throws for an unknown encoding — it falls back to UTF-8.
    /// </summary>
    public static string DecodeXml(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            return string.Empty;

        EnsureCodePagesRegistered();

        // A BOM is authoritative — trust it over the declaration.
        if (TryDecodeByBom(bytes, out var bomDecoded))
            return bomDecoded;

        // Read the prolog with a single-byte ASCII-compatible encoding to find the declared name.
        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 256));
        var match = EncodingAttr.Match(head);
        if (match.Success)
        {
            var encoding = TryGetEncoding(match.Groups[1].Value);
            if (encoding is not null)
                return encoding.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool TryDecodeByBom(byte[] bytes, out string decoded)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            decoded = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return true;
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            decoded = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            return true;
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            decoded = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            return true;
        }
        decoded = string.Empty;
        return false;
    }

    private static Encoding? TryGetEncoding(string name)
    {
        try
        {
            return Encoding.GetEncoding(name.Trim());
        }
        catch (ArgumentException)
        {
            return null; // unknown / unsupported name — caller falls back to UTF-8
        }
    }
}
