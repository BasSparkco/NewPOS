using System.Security.Cryptography;

namespace POS.Application.Support;

/// <summary>Generates temporary passwords for newly created user accounts (admin communicates it out-of-band; no forced-change flow yet).</summary>
public static class RandomPasswordGenerator
{
    // Avoids visually ambiguous characters (0/O, 1/I/l).
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#%^&*";

    public static string Generate(int length = 14)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        Span<char> chars = length <= 256 ? stackalloc char[length] : new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return new string(chars);
    }
}
