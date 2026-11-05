using System.Net;

namespace OrientPyx.DataAccess.Network.Discovery;

/// <summary>
/// The spoken fallback for finding the host when the discovery broadcast does not get through (guest Wi-Fi with
/// client isolation): the host shows a 12-character code that encodes its IPv4 address and port; the operator reads
/// it out across the hall and types it on the station. Base32 over an alphabet without <c>0/O/1/I</c>, so it is
/// unambiguous when spoken or handwritten; letters are case-insensitive; spaces, dashes and dots are ignored.
/// </summary>
public static class ConnectionCode
{
    // 32 symbols: digits 2-9 and letters without I, O (1 and 0 are also absent).
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public const int Length = 12;

    /// <summary>Encodes 4 address bytes + a 16-bit port (48 bits → 10 symbols) plus a 10-bit checksum (2 symbols).</summary>
    public static string Encode(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Only IPv4 addresses can be encoded.", nameof(address));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        var bytes = address.GetAddressBytes();
        ulong value = 0;
        foreach (var b in bytes)
            value = (value << 8) | b;
        value = (value << 16) | (uint)port;

        var checksum = Checksum(value);
        var symbols = new char[Length];
        for (var i = 9; i >= 0; i--)
        {
            symbols[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }
        symbols[10] = Alphabet[(checksum >> 5) & 31];
        symbols[11] = Alphabet[checksum & 31];
        return Format(new string(symbols));
    }

    /// <summary>Decodes a code typed by the user; null when it is malformed or its checksum does not match.</summary>
    public static (IPAddress Address, int Port)? Decode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var clean = new string(code.Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '.' or '_')).ToArray())
            .ToUpperInvariant()
            .Replace('O', '0').Replace('I', '1').Replace('L', '1');
        // Typing mistakes a reader of the code is likely to make: 0/O and 1/I/L are not in the alphabet.
        clean = clean.Replace('0', 'O').Replace('1', 'I');
        if (clean.Length != Length)
            return null;

        ulong value = 0;
        for (var i = 0; i < 10; i++)
        {
            var index = Alphabet.IndexOf(clean[i]);
            if (index < 0)
                return null;
            value = (value << 5) | (uint)index;
        }
        var c1 = Alphabet.IndexOf(clean[10]);
        var c2 = Alphabet.IndexOf(clean[11]);
        if (c1 < 0 || c2 < 0 || ((c1 << 5) | c2) != Checksum(value))
            return null;

        var port = (int)(value & 0xFFFF);
        var ip = value >> 16;
        var bytes = new[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip };
        if (port == 0)
            return null;
        return (new IPAddress(bytes), port);
    }

    /// <summary>Groups the symbols for reading aloud: <c>XXXX-XXXX-XXXX</c>.</summary>
    public static string Format(string code)
        => code.Length == Length ? $"{code[..4]}-{code[4..8]}-{code[8..]}" : code;

    // A 10-bit checksum of the 48 payload bits (sum of 5-bit groups, folded).
    private static int Checksum(ulong value)
    {
        uint sum = 0;
        for (var i = 0; i < 10; i++)
        {
            sum += (uint)(value & 31) * (uint)(i + 1);
            value >>= 5;
        }
        return (int)(sum % 1024);
    }
}
