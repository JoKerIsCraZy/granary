namespace Granary.Core.Remote;

/// <summary>
/// RFC 4648 base32 in upper case without padding: the form the pairing link carries the key in (silo-roku's
/// <c>sealed.bs</c> <c>base32</c>, silo-android's <c>SiloCastSealed.base32</c>).
/// </summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int BitsPerDigit = 5;

    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        char[] text = new char[((bytes.Length * 8) + BitsPerDigit - 1) / BitsPerDigit];
        int written = 0;
        int buffer = 0;
        int bits = 0;
        foreach (byte value in bytes)
        {
            buffer = ((buffer << 8) | value) & 0xFFFF;
            bits += 8;
            while (bits >= BitsPerDigit)
            {
                bits -= BitsPerDigit;
                text[written++] = Alphabet[(buffer >> bits) & 31];
            }
        }
        if (bits > 0)
        {
            text[written++] = Alphabet[(buffer << (BitsPerDigit - bits)) & 31];
        }
        return new string(text, 0, written);
    }

    /// <summary>
    /// The bytes of <paramref name="text"/> in either case, or null for a character outside the alphabet, padding, or a
    /// length or last digit no encoding gives.
    /// </summary>
    public static byte[]? Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes = new byte[text.Length * BitsPerDigit / 8];
        int written = 0;
        int buffer = 0;
        int bits = 0;
        foreach (char character in text)
        {
            int digit = Alphabet.IndexOf(char.ToUpperInvariant(character), StringComparison.Ordinal);
            if (digit < 0)
            {
                return null;
            }
            buffer = ((buffer << BitsPerDigit) | digit) & 0xFFFF;
            bits += BitsPerDigit;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[written++] = (byte)((buffer >> bits) & 0xFF);
            }
        }
        // Whole bytes only: what is left over is less than a digit and all zeros, as Encode leaves it.
        bool leftoverIsPadding = bits < BitsPerDigit && (buffer & ((1 << bits) - 1)) == 0;
        return leftoverIsPadding ? bytes : null;
    }
}
