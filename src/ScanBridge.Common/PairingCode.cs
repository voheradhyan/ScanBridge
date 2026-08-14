using System.Security.Cryptography;
using System.Text;

namespace ScanBridge.Common;

/// <summary>
/// The written form of a pairing key — what a person copies from their PC into their Remote
/// Desktop session.
///
/// Design constraints came from who types it: somebody who is not a technician, once, probably
/// reading it off one screen and pasting it into another. So:
///
///   * Crockford's base32 alphabet, which has no I, L, O or U — so a code can never contain a
///     character that looks like another one, and there is no word to misread.
///   * Case-insensitive, and 0/O and 1/I/L are folded on the way in, because somebody will
///     retype it eventually and be sure they got it right.
///   * Grouped in fives with dashes, which are ignored when reading it back.
///
/// 160 bits of entropy, which is far beyond guessing and still short enough to fit on a line.
/// The key itself is the SHA-256 of those bytes, so the code is never stored anywhere.
/// </summary>
public static class PairingCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int EntropyBytes = 20;   // 160 bits -> 32 characters
    private const int GroupSize = 5;

    /// <summary>Bytes a PC keeps so it can show its code again. Not the key.</summary>
    public const int SeedLength = EntropyBytes;

    public static byte[] NewSeed() => RandomNumberGenerator.GetBytes(EntropyBytes);

    /// <summary>The code to show the user, for a seed this PC is holding.</summary>
    public static string Format(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return Group(Encode(seed));
    }

    /// <summary>
    /// The key both ends actually authenticate with. The PC derives it from its seed; the
    /// server derives the same value from the code, without ever holding the seed.
    /// </summary>
    public static byte[] KeyFor(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return SHA256.HashData(seed);
    }

    /// <summary>
    /// Turns a code the user typed or pasted back into the key. Throws
    /// <see cref="FormatException"/> with a message meant to be shown to them.
    /// </summary>
    public static byte[] ToKey(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        byte[] entropy = Decode(code);
        if (entropy.Length != EntropyBytes)
        {
            throw new FormatException(
                $"That pairing code is {entropy.Length * 8} bits long; a full one is " +
                $"{EntropyBytes * 8}. Check that all of it was copied.");
        }

        return SHA256.HashData(entropy);
    }

    private static string Group(string raw)
    {
        var text = new StringBuilder(raw.Length + raw.Length / GroupSize);
        for (int i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % GroupSize == 0) text.Append('-');
            text.Append(raw[i]);
        }
        return text.ToString();
    }

    private static string Encode(byte[] data)
    {
        var text = new StringBuilder();
        int buffer = 0;
        int bits = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                bits -= 5;
                text.Append(Alphabet[(buffer >> bits) & 0x1F]);
            }
        }

        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        return text.ToString();
    }

    private static byte[] Decode(string code)
    {
        var bytes = new List<byte>();
        int buffer = 0;
        int bits = 0;

        foreach (char raw in code)
        {
            if (raw is '-' or ' ' or '\t' or '\r' or '\n') continue;

            char c = Normalise(raw);
            int value = Alphabet.IndexOf(c);
            if (value < 0)
            {
                throw new FormatException(
                    $"'{raw}' is not part of a pairing code. Codes use digits and letters only, " +
                    "and never the letters I, L, O or U.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return bytes.ToArray();
    }

    /// <summary>Folds the characters people confuse, so a retyped code still works.</summary>
    private static char Normalise(char c) => char.ToUpperInvariant(c) switch
    {
        'O' => '0',
        'I' or 'L' => '1',
        'U' => 'V',
        var other => other,
    };
}
