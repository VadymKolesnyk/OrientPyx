using System.Text;

namespace OrientDesk.Presentation.Services;

/// <summary>
/// Decodes raw CSV bytes into text. A CSV has no in-band encoding declaration, so we: trust a
/// byte-order mark when present; otherwise try strict UTF-8 and, if the bytes are not valid UTF-8,
/// fall back to windows-1251 (the legacy Ukrainian/Cyrillic code page these exports often use). The
/// windows-1251 provider is registered the same way the XML reader does it.
/// </summary>
public static class CsvEncodingReader
{
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            return string.Empty;

        // A BOM is authoritative.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // No BOM: try strict UTF-8; on invalid bytes, fall back to windows-1251.
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            XmlEncodingReader.EnsureCodePagesRegistered();
            try
            {
                return Encoding.GetEncoding("windows-1251").GetString(bytes);
            }
            catch (ArgumentException)
            {
                // Provider unavailable — last resort, lenient UTF-8 (replacement chars over a throw).
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }
}
